@echo off
setlocal enableextensions enabledelayedexpansion

echo ============================================================
echo  Promaker / DSPilot - Leftover File Cleaner
echo ============================================================
echo.
echo Purpose: After uninstalling Promaker/DSPilot via Control Panel,
echo          this script wipes leftover user data / cache / logs /
echo          shared files so the PC is clean for a fresh install test.
echo.
echo NOTE: This script does NOT touch the Program Files install dir.
echo       Uninstall the apps via Control Panel first.
echo.

rem ---- Admin check (needed for PROGRAMDATA and service cleanup) ----
net session >nul 2>&1
if errorlevel 1 (
    echo [!] Administrator privileges required.
    echo     Right-click this .bat and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo Targets to delete:
echo   [APPDATA]      %APPDATA%\Dualsoft\Promaker\
echo   [LOCALAPPDATA] %LOCALAPPDATA%\Dualsoft\Promaker\
echo   [TEMP]         %TEMP%\Promaker\
echo   [TEMP]         %TEMP%\promaker-kb-*  and  %TEMP%\codex-img-*
echo   [PROGRAMDATA]  %PROGRAMDATA%\DualSoft\Shared\
echo   [DOCUMENTS]    %USERPROFILE%\Documents\ds2_eventlog_*.txt
echo   [DOCUMENTS]    %USERPROFILE%\Documents\ds2_iomap_*.txt
echo   [SERVICE]      PromakerAgentService / DSPilotService (if present)
echo   [REGISTRY]     HKCU\...\Run\PromakerAgentTray (if present)
echo.

set /p CONFIRM=Proceed? (Y/N):
if /i not "%CONFIRM%"=="Y" (
    echo Cancelled.
    exit /b 1
)
echo.

echo [1/6] Killing running processes ...
taskkill /f /im Promaker.exe           >nul 2>&1
taskkill /f /im DSPilot.exe            >nul 2>&1
taskkill /f /im Promaker.Agent.exe     >nul 2>&1
taskkill /f /im Promaker.AgentTray.exe >nul 2>&1

echo [2/6] Removing leftover Windows services ...
sc query PromakerAgentService >nul 2>&1
if not errorlevel 1 (
    echo   - PromakerAgentService stop/delete
    sc stop   PromakerAgentService >nul 2>&1
    sc delete PromakerAgentService >nul 2>&1
)
sc query DSPilotService >nul 2>&1
if not errorlevel 1 (
    echo   - DSPilotService stop/delete
    sc stop   DSPilotService >nul 2>&1
    sc delete DSPilotService >nul 2>&1
)

echo [3/6] Cleaning APPDATA / LOCALAPPDATA ...
if exist "%APPDATA%\Dualsoft\Promaker"      rmdir /s /q "%APPDATA%\Dualsoft\Promaker"
if exist "%LOCALAPPDATA%\Dualsoft\Promaker" rmdir /s /q "%LOCALAPPDATA%\Dualsoft\Promaker"
rem Remove empty parent folder if no other Dualsoft app remains
rmdir "%APPDATA%\Dualsoft"      2>nul
rmdir "%LOCALAPPDATA%\Dualsoft" 2>nul

echo [4/6] Cleaning TEMP and PROGRAMDATA\DualSoft\Shared ...
if exist "%TEMP%\Promaker" rmdir /s /q "%TEMP%\Promaker"
for /d %%D in ("%TEMP%\promaker-kb-*") do rmdir /s /q "%%D" 2>nul
for /d %%D in ("%TEMP%\codex-img-*")   do rmdir /s /q "%%D" 2>nul
if exist "%PROGRAMDATA%\DualSoft\Shared" rmdir /s /q "%PROGRAMDATA%\DualSoft\Shared"
rmdir "%PROGRAMDATA%\DualSoft" 2>nul

echo [5/6] Cleaning simulation logs in Documents ...
del /q "%USERPROFILE%\Documents\ds2_eventlog_*.txt" 2>nul
del /q "%USERPROFILE%\Documents\ds2_iomap_*.txt"    2>nul

echo [6/6] Cleaning leftover registry keys ...
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PromakerAgentTray >nul 2>&1
if not errorlevel 1 (
    echo   - HKCU\...\Run\PromakerAgentTray deleted
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PromakerAgentTray /f >nul 2>&1
)

echo.
echo ============================================================
echo  Done. Ready for a fresh install test.
echo ============================================================
endlocal
pause
