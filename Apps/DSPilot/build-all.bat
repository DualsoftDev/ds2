@echo off
setlocal enabledelayedexpansion
title DSPilot Build All (Windows + Linux)

:: NOTE: keep this file ASCII-only (no chcp needed then) - see build-linux.bat.
::
:: Child scripts (build-installer.bat contains UTF-8 Korean comments) must be
:: opened while the console is on its ORIGINAL codepage: opening them under an
:: inherited chcp 65001 makes cmd.exe lose line boundaries at multibyte bytes
:: and execute line fragments as commands. Children switch to 65001 themselves,
:: so restore the original codepage right before each call.
for /f "tokens=2 delims=:" %%i in ('chcp') do set "ORIG_CP=%%i"
set "ORIG_CP=%ORIG_CP: =%"
if not defined ORIG_CP set "ORIG_CP=949"

set "SOLUTION_DIR=%~dp0"
set "OUTPUT_DIR=%SOLUTION_DIR%Output"

:: Suppress pause/explorer inside the child builds. Remember whether WE were
:: called with NOPAUSE so build-all's own final pause still behaves correctly.
set "OUTER_NOPAUSE=%NOPAUSE%"
set "NOPAUSE=1"

echo ============================================
echo   DSPilot Build All (Windows + Linux)
echo ============================================
echo.

:: Order matters: build-installer.bat wipes the Output folder at start, so
:: build Windows first, then add the Linux tarball into the same Output.
echo [1/2] Windows installer (build-installer.bat)...
echo --------------------------------------------
chcp %ORIG_CP% >nul 2>&1
call "%SOLUTION_DIR%build-installer.bat"
if !errorlevel! neq 0 goto :fail_win
echo.

echo [2/2] Linux package (build-linux.bat)...
echo --------------------------------------------
chcp %ORIG_CP% >nul 2>&1
call "%SOLUTION_DIR%build-linux.bat"
if !errorlevel! neq 0 goto :fail_linux
echo.

:: Success
chcp %ORIG_CP% >nul 2>&1
echo ============================================
echo   All Builds Complete
echo ============================================
echo.
echo   Output: %OUTPUT_DIR%
dir /b "%OUTPUT_DIR%" 2>nul
echo.
goto :end

:fail_win
echo.
echo [ERROR] Windows installer build failed - Linux build skipped.
goto :error

:fail_linux
echo.
echo [ERROR] Linux package build failed. (Windows installer is already in Output)
goto :error

:error
chcp %ORIG_CP% >nul 2>&1
echo.
echo ============================================
echo   Build FAILED. See errors above.
echo ============================================
echo.
if not defined OUTER_NOPAUSE pause
exit /b 1

:end
if not defined OUTER_NOPAUSE (
    if exist "%OUTPUT_DIR%" start "" explorer.exe "%OUTPUT_DIR%"
    pause
)
exit /b 0
