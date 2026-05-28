@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul 2>&1
title DSPilot Build ^& Installer

echo ============================================
echo   DSPilot Build ^& Installer Generator
echo ============================================
echo.

:: Configuration
set "SOLUTION_DIR=%~dp0"
set "PROJECT_DIR=%SOLUTION_DIR%DSPilot"
set "PUBLISH_DIR=%SOLUTION_DIR%publish"
set "OUTPUT_DIR=%SOLUTION_DIR%Output"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "ISS_FILE=%SOLUTION_DIR%Installer\DSPilot.iss"
set "MTX_DIR=%SOLUTION_DIR%Installer\mediamtx"
:: CCTV — 번들할 외부 바이너리 버전 (필요 시 갱신)
set "MTX_VERSION=v1.9.3"
set "WINSW_VERSION=v2.12.0"

:: Check Inno Setup
if not exist "%ISCC%" goto :no_iscc

:: Step 1: Clean previous build
echo [1/4] Cleaning previous build...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
echo       Done.
echo.

:: Step 2: Restore packages
echo [2/4] Restoring NuGet packages...
dotnet restore "%SOLUTION_DIR%DSPilot.sln" --verbosity quiet
if !errorlevel! neq 0 goto :fail_restore

echo       Done.
echo.

:: Step 3: Publish DSPilot (self-contained)
echo [3/4] Publishing DSPilot (self-contained, win-x64)...
dotnet publish "%PROJECT_DIR%\DSPilot.csproj" -c Release -r win-x64 --self-contained true -o "%PUBLISH_DIR%" -p:PublishSingleFile=false -p:IncludeAllContentForSelfExtract=true -m:1
if !errorlevel! neq 0 goto :fail_publish

echo       Done.
echo.

:: Step 3b: Fetch CCTV binaries (MediaMTX + WinSW) into Installer\mediamtx
echo [3b] Preparing CCTV binaries (MediaMTX + WinSW)...
if not exist "%MTX_DIR%" mkdir "%MTX_DIR%"

if not exist "%MTX_DIR%\mediamtx.exe" (
    echo       Downloading MediaMTX %MTX_VERSION%...
    powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $u='https://github.com/bluenviron/mediamtx/releases/download/%MTX_VERSION%/mediamtx_%MTX_VERSION%_windows_amd64.zip'; $z='%MTX_DIR%\mtx.zip'; Invoke-WebRequest -Uri $u -OutFile $z; Expand-Archive -Path $z -DestinationPath '%MTX_DIR%' -Force; Remove-Item $z"
    if !errorlevel! neq 0 goto :fail_cctv
) else (
    echo       MediaMTX already present, skipping.
)

if not exist "%MTX_DIR%\mediamtx-service.exe" (
    echo       Downloading WinSW %WINSW_VERSION%...
    powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $u='https://github.com/winsw/winsw/releases/download/%WINSW_VERSION%/WinSW-x64.exe'; Invoke-WebRequest -Uri $u -OutFile '%MTX_DIR%\mediamtx-service.exe'"
    if !errorlevel! neq 0 goto :fail_cctv
) else (
    echo       WinSW already present, skipping.
)
echo       Done.
echo.

:: Step 4: Build installer with Inno Setup
echo [4/4] Building installer with Inno Setup...
"%ISCC%" "%ISS_FILE%"
if !errorlevel! neq 0 goto :fail_iscc

echo       Done.
echo.

:: Success
echo ============================================
echo   Build Complete!
echo ============================================
echo.
for /f "tokens=*" %%i in ('powershell -NoProfile -Command "(Get-Item '%PUBLISH_DIR%\DSPilot.exe').VersionInfo.FileVersion"') do set "APP_VER=%%i"
echo   Installer: %OUTPUT_DIR%\DSPilot_Setup_%APP_VER%.exe
echo.
goto :end

:no_iscc
echo [ERROR] Inno Setup 6 not found: %ISCC%
echo         Download from https://jrsoftware.org/isdl.php
goto :error

:fail_restore
echo [ERROR] dotnet restore failed.
goto :error

:fail_publish
echo [ERROR] dotnet publish (DSPilot) failed.
goto :error

:fail_cctv
echo [ERROR] Failed to download CCTV binaries (MediaMTX / WinSW).
echo         Check network, or place mediamtx.exe + mediamtx-service.exe in %MTX_DIR% manually.
goto :error

:fail_iscc
echo [ERROR] Inno Setup compilation failed.
goto :error

:error
echo.
echo ============================================
echo   Build FAILED. See errors above.
echo ============================================
echo.
pause
exit /b 1

:end
pause
exit /b 0
