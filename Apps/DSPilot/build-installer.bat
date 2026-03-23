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

:: Step 3: Publish (self-contained)
echo [3/4] Publishing DSPilot (self-contained, win-x64)...
dotnet publish "%PROJECT_DIR%\DSPilot.csproj" -c Release -r win-x64 --self-contained true -o "%PUBLISH_DIR%" -p:PublishSingleFile=false -p:IncludeAllContentForSelfExtract=true
if !errorlevel! neq 0 goto :fail_publish

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
echo   Installer: %OUTPUT_DIR%\DSPilot_Setup_1.0.0.exe
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
echo [ERROR] dotnet publish failed.
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
