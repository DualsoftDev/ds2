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
:: CCTV - bundled external binary versions (update as needed)
:: v1.10.0+ required for H.265 over WebRTC (camera H.265 streams play in supporting browsers)
set "MTX_VERSION=v1.19.1"
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
    rem Extract only mediamtx.exe - the zip's stock mediamtx.yml must NOT clobber our customized (committed) yml.
    powershell -NoProfile -Command "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $u='https://github.com/bluenviron/mediamtx/releases/download/%MTX_VERSION%/mediamtx_%MTX_VERSION%_windows_amd64.zip'; $z='%MTX_DIR%\mtx.zip'; $d='%MTX_DIR%\mtx-tmp'; Invoke-WebRequest -Uri $u -OutFile $z; Expand-Archive -Path $z -DestinationPath $d -Force; Copy-Item (Join-Path $d 'mediamtx.exe') '%MTX_DIR%\mediamtx.exe' -Force; Remove-Item $d -Recurse -Force; Remove-Item $z"
    if !errorlevel! neq 0 goto :fail_cctv
) else (
    echo       MediaMTX already present, skipping.
)

rem Use WinSW-net461 build: depends only on Windows 10/11 in-box .NET Framework 4.8,
rem so it works on an offline target PC with no .NET Core runtime installed.
if not exist "%MTX_DIR%\mediamtx-service.exe" (
    echo       Downloading WinSW %WINSW_VERSION% ^(net461^)...
    powershell -NoProfile -Command "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $u='https://github.com/winsw/winsw/releases/download/%WINSW_VERSION%/WinSW.NET461.exe'; Invoke-WebRequest -Uri $u -OutFile '%MTX_DIR%\mediamtx-service.exe'"
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
