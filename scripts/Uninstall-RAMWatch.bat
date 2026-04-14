@echo off
net session >nul 2>&1 || (echo Run as Administrator & pause & exit /b 1)

echo Removing RAMWatch service...

sc.exe stop RAMWatch >nul 2>&1
sc.exe delete RAMWatch

:: Remove GUI autostart entry
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v RAMWatch /f 2>nul

echo.
echo RAMWatch service removed.
echo Data preserved in %ProgramData%\RAMWatch (delete manually if desired).
echo.
pause
