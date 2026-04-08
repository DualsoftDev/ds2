@echo off
echo ========================================
echo  Ds2.View3D Model Gallery Server
echo ========================================
echo.
echo Starting HTTP server on port 8000...
echo.
echo Gallery URLs:
echo   - Main Gallery (29 models): http://localhost:8000/gallery.html
echo   - Model Load Test:          http://localhost:8000/test-all-models.html
echo.
echo Press Ctrl+C to stop the server
echo ========================================
echo.

REM Open gallery.html automatically
start http://localhost:8000/gallery.html

python -m http.server 8000

pause
