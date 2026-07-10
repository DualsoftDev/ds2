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
:: Promaker.Agent (옵션 컴포넌트) — Promaker 없이 DSPilot 만 쓰는 환경용 헤드리스 모니터링 백엔드.
:: 별도 솔루션(Apps\Promaker)의 프로젝트를 self-contained 로 publish-agent 폴더에 publish.
:: 이 폴더가 비면 DSPilot.iss 의 #if HasAgent 가드가 Agent 옵션을 자동 생략한다.
set "AGENT_PROJECT=%SOLUTION_DIR%..\Promaker\Promaker.Agent\Promaker.Agent.csproj"
set "AGENT_PUBLISH_DIR=%SOLUTION_DIR%publish-agent"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "ISS_FILE=%SOLUTION_DIR%Installer\DSPilot.iss"
set "MTX_DIR=%SOLUTION_DIR%Installer\mediamtx"
set "FFMPEG_DIR=%SOLUTION_DIR%Installer\ffmpeg"
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
if exist "%AGENT_PUBLISH_DIR%" rmdir /s /q "%AGENT_PUBLISH_DIR%"
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

rem ffmpeg (essentials, GPL) - CCTV snapshot one-shot frame grabber (/api/cctv/snapshot).
rem Not a resident service: DSPilot spawns it per request. gyan.dev "release-essentials" = latest
rem stable; the zip's top folder name is versioned so locate ffmpeg.exe by recursive search.
rem 실패해도 설치 빌드는 계속 - DSPilot.iss 의 #if HasFfmpeg 가드가 자동 생략(스냅샷은 폴백 이미지만 동작).
if not exist "%FFMPEG_DIR%\ffmpeg.exe" (
    echo       Downloading ffmpeg release-essentials...
    if not exist "%FFMPEG_DIR%" mkdir "%FFMPEG_DIR%"
    powershell -NoProfile -Command "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $u='https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'; $z='%FFMPEG_DIR%\ff.zip'; $d='%FFMPEG_DIR%\ff-tmp'; Invoke-WebRequest -Uri $u -OutFile $z; Expand-Archive -Path $z -DestinationPath $d -Force; $exe=Get-ChildItem $d -Recurse -Filter ffmpeg.exe | Select-Object -First 1; Copy-Item $exe.FullName '%FFMPEG_DIR%\ffmpeg.exe' -Force; $lic=Get-ChildItem $d -Recurse -Filter LICENSE | Select-Object -First 1; if ($lic) { Copy-Item $lic.FullName '%FFMPEG_DIR%\LICENSE-ffmpeg.txt' -Force }; Remove-Item $d -Recurse -Force; Remove-Item $z"
    if !errorlevel! neq 0 (
        echo       [WARN] ffmpeg download FAILED — installer will be built WITHOUT ffmpeg ^(snapshot live-grab disabled^).
    )
) else (
    echo       ffmpeg already present, skipping.
)
echo       Done.
echo.

:: Step 3c: Publish Promaker.Agent (optional component, self-contained)
:: Promaker 미설치 환경에서 DSPilot 가 접속할 5051 모니터링 Hub 백엔드. 별도 솔루션 프로젝트라
:: DSPilot.sln 에 없고 csproj 를 직접 publish (자체 의존성 restore 포함). self-contained = 타겟 PC 에
:: .NET 9 런타임이 없어도 동작. 실패해도 설치 빌드는 계속 — DSPilot.iss 가 Agent 옵션만 자동 생략한다.
echo [3c] Publishing Promaker.Agent (optional, self-contained, win-x64)...
if exist "%AGENT_PROJECT%" (
    dotnet publish "%AGENT_PROJECT%" -c Release -r win-x64 --self-contained true -o "%AGENT_PUBLISH_DIR%" -p:PublishSingleFile=false -m:1
    if !errorlevel! neq 0 (
        echo       [WARN] Promaker.Agent publish FAILED — installer will be built WITHOUT the Agent option.
        if exist "%AGENT_PUBLISH_DIR%" rmdir /s /q "%AGENT_PUBLISH_DIR%"
    ) else (
        echo       Done.
    )
) else (
    echo       [WARN] Promaker.Agent project not found at "%AGENT_PROJECT%" — skipping Agent bundle.
)
echo.

:: Step 4: Build installer with Inno Setup
echo [4/4] Building installer with Inno Setup...
:: 브리핑 릴레이 API 키 주입 — Installer\briefing-apikey.txt(git 미포함) 또는 환경변수 DSP_BRIEFING_API_KEY.
:: 키가 있으면 /D 로 넘겨 설치본에 릴레이 자동연결(appsettings.Secrets.json)을 포함시킨다. 없으면 경고 후 미포함 빌드.
set "BRIEFING_KEY=%DSP_BRIEFING_API_KEY%"
if exist "%SOLUTION_DIR%Installer\briefing-apikey.txt" set /p BRIEFING_KEY=<"%SOLUTION_DIR%Installer\briefing-apikey.txt"
if defined BRIEFING_KEY (
    echo       브리핑 릴레이 자동연결 포함 ^(API 키 주입됨^).
    "%ISCC%" /D"BriefingApiKey=%BRIEFING_KEY%" "%ISS_FILE%"
) else (
    echo       [WARN] 브리핑 API 키 미주입 — 설치본에 릴레이 자동연결이 포함되지 않습니다.
    echo              ^(Installer\briefing-apikey.txt 생성 또는 DSP_BRIEFING_API_KEY 설정 후 재빌드^)
    "%ISCC%" "%ISS_FILE%"
)
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
:: NOPAUSE=1 (통합 빌드 build-suite.sh 등 비대화형 호출) 면 멈추지 않는다.
if not defined NOPAUSE pause
exit /b 1

:end
:: 빌드 성공 후 산출물 폴더를 연다(설치 exe 선택 상태로). 통합 빌드(NOPAUSE) 시엔 열지 않는다.
if not defined NOPAUSE (
    if exist "%OUTPUT_DIR%\DSPilot_Setup_%APP_VER%.exe" (
        start "" explorer.exe /select,"%OUTPUT_DIR%\DSPilot_Setup_%APP_VER%.exe"
    ) else if exist "%OUTPUT_DIR%" (
        start "" explorer.exe "%OUTPUT_DIR%"
    )
)
if not defined NOPAUSE pause
exit /b 0
