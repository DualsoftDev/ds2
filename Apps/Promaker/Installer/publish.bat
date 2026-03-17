@echo off
setlocal EnableDelayedExpansion

echo ========================================
echo   Promaker Release Publish
echo ========================================
echo.

set "SCRIPT_DIR=%~dp0"
set "REPO_DIR=%SCRIPT_DIR%..\..\.."
set "PROJECT_FILE=%REPO_DIR%\Apps\Promaker\Promaker\Promaker.csproj"
set "SETUP_FILE=%SCRIPT_DIR%Setup.iss"
set "OUTPUT_DIR=%SCRIPT_DIR%Output"
set "ISCC_PATH=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "MODE=%~1"

if /I "%MODE%"=="" set "MODE=sc"

if /I "%MODE%"=="sc" (
    set "SELF_CONTAINED=true"
    set "MODE_LABEL=self-contained"
    set "PUBLISH_SUBDIR=publish-self-contained"
    set "OUTPUT_SUFFIX=_sc"
) else if /I "%MODE%"=="fd" (
    set "SELF_CONTAINED=false"
    set "MODE_LABEL=framework-dependent"
    set "PUBLISH_SUBDIR=publish-framework-dependent"
    set "OUTPUT_SUFFIX=_fd"
) else (
    echo [ERROR] Unknown mode: %MODE%
    echo         Use one of: sc, fd
    exit /b 1
)

set "PUBLISH_DIR=%REPO_DIR%\Apps\Promaker\Promaker\bin\Release\net9.0-windows\win-x64\%PUBLISH_SUBDIR%"

if not exist "%PROJECT_FILE%" (
    echo [ERROR] Project file not found:
    echo         %PROJECT_FILE%
    exit /b 1
)

echo Mode:
echo   %MODE_LABEL%
echo.

echo [1/5] Version bump...
powershell -Command "$f = '%PROJECT_FILE%'; [xml]$x = Get-Content $f; $pg = $x.SelectSingleNode('//PropertyGroup'); $v = [version]$pg.SelectSingleNode('Version').InnerText; $nv = '{0}.{1}.{2}.{3}' -f $v.Major,$v.Minor,$v.Build,($v.Revision+1); $pg.SelectSingleNode('Version').InnerText = $nv; $pg.SelectSingleNode('AssemblyVersion').InnerText = $nv; $pg.SelectSingleNode('FileVersion').InnerText = $nv; $x.Save($f); Write-Host ('  ' + $nv)"
if errorlevel 1 (
    echo [ERROR] Version bump failed.
    exit /b 1
)
echo.

echo [2/5] Restore...
dotnet restore "%PROJECT_FILE%" --verbosity quiet
if errorlevel 1 (
    echo [ERROR] Restore failed.
    exit /b 1
)
echo.

echo [3/5] Publish...
dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 --self-contained %SELF_CONTAINED% -o "%PUBLISH_DIR%" -p:DebugType=none -p:DebugSymbols=false
if errorlevel 1 (
    echo [ERROR] Publish failed.
    exit /b 1
)
echo.

if not exist "!ISCC_PATH!" (
    echo [ERROR] Inno Setup compiler not found:
    echo         !ISCC_PATH!
    echo Install Inno Setup 6 or update ISCC_PATH in this file.
    exit /b 1
)

echo [4/5] Build installer...
"!ISCC_PATH!" /DPublishDir="%PUBLISH_DIR%" /DSelfContainedMode="%SELF_CONTAINED%" /DOutputSuffix="%OUTPUT_SUFFIX%" "%SETUP_FILE%"
if errorlevel 1 (
    echo [ERROR] Installer build failed.
    exit /b 1
)
echo.

echo [5/5] Zip...
powershell -Command "$exe = (Get-ChildItem '%OUTPUT_DIR%\Promaker_Setup_*%OUTPUT_SUFFIX%.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1); $zip = $exe.FullName -replace '\.exe$','.zip'; Compress-Archive -Path $exe.FullName -DestinationPath $zip -Force; Write-Host ('  ' + $zip)"
if errorlevel 1 (
    echo [ERROR] Zip failed.
    exit /b 1
)
echo.

echo ========================================
echo   Done.
echo ========================================
echo Publish folder:
echo   %PUBLISH_DIR%
echo Installer output:
echo   %OUTPUT_DIR%
echo.

dir /b "%OUTPUT_DIR%\Promaker_Setup_*%OUTPUT_SUFFIX%.*" 2>nul

endlocal
