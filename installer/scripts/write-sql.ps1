# Fill the password placeholders in a .sql template and write the result to a
# temp file for mysql.exe to consume.
#
# Serves both init-db.sql (needs __APPPWD__ and __ROPWD__) and
# init-db-embedded.sql (needs only __APPPWD__), so RoPwd is optional.
#
# .Replace() rather than -replace, and here it genuinely matters: these values
# are PASSWORDS. With -replace, a password containing "$1" or "$&" would be
# interpreted as a capture-group reference and the account would silently end up
# with a different password than the one written into the config files --
# the application would then fail to connect for no visible reason.

param(
    [Parameter(Mandatory = $true)][string]$Template,
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][AllowEmptyString()][string]$AppPwd,
    [AllowEmptyString()][string]$RoPwd = ''
)

$ErrorActionPreference = 'Stop'

try {
    if (-not (Test-Path -LiteralPath $Template)) {
        Write-Host "[ERROR] SQL template not found: $Template"
        exit 1
    }

    $s = Get-Content -LiteralPath $Template -Raw -Encoding UTF8
    $s = $s.Replace('__APPPWD__', $AppPwd)
    $s = $s.Replace('__ROPWD__',  $RoPwd)

    # UTF8 to match the --default-character-set=utf8mb4 that mysql.exe is
    # invoked with; the templates carry comments that may be non-ASCII.
    Set-Content -LiteralPath $Output -Value $s -Encoding UTF8

    if (-not (Test-Path -LiteralPath $Output)) {
        Write-Host "[ERROR] SQL file was not written: $Output"
        exit 2
    }

    exit 0
}
catch {
    Write-Host "[ERROR] Failed to write SQL file: $($_.Exception.Message)"
    exit 3
}
