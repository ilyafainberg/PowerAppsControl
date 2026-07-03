@echo off
setlocal EnableExtensions EnableDelayedExpansion
:: ============================================================================
::  apply-update.cmd — update applier for PowerAppsControl.
::
::  Runs as a SEPARATE process from the (locked) exe so it can replace it.
::  Launched by Updater.SpawnApplyHelper with:
::    %1 = PID to wait for   %2 = "portable"|"installer"
::    %3 = downloaded asset  %4 = install dir   %5 = exe name
::
::  Flow: wait for PID → apply (extract-over for portable / silent setup for
::  installer) → re-register the server (portable path) → clean up.
:: ============================================================================

set "APP_PID=%~1"
set "KIND=%~2"
set "ASSET=%~3"
set "INSTALL_DIR=%~4"
set "APP_EXE=%~5"

echo Waiting for PowerAppsControl (PID %APP_PID%) to close...
set /a _tries=0
:waitloop
tasklist /FI "PID eq %APP_PID%" 2>nul | find "%APP_PID%" >nul
if not errorlevel 1 (
    set /a _tries+=1
    if !_tries! geq 120 goto :giveup
    ping -n 2 127.0.0.1 >nul
    goto :waitloop
)

if /I "%KIND%"=="installer" goto :installer

:: --- PORTABLE: extract the zip over the install directory -------------------
echo Applying portable update...
set "STAGE=%TEMP%\pac-stage-%RANDOM%"
mkdir "%STAGE%" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Expand-Archive -LiteralPath '%ASSET%' -DestinationPath '%STAGE%' -Force"
if errorlevel 1 goto :fail
robocopy "%STAGE%" "%INSTALL_DIR%" /E /IS /IT /NFL /NDL /NJH /NJS /R:2 /W:1 >nul
if %ERRORLEVEL% GEQ 8 goto :fail
:: Refresh MCP registration + companion skill from the new build.
"%INSTALL_DIR%\%APP_EXE%" --register --quiet
echo Update complete. Restart your MCP host (Scout / Copilot CLI) to load it.
goto :cleanup

:: --- INSTALLER: extract setup.exe and run it silently -----------------------
:installer
echo Applying installer update...
set "STAGE=%TEMP%\pac-stage-%RANDOM%"
mkdir "%STAGE%" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Expand-Archive -LiteralPath '%ASSET%' -DestinationPath '%STAGE%' -Force"
if errorlevel 1 goto :fail
for %%F in ("%STAGE%\*setup*.exe") do (
    "%%~fF" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
    goto :cleanup
)
goto :fail

:cleanup
if defined STAGE rmdir /S /Q "%STAGE%" >nul 2>&1
del /Q "%ASSET%" >nul 2>&1
endlocal
exit /b 0

:giveup
echo Timed out waiting for the app to close. Aborting update.
endlocal
exit /b 1

:fail
echo Update failed. Your existing install was left in place. Re-download the
echo latest release from https://github.com/ilyafainberg/PowerAppsControl/releases
if defined STAGE rmdir /S /Q "%STAGE%" >nul 2>&1
endlocal
exit /b 1
