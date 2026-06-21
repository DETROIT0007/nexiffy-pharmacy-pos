@echo off
title Nexffy Pharmacy POS
cd /d "%~dp0"
echo.
echo  ============================================
echo   Nexffy Pharmacy POS  -  http://localhost:5200
echo  ============================================
echo.
echo  Starting server... (close this window to stop)
echo  Open your browser to: http://localhost:5200
echo.
dotnet run --no-launch-profile
pause
