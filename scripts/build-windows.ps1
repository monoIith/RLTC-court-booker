[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Configuration = "Release",

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "1.0.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The self-contained MSI must be produced on Windows x64."
}
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw "The self-contained MSI must be produced on a Windows x64 host."
}

$versionParts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw "Version must fit the Windows Installer major.minor.build limits (255.255.65535)."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repositoryRoot "artifacts"
$appPublish = Join-Path $artifacts "app-win-x64"
$workerPublish = Join-Path $artifacts "worker-win-x64"
$staging = Join-Path $artifacts "windows-x64"
$workerStaging = Join-Path $staging "worker"
$installerOutput = Join-Path $artifacts "installer"
$appProject = Join-Path $repositoryRoot "src/RockcliffeCourtBooker.App/RockcliffeCourtBooker.App.csproj"
$automationTestProject = Join-Path $repositoryRoot "tests/RockcliffeCourtBooker.Automation.Tests/RockcliffeCourtBooker.Automation.Tests.csproj"
$workerProject = Join-Path $repositoryRoot "src/RockcliffeCourtBooker.Worker/RockcliffeCourtBooker.Worker.csproj"
$installerProject = Join-Path $repositoryRoot "installer/RockcliffeCourtBooker.Installer/RockcliffeCourtBooker.Installer.wixproj"
$installerSource = Join-Path $repositoryRoot "installer/RockcliffeCourtBooker.Installer/Package.wxs"
$installerDirectory = Split-Path -Parent $installerProject
$msiLanguageNormalizer = Join-Path $installerDirectory "NormalizeMsiFileLanguages.ps1"
$notificationAssets = Join-Path $repositoryRoot "src/RockcliffeCourtBooker.Notifications/obj/project.assets.json"

function Reset-ArtifactDirectory {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Copy-PublishTree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $resolvedSource = (Resolve-Path -LiteralPath $Source).Path
    foreach ($file in Get-ChildItem -LiteralPath $resolvedSource -Recurse -File) {
        $relativePath = [IO.Path]::GetRelativePath($resolvedSource, $file.FullName)
        $destinationPath = Join-Path $Destination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        if (-not (Test-Path -LiteralPath $destinationDirectory)) {
            New-Item -ItemType Directory -Path $destinationDirectory | Out-Null
        }

        if (Test-Path -LiteralPath $destinationPath) {
            $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
            if ($sourceHash -ne $destinationHash) {
                throw "App and worker publish outputs contain different files at '$relativePath'. Use distinct names or align their dependency versions."
            }

            continue
        }

        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath
    }
}

function Assert-PackagedFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release file was not produced: $Path"
    }
}

function Assert-CustomActionTargetLengths {
    param([Parameter(Mandatory)][string]$Path)

    [xml]$source = Get-Content -LiteralPath $Path -Raw
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($source.NameTable)
    $namespaceManager.AddNamespace("wix", "http://wixtoolset.org/schemas/v4/wxs")

    foreach ($setProperty in $source.SelectNodes("//wix:SetProperty", $namespaceManager)) {
        # SetProperty rows that schedule a same-named custom action are stored in
        # CustomAction.Target, an MSI Formatted column limited to 255 characters.
        $target = $setProperty.GetAttribute("Value")
        if ($target.Length -gt 255) {
            $id = $setProperty.GetAttribute("Id")
            throw "Custom action target '$id' exceeds MSI's 255-character limit."
        }
    }
}

function Resolve-MsBuildProperty {
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$PropertyName
    )

    $propertyOutput = @(& dotnet msbuild $ProjectPath `
        "-getProperty:$PropertyName" `
        -nologo `
        -verbosity:quiet)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve MSBuild property '$PropertyName' for $ProjectPath."
    }

    $values = @($propertyOutput | ForEach-Object { $_.Trim() } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
    if ($values.Count -ne 1) {
        throw "MSBuild returned $($values.Count) values for '$PropertyName'; expected exactly one."
    }

    return $values[0]
}

foreach ($directory in @($appPublish, $workerPublish, $staging, $installerOutput)) {
    Reset-ArtifactDirectory -Path $directory
}

Assert-CustomActionTargetLengths -Path $installerSource

Push-Location $repositoryRoot
try {
    dotnet restore RockcliffeCourtBooker.slnx
    if ($LASTEXITCODE -ne 0) { throw "Solution restore failed." }

    if (-not (Test-Path -LiteralPath $notificationAssets -PathType Leaf)) {
        throw "Notification project restore assets were not produced."
    }

    $assets = Get-Content -LiteralPath $notificationAssets -Raw | ConvertFrom-Json -AsHashtable
    $runtimeLibrary = $assets["libraries"].GetEnumerator() | Where-Object {
        $_.Key -like "Microsoft.WindowsAppSDK.Runtime/*"
    } | Select-Object -First 1
    if ($null -eq $runtimeLibrary) {
        throw "The pinned Microsoft.WindowsAppSDK.Runtime package was not found in restore assets."
    }

    $runtimeMsixDirectory = $null
    foreach ($packageRoot in $assets["packageFolders"].Keys) {
        $candidate = Join-Path $packageRoot (Join-Path $runtimeLibrary.Value["path"] "tools/MSIX/win10-x64")
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $runtimeMsixDirectory = $candidate
            break
        }
    }
    if ($null -eq $runtimeMsixDirectory) {
        throw "The pinned Windows App Runtime MSIX directory was not found in any restored NuGet package root."
    }
    $prerequisiteDirectory = Join-Path $staging "prerequisites"
    New-Item -ItemType Directory -Path $prerequisiteDirectory | Out-Null
    foreach ($packageName in @("Microsoft.WindowsAppRuntime.2.msix", "Microsoft.WindowsAppRuntime.Singleton.2.msix")) {
        $sourcePackage = Join-Path $runtimeMsixDirectory $packageName
        Assert-PackagedFile -Path $sourcePackage
        Copy-Item -LiteralPath $sourcePackage -Destination (Join-Path $prerequisiteDirectory $packageName)
    }

    dotnet build RockcliffeCourtBooker.slnx --configuration $Configuration --no-restore `
        -p:WindowsAppSDKSelfContained=false
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed." }

    # Playwright's generated installer script expects Microsoft.Playwright.dll
    # beside it. Executable test output contains both; a class-library output does not.
    $automationOutput = Join-Path (Split-Path -Parent $automationTestProject) "bin/$Configuration/net10.0"
    $playwrightScript = Join-Path $automationOutput "playwright.ps1"
    Assert-PackagedFile -Path $playwrightScript

    # Install the pinned browser once, use that exact tree for deterministic
    # fixture tests, then carry the same tree into the MSI staging directory.
    $browserDirectory = Join-Path $staging "ms-playwright"
    $previousBrowserPath = [Environment]::GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", "Process")
    $previousPlaywrightTestFlag = [Environment]::GetEnvironmentVariable("ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS", "Process")
    try {
        [Environment]::SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", $browserDirectory, "Process")
        & $playwrightScript install chromium
        if ($LASTEXITCODE -ne 0) {
            throw "Playwright Chromium installation failed with exit code $LASTEXITCODE."
        }

        [Environment]::SetEnvironmentVariable("ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS", "1", "Process")
        dotnet test RockcliffeCourtBooker.slnx --configuration $Configuration --no-restore --no-build
        if ($LASTEXITCODE -ne 0) { throw "Automated tests failed." }
    }
    finally {
        [Environment]::SetEnvironmentVariable("ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS", $previousPlaywrightTestFlag, "Process")
        [Environment]::SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", $previousBrowserPath, "Process")
    }

    dotnet restore $appProject --runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw "App win-x64 restore failed." }

    dotnet restore $workerProject --runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw "Worker win-x64 restore failed." }

    dotnet publish $appProject `
        --configuration $Configuration --runtime win-x64 --self-contained true --no-restore `
        --output $appPublish `
        -p:Version=$Version -p:DebugSymbols=false -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

    dotnet publish $workerProject `
        --configuration $Configuration --runtime win-x64 --self-contained true --no-restore `
        --output $workerPublish `
        -p:Version=$Version -p:DebugSymbols=false -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw "Worker publish failed." }

    Assert-PackagedFile -Path (Join-Path $appPublish "RockcliffeCourtBooker.exe")
    Assert-PackagedFile -Path (Join-Path $workerPublish "RockcliffeCourtBooker.Worker.exe")
    Assert-PackagedFile -Path (Join-Path $workerPublish "playwright.ps1")
    Assert-PackagedFile -Path (Join-Path $prerequisiteDirectory "Microsoft.WindowsAppRuntime.2.msix")
    Assert-PackagedFile -Path (Join-Path $prerequisiteDirectory "Microsoft.WindowsAppRuntime.Singleton.2.msix")

    # Keep the independently self-contained WPF app and Windows App SDK worker in
    # distinct directories. Their framework closures can legitimately contain
    # different files with the same runtime filename (for example WindowsBase.dll),
    # so flattening them would either corrupt one application or make valid builds
    # fail depending on publish ordering.
    Copy-PublishTree -Source $appPublish -Destination $staging
    Copy-PublishTree -Source $workerPublish -Destination $workerStaging

    $chromiumExecutables = @(Get-ChildItem -LiteralPath $browserDirectory -Recurse -File -Filter "chrome.exe")
    if ($chromiumExecutables.Count -lt 1) {
        throw "The bundled Playwright Chromium executable was not found under $browserDirectory."
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        productVersion = $Version
        runtimeIdentifier = "win-x64"
        appExecutable = "RockcliffeCourtBooker.exe"
        workerExecutable = "worker/RockcliffeCourtBooker.Worker.exe"
        playwrightBrowserDirectory = "ms-playwright"
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging "release-manifest.json") -Encoding utf8

    $unexpectedConfiguration = @(Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object {
        $_.Name -in @("account.json", "config.json")
    })
    if ($unexpectedConfiguration.Count -gt 0) {
        throw "A real account/configuration JSON must never be included in release artifacts."
    }

    dotnet build $installerProject `
        --configuration $Configuration `
        --output $installerOutput `
        -p:PublishDirectory=$staging `
        -p:ProductVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed." }
}
finally {
    Pop-Location
}

$msiFiles = @(Get-ChildItem -LiteralPath $installerOutput -Recurse -File -Filter "*.msi")
if ($msiFiles.Count -ne 1) {
    throw "Expected exactly one MSI in $installerOutput, but found $($msiFiles.Count)."
}

$msi = $msiFiles[0]
$wixPdbFiles = @(Get-ChildItem -LiteralPath $installerOutput -Recurse -File -Filter "*.wixpdb")
if ($wixPdbFiles.Count -ne 1) {
    throw "Expected exactly one WiX PDB in $installerOutput, but found $($wixPdbFiles.Count)."
}
$wixPdb = $wixPdbFiles[0]

$wixToolDirectory = Resolve-MsBuildProperty `
    -ProjectPath $installerProject `
    -PropertyName "WixBinDir"
$dtfAssembly = Join-Path $wixToolDirectory "WixToolset.Dtf.WindowsInstaller.dll"
$wixTool = Join-Path $wixToolDirectory "wix.dll"
Assert-PackagedFile -Path $msiLanguageNormalizer
Assert-PackagedFile -Path $dtfAssembly
Assert-PackagedFile -Path $wixTool

# WiX faithfully copies PE version-resource language metadata, including invalid
# values embedded in a few third-party Chromium/.NET binaries. Normalize those
# values to MSI's versioned/unversioned rules, then re-run the complete MSI
# validation suite. This keeps ICE03/ICE60 effective on the final database.
& $msiLanguageNormalizer -MsiPath $msi.FullName -DtfAssemblyPath $dtfAssembly

dotnet exec --roll-forward Major $wixTool msi validate $msi.FullName `
    -pdb $wixPdb.FullName `
    -sice ICE38 `
    -sice ICE64 `
    -sice ICE91
if ($LASTEXITCODE -ne 0) { throw "Normalized MSI validation failed." }

$msiHash = (Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
"$msiHash  $($msi.Name)" | Set-Content -LiteralPath "$($msi.FullName).sha256" -Encoding ascii

Write-Host "Created $($msi.FullName)"
Write-Host "SHA-256 $msiHash"
