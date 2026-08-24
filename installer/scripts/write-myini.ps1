# Generate my.ini from my.ini.template by filling in port / basedir / datadir.
#
# Uses .Replace() rather than PowerShell's -replace operator on purpose:
# -replace treats its first argument as a regex and, more importantly, treats
# "$1", "$&" etc. in the REPLACEMENT as capture-group references. A Windows
# path containing a dollar sign (an admin share like \\host\c$\... is the usual
# way this shows up) would then be silently mangled. .Replace() is a plain
# literal substitution with no such surprises.

param(
    [Parameter(Mandatory = $true)][string]$Template,
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][string]$Port,
    [Parameter(Mandatory = $true)][string]$BaseDir,
    [Parameter(Mandatory = $true)][string]$DataDir
)

$ErrorActionPreference = 'Stop'

try {
    if (-not (Test-Path -LiteralPath $Template)) {
        Write-Host "[ERROR] Template not found: $Template"
        exit 1
    }

    $t = Get-Content -LiteralPath $Template -Raw -Encoding UTF8

    # MySQL wants forward slashes in my.ini paths
    $t = $t.Replace('__PORT__',    $Port)
    $t = $t.Replace('__BASEDIR__', $BaseDir.Replace('\', '/'))
    $t = $t.Replace('__DATADIR__', $DataDir.Replace('\', '/'))

    # ASCII on purpose: my.ini is read by mysqld before any character set is
    # negotiated, and the paths here are ASCII anyway.
    Set-Content -LiteralPath $Output -Value $t -Encoding ASCII

    if (-not (Test-Path -LiteralPath $Output)) {
        Write-Host "[ERROR] my.ini was not written: $Output"
        exit 2
    }

    exit 0
}
catch {
    Write-Host "[ERROR] Failed to write my.ini: $($_.Exception.Message)"
    exit 3
}
