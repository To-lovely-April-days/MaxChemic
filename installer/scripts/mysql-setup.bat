@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM  MaxChemical database deployment (called by the installer; can be re-run
REM  by hand as Administrator).
REM
REM  Usage:
REM   mysql-setup.bat <mode> <appdir> <zip> <host> <port> <admin_user>
REM                   <admin_pwd> <app_pwd> <ro_pwd>
REM
REM  Modes:
REM   embedded - unpack the zip, init the data dir, register a private service,
REM              then create the database and accounts
REM   existing - skip all of the above, only create the database and accounts
REM              on the customer's own MySQL/MariaDB instance
REM
REM  Both modes unpack MySQL into the install dir: "existing" still needs a
REM  version-matched mysql.exe client to run the init script (the customer's
REM  machine may not have one, or it may be a different version). Costs ~400MB
REM  of disk, buys a SINGLE code path -- fewer branches, fewer things to go
REM  wrong, which is worth it when you cannot debug each site by hand.
REM
REM  Switching to MariaDB: just swap the zip. Every flag used here
REM  (--initialize-insecure / --install / --defaults-file) is compatible.
REM
REM  ---------------------------------------------------------------------------
REM  THIS FILE MUST STAY PURE ASCII. Do not add Chinese (or any non-ASCII)
REM  text, and do not add "chcp".
REM
REM  cmd re-seeks the file BY BYTE OFFSET every time it runs "goto"/"call
REM  :label". This script has several jumps (:createdb, :waitloop, :ready).
REM  With multi-byte characters in the file, the offsets land in the middle of
REM  a line and commands get sliced into fragments -- you get garbage like
REM  "'<mojibake>' is not recognized as an internal or external command".
REM  That is exactly what happened on a Traditional-Chinese test machine.
REM
REM  Encoding cannot be fixed by picking "the right" code page either: GBK
REM  works only on Simplified Chinese, Big5 only on Traditional, and the
REM  customer may run an English or Japanese Windows. ASCII is single-byte
REM  under every code page, so it is the only choice that always aligns --
REM  and the log stays readable on any machine.
REM
REM  NO INLINE POWERSHELL EITHER. The PowerShell work lives in three sibling
REM  .ps1 files (unpack-mysql / write-myini / write-sql) and is invoked with
REM  -File plus named parameters, one call per line.
REM
REM  The previous version inlined it with multi-line "^" continuations holding
REM  nested double quotes, single quotes and parentheses. cmd parses all of
REM  that itself before PowerShell sees a byte of it, and an install path with
REM  a space in it -- "C:\Program Files\..." always has one -- was enough to
REM  break it, reporting only a bare syntax error about a stray character.
REM  With -File, cmd has exactly one level of quoting to get right.
REM
REM  NO PARENTHESES IN ANY ECHO THAT SITS INSIDE AN IF/ELSE BLOCK. cmd closes
REM  the block at the first ")" it sees, even inside an echo argument; whatever
REM  follows becomes a separate command and you get "X was unexpected at this
REM  time" pointing at a character that looks entirely innocent.
REM
REM  This cost a full debugging round: "[4/6] ... skipping (existing data kept)."
REM  blew up the step-4 block, and because it failed while cmd was PARSING, the
REM  echo never printed -- the log stopped after [3/6] and pointed the blame at
REM  the wrong line entirely. Use a dash or a comma instead. (build.bat has the
REM  same rule but can use full-width parens; this file cannot, it must stay ASCII.)
REM ============================================================================

set "MODE=%~1"
set "APPDIR=%~2"
set "ZIPPATH=%~3"
set "DBHOST=%~4"
set "DBPORT=%~5"
set "ADMUSER=%~6"
set "ADMPWD=%~7"
set "APPPWD=%~8"
set "ROPWD=%~9"

set "SVCNAME=MaxChemicalMySQL"
set "MYSQLDIR=%APPDIR%\mysql"
set "DATADIR=%MYSQLDIR%\data"
set "MYINI=%MYSQLDIR%\my.ini"
set "MYSQLEXE=%MYSQLDIR%\bin\mysql.exe"

echo [%date% %time%] === MaxChemical database deployment START ===
REM  Bump this whenever this script changes in a way worth telling apart in a
REM  log. Without it there is no way to know from the log alone whether the
REM  machine is running the script you just fixed or a stale copy from an
REM  older installer build -- which has already cost a full debugging round.
echo   Script:    2026-08-24-c  [no parens in blocks; per-step exit codes]
echo   Mode:      %MODE%
echo   Target:    %DBHOST%:%DBPORT%
echo   Install:   %APPDIR%

if "%APPDIR%"=="" ( echo [ERROR] Missing argument: install dir & exit /b 1 )
if "%DBPORT%"=="" ( echo [ERROR] Missing argument: port & exit /b 1 )

REM An empty admin password must NOT be passed as "-p" -- a bare -p turns into
REM an interactive prompt and hangs the whole installation.
if "%ADMPWD%"=="" (
    set "PWDARG="
) else (
    set "PWDARG=-p%ADMPWD%"
)

REM ---- 1. Unpack MySQL (both modes, see header) -----------------------------
if exist "%MYSQLEXE%" (
    echo [1/6] MySQL binaries already present, skipping unpack.
) else (
    echo [1/6] Unpacking MySQL, about 1 minute ...
    if not exist "%MYSQLDIR%" mkdir "%MYSQLDIR%"
    REM The zip has a single top folder mysql-8.0.x-winx64\ -- lift its contents up
    powershell -NoProfile -ExecutionPolicy Bypass -File "%APPDIR%\scripts\unpack-mysql.ps1" -ZipPath "%ZIPPATH%" -Destination "%MYSQLDIR%"
    echo       unpack-mysql.ps1 returned !errorlevel!
    if not exist "%MYSQLEXE%" (
        echo [ERROR] mysql.exe not found after unpacking.
        echo         Make sure the file is the Windows x64 "ZIP Archive", not the MSI installer.
        exit /b 2
    )
)

REM ---- existing mode: jump straight to creating the database ----------------
if /i "%MODE%"=="existing" (
    echo [2-5/6] Using the existing instance, skipping init and service setup.
    goto :createdb
)

REM ---- 2. Remove a leftover service from a previous install ------------------
sc query "%SVCNAME%" >nul 2>&1
if %errorlevel%==0 (
    echo [2/6] Found existing service %SVCNAME%, stopping and removing it ...
    net stop "%SVCNAME%" >nul 2>&1
    "%MYSQLDIR%\bin\mysqld.exe" --remove "%SVCNAME%" >nul 2>&1
    sc delete "%SVCNAME%" >nul 2>&1
    REM Service deletion is asynchronous -- give the OS time to release handles
    timeout /t 3 /nobreak >nul
) else (
    echo [2/6] No existing service found, continuing.
)

REM ---- 3. Generate my.ini ---------------------------------------------------
echo [3/6] Writing my.ini ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%APPDIR%\scripts\write-myini.ps1" -Template "%APPDIR%\scripts\my.ini.template" -Output "%MYINI%" -Port "%DBPORT%" -BaseDir "%MYSQLDIR%" -DataDir "%DATADIR%"
set "RC=%errorlevel%"
echo       write-myini.ps1 returned %RC%
if not "%RC%"=="0" exit /b 8
if not exist "%MYINI%" echo [ERROR] my.ini was not created at %MYINI% & exit /b 8

REM ---- 4. Initialize the data directory (first run only) --------------------
REM  --initialize-insecure creates a passwordless root; step 6 sets its password
REM  immediately after. Initializing WITH a password would make MySQL write a
REM  random one into the error log which we would then have to parse back out --
REM  more moving parts, more to break. The window is a few seconds and the
REM  server is not listening yet.
if exist "%DATADIR%\mysql" (
    echo [4/6] Data directory already initialized, skipping - existing data kept.
) else (
    echo [4/6] Initializing data directory, about 30 seconds ...
    if exist "%DATADIR%" rmdir /s /q "%DATADIR%"
    "%MYSQLDIR%\bin\mysqld.exe" --defaults-file="%MYINI%" --initialize-insecure --console
    if not exist "%DATADIR%\mysql" (
        echo [ERROR] Data directory initialization failed. See the mysqld output above.
        echo         If there is no output at all, mysqld could not start -- almost
        echo         always a missing Visual C++ runtime - vcruntime140_1.dll.
        exit /b 3
    )
)

REM ---- 5. Register and start the service -------------------------------------
echo [5/6] Registering Windows service %SVCNAME% and starting it ...
"%MYSQLDIR%\bin\mysqld.exe" --install "%SVCNAME%" --defaults-file="%MYINI%"
if errorlevel 1 (
    echo [ERROR] Failed to register the service - needs Administrator rights.
    exit /b 4
)
sc config "%SVCNAME%" start= auto >nul
net start "%SVCNAME%"
if errorlevel 1 (
    echo [ERROR] Service failed to start. Usual cause: port %DBPORT% already in use.
    echo         Reinstall with a different port, or read mysql-error.log under %DATADIR%.
    exit /b 5
)

:createdb

REM ---- Wait until the server answers ----------------------------------------
REM  A freshly started service may not have its socket ready yet, and an
REM  existing instance may simply be busy. Polling beats sleeping a fixed time.
echo       Waiting for the database to accept connections ...
set /a WAIT=0
:waitloop
"%MYSQLEXE%" -u %ADMUSER% %PWDARG% -P %DBPORT% -h %DBHOST% -e "SELECT 1" >nul 2>&1
if %errorlevel%==0 goto ready
set /a WAIT+=1
if %WAIT% geq 30 (
    echo [ERROR] Timed out after 30 seconds. Check:
    echo         - host/port correct: %DBHOST%:%DBPORT%
    echo         - admin account and password correct: %ADMUSER%
    echo         - that account may log in from this machine
    exit /b 6
)
timeout /t 1 /nobreak >nul
goto waitloop
:ready
echo       Database is ready.

REM ---- 6. Create the database and the two accounts ---------------------------
REM  init-db.sql uses placeholders; substitute them here.
REM  Passwords never touch the install dir: the temp file goes to %TEMP% and is
REM  deleted right after it runs.
echo [6/6] Creating database and accounts: maxchemical (read-write), mcp_ro (read-only) ...
set "TMPSQL=%TEMP%\maxchemical-init-%RANDOM%%RANDOM%.sql"
powershell -NoProfile -ExecutionPolicy Bypass -File "%APPDIR%\scripts\write-sql.ps1" -Template "%APPDIR%\scripts\init-db.sql" -Output "%TMPSQL%" -AppPwd "%APPPWD%" -RoPwd "%ROPWD%"
set "RC=%errorlevel%"
echo       write-sql.ps1 returned %RC%
if not "%RC%"=="0" exit /b 9

"%MYSQLEXE%" -u %ADMUSER% %PWDARG% -P %DBPORT% -h %DBHOST% --default-character-set=utf8mb4 < "%TMPSQL%"
set "SQLRC=%errorlevel%"
del /f /q "%TMPSQL%" >nul 2>&1
if not "%SQLRC%"=="0" (
    echo [ERROR] The init script failed. Most likely the admin account lacks
    echo         CREATE DATABASE or CREATE USER privileges.
    exit /b 7
)

REM ---- embedded only: close the passwordless-root window ----------------------
REM  Kept in a separate file rather than inside init-db.sql so that "never touch
REM  the customer's root password" is guaranteed by the code path, not by a
REM  comment reminding someone not to run it.
if /i "%MODE%"=="embedded" (
    echo       Setting the root password on the embedded instance ...
    set "TMPSQL2=%TEMP%\maxchemical-root-%RANDOM%%RANDOM%.sql"
    powershell -NoProfile -ExecutionPolicy Bypass -File "%APPDIR%\scripts\write-sql.ps1" -Template "%APPDIR%\scripts\init-db-embedded.sql" -Output "!TMPSQL2!" -AppPwd "%APPPWD%"
    "%MYSQLEXE%" -u %ADMUSER% %PWDARG% -P %DBPORT% -h %DBHOST% --default-character-set=utf8mb4 < "!TMPSQL2!"
    del /f /q "!TMPSQL2!" >nul 2>&1
    REM root's password just changed, later commands need the new one
    set "PWDARG=-p%APPPWD%"
)

REM ---- Import the base schema. DOE tables are created by the app on startup --
if exist "%APPDIR%\Maxchemical.sql" (
    echo       Importing base schema ...
    "%MYSQLEXE%" -u %ADMUSER% %PWDARG% -P %DBPORT% -h %DBHOST% --default-character-set=utf8mb4 maxchemical < "%APPDIR%\Maxchemical.sql" 2>nul
    REM On reinstall the tables already exist and this errors out. That is
    REM expected, so it is not treated as a failure.
)

echo [%date% %time%] === Deployment COMPLETE ===
if /i "%MODE%"=="embedded" echo   Service:  %SVCNAME%
echo   Database: maxchemical @ %DBHOST%:%DBPORT%
echo   Accounts: maxchemical (read-write) / mcp_ro (read-only)

REM Success marker. The installer reads this log in ssPostInstall and looks for
REM this exact line to decide whether deployment really succeeded -- [Run] does
REM not check exit codes, so without this a mid-script failure would still show
REM "installation successful".
echo MAXCHEMICAL_DB_SETUP_OK
exit /b 0
