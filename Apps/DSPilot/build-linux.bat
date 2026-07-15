@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul 2>&1
title DSPilot Linux Package Builder

echo ============================================
echo   DSPilot Linux Package Builder (win-host)
echo ============================================
echo.

:: Wrapper that runs Installer\linux\build-linux.sh via Git Bash on Windows.
:: build-linux.sh supports Windows-hosted builds (CRLF normalization built in;
:: exec bits are granted by install.sh on the target machine).
:: CCTV-less build: set SKIP_MEDIAMTX=1 before calling (passed through as-is).
:: NOTE: keep this file ASCII-only. cmd.exe misparses batch files containing
:: multibyte UTF-8 text when the console codepage is already 65001 (e.g. when
:: called from another script or re-run in the same window), executing line
:: fragments as commands. The chcp above is only for displaying the UTF-8
:: output of build-linux.sh.
set "SOLUTION_DIR=%~dp0"
set "OUTPUT_DIR=%SOLUTION_DIR%Output"
set "SH_FILE=%SOLUTION_DIR%Installer\linux\build-linux.sh"

:: Locate Git Bash (WSL's System32\bash.exe is not usable here: dotnet/path issues)
set "GIT_BASH="
if exist "%ProgramFiles%\Git\bin\bash.exe" set "GIT_BASH=%ProgramFiles%\Git\bin\bash.exe"
if not defined GIT_BASH if exist "%ProgramFiles(x86)%\Git\bin\bash.exe" set "GIT_BASH=%ProgramFiles(x86)%\Git\bin\bash.exe"
if not defined GIT_BASH if exist "%LocalAppData%\Programs\Git\bin\bash.exe" set "GIT_BASH=%LocalAppData%\Programs\Git\bin\bash.exe"
if not defined GIT_BASH (
    for /f "delims=" %%i in ('where git.exe 2^>nul') do (
        if not defined GIT_BASH if exist "%%~dpi..\bin\bash.exe" set "GIT_BASH=%%~dpi..\bin\bash.exe"
    )
)
if not defined GIT_BASH goto :no_bash

:: Git Bash handles C:/foo/bar paths reliably, so just flip the backslashes.
set "SH_FILE=%SH_FILE:\=/%"

echo Git Bash : %GIT_BASH%
echo Script   : %SH_FILE%
echo.

"%GIT_BASH%" "%SH_FILE%"
if !errorlevel! neq 0 goto :fail_build

echo.
echo ============================================
echo   Linux Package Complete
echo ============================================
echo.
set "TARBALL="
for /f "delims=" %%i in ('dir /b /o-d "%OUTPUT_DIR%\DSPilot_linux-x64_*.tar.gz" 2^>nul') do if not defined TARBALL set "TARBALL=%%i"
if defined TARBALL echo   Package: %OUTPUT_DIR%\%TARBALL%
echo.
goto :end

:no_bash
echo [ERROR] Git Bash (bash.exe) not found.
echo         Install Git for Windows: https://git-scm.com/download/win
goto :error

:fail_build
echo.
echo [ERROR] build-linux.sh failed. See errors above.
goto :error

:error
echo.
echo ============================================
echo   Build FAILED. See errors above.
echo ============================================
echo.
:: NOPAUSE=1 (non-interactive callers such as build-all.bat) skips the pause.
if not defined NOPAUSE pause
exit /b 1

:end
:: On success open the Output folder with the tarball selected (skip if NOPAUSE).
if not defined NOPAUSE (
    if defined TARBALL (
        start "" explorer.exe /select,"%OUTPUT_DIR%\%TARBALL%"
    ) else if exist "%OUTPUT_DIR%" (
        start "" explorer.exe "%OUTPUT_DIR%"
    )
)
if not defined NOPAUSE pause
exit /b 0
