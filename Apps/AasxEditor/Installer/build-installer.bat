@echo off
REM Build the AasxEditor.Desktop installer end-to-end.
REM
REM Steps:
REM   1) Fetch Monaco Editor (offline JS bundle) into Core/wwwroot/lib if missing.
REM   2) dotnet publish Desktop with self-contained win-x64 profile.
REM   3) Compile the Inno Setup script into Installer/Output/.
REM
REM Prerequisites on the build machine:
REM   - .NET SDK 9.0+
REM   - PowerShell (built-in on Win 10/11)
REM   - Inno Setup 6 (ISCC.exe on PATH, or installed at default location)

setlocal
pushd "%~dp0\.."

echo ======================================================================
echo [1/3] Fetching Monaco Editor (offline) ...
echo ======================================================================
powershell.exe -ExecutionPolicy Bypass -File "scripts\fetch-monaco.ps1"
if errorlevel 1 goto :err

echo.
echo ======================================================================
echo [2/3] Publishing AasxEditor.Desktop (self-contained, win-x64) ...
echo ======================================================================
dotnet publish "AasxEditor.Desktop\AasxEditor.Desktop.csproj" -c Release -p:PublishProfile=win-x64 --nologo -v minimal
if errorlevel 1 goto :err

echo.
echo ======================================================================
echo [3/3] Compiling Inno Setup installer ...
echo ======================================================================

REM Find ISCC.exe -- try PATH first, then default install locations.
set "ISCC_EXE="
where iscc.exe >nul 2>nul && set "ISCC_EXE=iscc.exe"
if not defined ISCC_EXE if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC_EXE=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC_EXE if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe"      set "ISCC_EXE=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC_EXE (
    echo ERROR: ISCC.exe not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php
    goto :err
)

"%ISCC_EXE%" "Installer\AasxEditor.iss"
if errorlevel 1 goto :err

echo.
echo ======================================================================
echo SUCCESS -- installer placed in Installer\Output\
echo ======================================================================
dir /b "Installer\Output\*.exe" 2>nul

popd
endlocal
exit /b 0

:err
echo.
echo ----------------------------------------------------------------------
echo BUILD FAILED
echo ----------------------------------------------------------------------
popd
endlocal
exit /b 1