@echo off
echo ========================================
echo  Ds2.View3D 라이브러리 설정 확인
echo ========================================
echo.

set ERROR_COUNT=0

echo [1/4] Lib3D 폴더 확인...
if not exist "Lib3D\" (
    echo   ❌ Lib3D 폴더가 없습니다!
    set /a ERROR_COUNT+=1
) else (
    echo   ✅ Lib3D 폴더 존재
)

echo.
echo [2/4] 필수 파일 확인...
set FILES=index.js gallery.html gallery-extended.html test-all-models.html start-server.bat
for %%F in (%FILES%) do (
    if exist "%%F" (
        echo   ✅ %%F
    ) else (
        echo   ❌ %%F 없음!
        set /a ERROR_COUNT+=1
    )
)

echo.
echo [3/4] Lib3D 모델 파일 확인 (29개)...
set COUNT=0
for %%F in (Lib3D\*.js) do set /a COUNT+=1
echo   총 %COUNT%개 파일

if %COUNT% LSS 29 (
    echo   ⚠️  예상보다 적음 (29개 필요)
    set /a ERROR_COUNT+=1
) else if %COUNT% GTR 29 (
    echo   ℹ️  예상보다 많음 (29개 예상)
) else (
    echo   ✅ 정확히 29개
)

echo.
echo [4/4] Python 확인...
python --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo   ✅ Python 설치됨
    python --version
) else (
    echo   ❌ Python이 설치되지 않았습니다!
    echo   https://www.python.org/downloads/ 에서 Python 3 설치 필요
    set /a ERROR_COUNT+=1
)

echo.
echo ========================================
echo  확인 완료
echo ========================================
echo.

if %ERROR_COUNT% EQU 0 (
    echo ✅ 모든 검증 통과!
    echo.
    echo 서버를 실행하려면:
    echo   start-server.bat
    echo.
) else (
    echo ❌ %ERROR_COUNT%개 문제 발견
    echo 위 오류를 수정하세요.
    echo.
)

pause
