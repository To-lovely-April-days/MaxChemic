@echo off
setlocal

REM ============================================================================
REM  Remove the embedded MySQL service on uninstall (called by [UninstallRun]).
REM
REM  Deliberately does NOT delete the data directory: experiment data belongs to
REM  the customer and must not disappear just because the software was removed.
REM  Anyone who wants a clean wipe can delete the install folder afterwards.
REM
REM  Pure ASCII on purpose, and no "chcp" -- same reason as mysql-setup.bat:
REM  cmd seeks by byte offset for goto/call, and a mixed-encoding batch file
REM  breaks in ways that are very hard to diagnose. This one has no jumps today,
REM  but keeping both scripts under the same rule means nobody has to remember
REM  which of them is allowed to contain non-ASCII text.
REM ============================================================================

set "APPDIR=%~1"
set "SVCNAME=MaxChemicalMySQL"
set "MYSQLDIR=%APPDIR%\mysql"

echo Stopping and removing database service %SVCNAME% ...

sc query "%SVCNAME%" >nul 2>&1
if not %errorlevel%==0 (
    echo Service %SVCNAME% not found, nothing to do.
    exit /b 0
)

net stop "%SVCNAME%" >nul 2>&1

if exist "%MYSQLDIR%\bin\mysqld.exe" (
    "%MYSQLDIR%\bin\mysqld.exe" --remove "%SVCNAME%" >nul 2>&1
)
REM Fall back to sc if mysqld --remove fails, e.g. binaries already deleted
sc delete "%SVCNAME%" >nul 2>&1

echo Service removed. Data directory kept: %MYSQLDIR%\data
exit /b 0
