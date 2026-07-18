[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MsiPath,

    [Parameter(Mandatory)]
    [string]$DtfAssemblyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$resolvedDtfAssemblyPath = (Resolve-Path -LiteralPath $DtfAssemblyPath).Path
Add-Type -Path $resolvedDtfAssemblyPath

$database = [WixToolset.Dtf.WindowsInstaller.Database]::new(
    $resolvedMsiPath,
    [WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::Transact)
$neutralizedCount = 0
$clearedCount = 0
try {
    $view = $database.OpenView("SELECT ``File``, ``Version``, ``Language`` FROM ``File``")
    try {
        $view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            try {
                $version = $record.GetString(2)
                $language = $record.GetString(3)
                if (-not [string]::IsNullOrEmpty($version) -and $language -ne "0") {
                    # This MSI is a single language-neutral application whose
                    # payload lives entirely in its private per-user directory.
                    # Chromium and some .NET PEs expose malformed or non-LANGID
                    # resource values; using MSI's documented neutral LANGID for
                    # every versioned payload file gives deterministic upgrade
                    # rules without changing runtime resource selection.
                    $record.SetString(3, "0")
                    $view.Modify(
                        [WixToolset.Dtf.WindowsInstaller.ViewModifyMode]::Update,
                        $record)
                    $neutralizedCount++
                }
                elseif ([string]::IsNullOrEmpty($version) -and
                    -not [string]::IsNullOrEmpty($language)) {
                    # This app has no localized type-library/help-file payload;
                    # its unversioned files should carry no MSI language metadata.
                    # DTF maps an empty string to a null MSI field.
                    $record.SetString(3, "")
                    $view.Modify(
                        [WixToolset.Dtf.WindowsInstaller.ViewModifyMode]::Update,
                        $record)
                    $clearedCount++
                }
            }
            finally {
                $record.Dispose()
            }
        }
    }
    finally {
        $view.Dispose()
    }

    $database.Commit()
}
finally {
    $database.Dispose()
}

Write-Host "Marked $neutralizedCount versioned payload file(s) as language-neutral (LANGID 0)."
Write-Host "Cleared $clearedCount language value(s) from unversioned files."
