@echo off
setlocal enabledelayedexpansion
title DSPilot Lite Build ^& Installer

:: NOTE: keep this file ASCII-only (see build-all.bat about chcp 65001 misparse).
::
:: DSPilot *Lite* installer - small download variant, built SEPARATELY from
:: build-installer.bat (own publish-*-lite / Output-lite folders; the standard
:: build's publish/, publish-agent/, publish-collector/, Output/ are untouched).
::
:: How it gets small:
::   1) framework-dependent publish (no bundled .NET runtime) for DSPilot,
::      Promaker.Agent and Ds2.Collector  -> ~70 MB less raw per app.
::   2) ffmpeg (100 MB) / MediaMTX (55 MB) / WinSW are NOT bundled. The
::      installer downloads them at install time (optional "cctv" task),
::      together with the ASP.NET Core 9 runtime when the target PC lacks it.
:: Everything else (services, firewall, port page, secrets) is the same
:: DSPilot.iss compiled with /DLite - see the #ifdef Lite blocks there.

echo ============================================
echo   DSPilot LITE Build ^& Installer Generator
echo ============================================
echo.

set "SOLUTION_DIR=%~dp0"
set "PROJECT_DIR=%SOLUTION_DIR%DSPilot"
set "PUBLISH_DIR=%SOLUTION_DIR%publish-lite"
set "OUTPUT_DIR=%SOLUTION_DIR%Output-lite"
set "AGENT_PROJECT=%SOLUTION_DIR%..\Promaker\Promaker.Agent\Promaker.Agent.csproj"
set "AGENT_PUBLISH_DIR=%SOLUTION_DIR%publish-agent-lite"
set "COLLECTOR_PROJECT=%SOLUTION_DIR%..\..\Solutions\Runtime\Ds2.Collector\Ds2.Collector.fsproj"
set "COLLECTOR_PUBLISH_DIR=%SOLUTION_DIR%publish-collector-lite"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "ISS_FILE=%SOLUTION_DIR%Installer\DSPilot.iss"

if not exist "%ISCC%" goto :no_iscc

echo [1/4] Cleaning previous LITE build...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
if exist "%AGENT_PUBLISH_DIR%" rmdir /s /q "%AGENT_PUBLISH_DIR%"
if exist "%COLLECTOR_PUBLISH_DIR%" rmdir /s /q "%COLLECTOR_PUBLISH_DIR%"
echo       Done.
echo.

echo [2/4] Restoring NuGet packages...
dotnet restore "%SOLUTION_DIR%DSPilot.sln" --verbosity quiet
if !errorlevel! neq 0 goto :fail_restore
echo       Done.
echo.

:: framework-dependent: target PC needs Microsoft.AspNetCore.App 9.x (installer downloads it if missing).
echo [3/4] Publishing DSPilot (framework-dependent, win-x64)...
dotnet publish "%PROJECT_DIR%\DSPilot.csproj" -c Release -r win-x64 --self-contained false -o "%PUBLISH_DIR%" -p:PublishSingleFile=false -m:1
if !errorlevel! neq 0 goto :fail_publish
echo       Done.
echo.

echo [3c] Publishing Promaker.Agent (optional, framework-dependent, win-x64)...
if exist "%AGENT_PROJECT%" (
    dotnet publish "%AGENT_PROJECT%" -c Release -r win-x64 --self-contained false -o "%AGENT_PUBLISH_DIR%" -p:PublishSingleFile=false -m:1
    if !errorlevel! neq 0 (
        echo       [WARN] Promaker.Agent publish FAILED - installer will be built WITHOUT the Agent option.
        if exist "%AGENT_PUBLISH_DIR%" rmdir /s /q "%AGENT_PUBLISH_DIR%"
    ) else (
        echo       Done.
    )
) else (
    echo       [WARN] Promaker.Agent project not found - skipping Agent bundle.
)
echo.

echo [3d] Publishing Ds2.Collector (framework-dependent, win-x64)...
if exist "%COLLECTOR_PROJECT%" (
    dotnet publish "%COLLECTOR_PROJECT%" -c Release -r win-x64 --self-contained false -o "%COLLECTOR_PUBLISH_DIR%" -p:PublishSingleFile=false -m:1
    if !errorlevel! neq 0 (
        echo       [WARN] Ds2.Collector publish FAILED - installer will be built WITHOUT the Agent/Collector option.
        if exist "%COLLECTOR_PUBLISH_DIR%" rmdir /s /q "%COLLECTOR_PUBLISH_DIR%"
    ) else (
        echo       Done.
    )
) else (
    echo       [WARN] Ds2.Collector project not found - skipping Agent/Collector bundle.
)
echo.

echo [4/4] Building LITE installer with Inno Setup...
:: Secrets (dsp.conf) - same resolution order as build-installer.bat, copied into publish-lite.
if exist "%PUBLISH_DIR%\dsp.conf" del /q "%PUBLISH_DIR%\dsp.conf"
set "DSP_CONF_SRC="
if defined DUALSOFT_SECRETS_DIR if exist "%DUALSOFT_SECRETS_DIR%\dsp.conf" set "DSP_CONF_SRC=%DUALSOFT_SECRETS_DIR%\dsp.conf"
if not defined DSP_CONF_SRC if exist "%SOLUTION_DIR%Installer\dsp.conf" set "DSP_CONF_SRC=%SOLUTION_DIR%Installer\dsp.conf"
if not defined DSP_CONF_SRC if exist "%PROJECT_DIR%\dsp.conf" set "DSP_CONF_SRC=%PROJECT_DIR%\dsp.conf"
if not defined DSP_CONF_SRC if exist "%PROJECT_DIR%\appsettings.Secrets.json" set "DSP_CONF_SRC=%PROJECT_DIR%\appsettings.Secrets.json"

set "ISS_DEFS=/DLite /DPublishDir=..\publish-lite /DAgentPublishDir=..\publish-agent-lite /DCollectorPublishDir=..\publish-collector-lite /DOutputDir=..\Output-lite /DOutputSuffix=_Lite"

if defined DSP_CONF_SRC (
    echo       secrets: {app}\dsp.conf ^<- "!DSP_CONF_SRC!"
    copy /y "!DSP_CONF_SRC!" "%PUBLISH_DIR%\dsp.conf" >nul
    "%ISCC%" %ISS_DEFS% "%ISS_FILE%"
) else (
    set "BRIEFING_KEY=%DSP_BRIEFING_API_KEY%"
    if exist "%SOLUTION_DIR%Installer\briefing-apikey.txt" set /p BRIEFING_KEY=<"%SOLUTION_DIR%Installer\briefing-apikey.txt"
    if defined BRIEFING_KEY (
        echo       dsp.conf not found - injecting briefing API key only ^(no CloudAuth^).
        "%ISCC%" %ISS_DEFS% /D"BriefingApiKey=!BRIEFING_KEY!" "%ISS_FILE%"
    ) else (
        echo       [WARN] neither dsp.conf nor briefing key found - mailing/cloud 'unconfigured' build.
        "%ISCC%" %ISS_DEFS% "%ISS_FILE%"
    )
)
if !errorlevel! neq 0 goto :fail_iscc
echo       Done.
echo.

echo ============================================
echo   LITE Build Complete!
echo ============================================
echo.
for /f "tokens=*" %%i in ('powershell -NoProfile -Command "(Get-Item '%PUBLISH_DIR%\DSPilot.exe').VersionInfo.FileVersion"') do set "APP_VER=%%i"
echo   Installer: %OUTPUT_DIR%\DSPilot_Setup_%APP_VER%_Lite.exe
for %%F in ("%OUTPUT_DIR%\DSPilot_Setup_%APP_VER%_Lite.exe") do echo   Size     : %%~zF bytes
echo.
goto :end

:no_iscc
echo [ERROR] Inno Setup 6 not found: %ISCC%
goto :error

:fail_restore
echo [ERROR] dotnet restore failed.
goto :error

:fail_publish
echo [ERROR] dotnet publish (DSPilot) failed.
goto :error

:fail_iscc
echo [ERROR] Inno Setup compilation failed.
goto :error

:error
echo.
echo ============================================
echo   LITE Build FAILED. See errors above.
echo ============================================
echo.
if not defined NOPAUSE pause
exit /b 1

:end
if not defined NOPAUSE (
    if exist "%OUTPUT_DIR%\DSPilot_Setup_%APP_VER%_Lite.exe" (
        start "" explorer.exe /select,"%OUTPUT_DIR%\DSPilot_Setup_%APP_VER%_Lite.exe"
    ) else if exist "%OUTPUT_DIR%" (
        start "" explorer.exe "%OUTPUT_DIR%"
    )
)
if not defined NOPAUSE pause
exit /b 0
