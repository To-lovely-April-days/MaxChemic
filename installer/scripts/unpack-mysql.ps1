# Unpack the MySQL zip into the install directory.
#
# The archive contains a single top-level folder (mysql-8.0.x-winx64\); we lift
# its contents one level up so that bin\ lib\ share\ sit directly under mysql\.
#
# Why this lives in a .ps1 instead of inline in the .bat:
# the inline form needed multi-line "^" continuations with nested double quotes,
# single quotes and parentheses. cmd parses that soup itself before PowerShell
# ever sees it, and a path containing a space -- which "C:\Program Files\..."
# always does -- was enough to make it fall apart with an unhelpful syntax
# error. Passing named parameters to a real script file removes cmd from the
# equation entirely: it only has to handle one level of quoting.

param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$Destination
)

$ErrorActionPreference = 'Stop'

try {
    if (-not (Test-Path -LiteralPath $ZipPath)) {
        Write-Host "[ERROR] Zip not found: $ZipPath"
        exit 1
    }

    $tmp = Join-Path $Destination '_tmp'
    if (Test-Path -LiteralPath $tmp) {
        Remove-Item -LiteralPath $tmp -Recurse -Force
    }

    Expand-Archive -LiteralPath $ZipPath -DestinationPath $tmp -Force

    $inner = Get-ChildItem -LiteralPath $tmp -Directory | Select-Object -First 1
    if ($null -eq $inner) {
        Write-Host "[ERROR] The archive has no top-level folder. Is this really the MySQL ZIP Archive?"
        exit 2
    }

    Get-ChildItem -LiteralPath $inner.FullName | Move-Item -Destination $Destination -Force
    Remove-Item -LiteralPath $tmp -Recurse -Force

    exit 0
}
catch {
    Write-Host "[ERROR] Unpack failed: $($_.Exception.Message)"
    exit 3
}
