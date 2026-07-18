[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw "Full verification requires Windows x64."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$automationTestProject = Join-Path $repositoryRoot "tests/RockcliffeCourtBooker.Automation.Tests/RockcliffeCourtBooker.Automation.Tests.csproj"
$browserDirectory = Join-Path $repositoryRoot "artifacts/verify-ms-playwright"

Push-Location $repositoryRoot
try {
    dotnet restore RockcliffeCourtBooker.slnx
    if ($LASTEXITCODE -ne 0) { throw "Solution restore failed." }

    dotnet build RockcliffeCourtBooker.slnx --configuration Release --no-restore `
        -p:WindowsAppSDKSelfContained=false
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed." }

    $playwrightScript = Join-Path (Split-Path -Parent $automationTestProject) "bin/Release/net10.0/playwright.ps1"
    if (-not (Test-Path -LiteralPath $playwrightScript -PathType Leaf)) {
        throw "The Playwright installer script was not produced."
    }

    if (Test-Path -LiteralPath $browserDirectory) {
        Remove-Item -LiteralPath $browserDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $browserDirectory | Out-Null

    $previousBrowserPath = [Environment]::GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", "Process")
    $previousPlaywrightTestFlag = [Environment]::GetEnvironmentVariable("ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS", "Process")
    try {
        [Environment]::SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", $browserDirectory, "Process")
        & $playwrightScript install chromium
        if ($LASTEXITCODE -ne 0) { throw "Playwright Chromium installation failed." }

        [Environment]::SetEnvironmentVariable("ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS", "1", "Process")
        dotnet test RockcliffeCourtBooker.slnx --configuration Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Automated tests failed." }
    }
    finally {
        [Environment]::SetEnvironmentVariable("ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS", $previousPlaywrightTestFlag, "Process")
        [Environment]::SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", $previousBrowserPath, "Process")
    }
}
finally {
    Pop-Location
}
