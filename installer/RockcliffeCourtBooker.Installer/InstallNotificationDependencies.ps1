[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FrameworkPackagePath,

    [Parameter(Mandatory)]
    [string]$SingletonPackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Install-PackageIfRequired {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required Windows App Runtime package is missing: $Path"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.GetEntry("AppxManifest.xml")
        if ($null -eq $manifestEntry) {
            throw "The Windows App Runtime package has no AppxManifest.xml: $Path"
        }

        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $identity = $manifest.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
        if ($null -eq $identity) {
            throw "The Windows App Runtime package identity could not be read: $Path"
        }

        $packageName = $identity.GetAttribute("Name")
        $requiredVersion = [Version]$identity.GetAttribute("Version")
        $requiredArchitecture = $identity.GetAttribute("ProcessorArchitecture")
    }
    finally {
        $archive.Dispose()
    }

    $installed = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Where-Object {
        [Version]$_.Version -ge $requiredVersion -and
        [string]::Equals($_.Architecture.ToString(), $requiredArchitecture, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($installed.Count -eq 0) {
        Add-AppxPackage -Path $Path -ErrorAction Stop
    }
}

# The Singleton package has a declared dependency on the matching framework.
Install-PackageIfRequired -Path $FrameworkPackagePath
Install-PackageIfRequired -Path $SingletonPackagePath
