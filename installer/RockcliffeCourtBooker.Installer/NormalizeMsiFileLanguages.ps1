[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MsiPath,

    [Parameter(Mandatory)]
    [string]$DtfAssemblyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-MsiLanguageField {
    param([AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrEmpty($Value) -or $Value.Length -gt 20) {
        return $false
    }

    foreach ($part in $Value.Split(',')) {
        if ($part -notmatch '^\d{1,5}$') {
            return $false
        }

        $languageId = 0
        if (-not [int]::TryParse($part, [ref]$languageId) -or $languageId -gt 65535) {
            return $false
        }

        # ICE03 requires a registered language identifier, not merely an integer
        # that fits in a 16-bit LANGID. Zero is MSI's neutral-language sentinel;
        # every other value must resolve through the Windows/.NET culture table.
        if ($languageId -ne 0) {
            try {
                $culture = [System.Globalization.CultureInfo]::GetCultureInfo($languageId)
                if ($culture.LCID -ne $languageId) {
                    return $false
                }
            }
            catch [System.Globalization.CultureNotFoundException] {
                return $false
            }
        }
    }

    return $true
}

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
                if (-not [string]::IsNullOrEmpty($version) -and
                    -not (Test-MsiLanguageField -Value $language)) {
                    # Chromium and some .NET runtime PEs expose a blank, malformed,
                    # or overlong language list. LANGID 0 is Windows Installer's
                    # documented value for a language-neutral versioned file.
                    $record.SetString(3, "0")
                    $view.Modify(
                        [WixToolset.Dtf.WindowsInstaller.ViewModifyMode]::Update,
                        $record)
                    $neutralizedCount++
                }
                elseif ([string]::IsNullOrEmpty($version) -and
                    -not [string]::IsNullOrEmpty($language)) {
                    # ICE60 requires the language column to be null when the file
                    # has no version. DTF maps an empty string to a null MSI field.
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

Write-Host "Normalized $neutralizedCount invalid versioned-file language value(s) to LANGID 0."
Write-Host "Cleared $clearedCount language value(s) from unversioned files."
