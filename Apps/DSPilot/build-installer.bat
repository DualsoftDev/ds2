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
set "PUBLISH_TRAY_DIR=%SOLUTION_DIR%publish-tray"
set "OUTPUT_DIR=%SOLUTION_DIR%Output"
set "TRAY_PROJECT_DIR=%SOLUTION_DIR%DSPilot.Tray"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "ISS_FILE=%SOLUTION_DIR%Installer\DSPilot.iss"

:: Check Inno Setup
if not exist "%ISCC%" goto :no_iscc

:: Step 1: Clean previous build
echo [1/5] Cleaning previous build...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%PUBLISH_TRAY_DIR%" rmdir /s /q "%PUBLISH_TRAY_DIR%"
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
echo       Done.
echo.

:: Step 2: Restore packages
echo [2/5] Restoring NuGet packages...
dotnet restore "%SOLUTION_DIR%DSPilot.sln" --verbosity quiet
if !errorlevel! neq 0 goto :fail_restore

echo       Done.
echo.

:: Step 3: Publish DSPilot (self-contained)
echo [3/5] Publishing DSPilot (self-contained, win-x64)...
dotnet publish "%PROJECT_DIR%\DSPilot.csproj" -c Release -r win-x64 --self-contained true -o "%PUBLISH_DIR%" -p:PublishSingleFile=false -p:IncludeAllContentForSelfExtract=true
if !errorlevel! neq 0 goto :fail_publish

echo       Done.
echo.

:: Step 4: Publish DSPilot.Tray (self-contained)
echo [4/5] Publishing DSPilot.Tray (self-contained, win-x64)...
dotnet publish "%TRAY_PROJECT_DIR%\DSPilot.Tray.csproj" -c Release -r win-x64 --self-contained true -o "%PUBLISH_TRAY_DIR%" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if !errorlevel! neq 0 goto :fail_publish_tray

echo       Done.
echo.

:: Step 5: Build installer with Inno Setup
echo [5/5] Building installer with Inno Setup...
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

:fail_publish_tray
echo [ERROR] dotnet publish (DSPilot.Tray) failed.
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
