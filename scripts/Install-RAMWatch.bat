@echo off
net session >nul 2>&1 || (echo Run as Administrator & pause & exit /b 1)

set INSTALL_DIR=%~dp0..
set DATA_DIR=%ProgramData%\RAMWatch

echo Installing RAMWatch service...

:: Create data directory with restricted ACLs
mkdir "%DATA_DIR%\logs" 2>nul

:: ACL: Administrators and SYSTEM get full control, Users get read-only
icacls "%DATA_DIR%" /inheritance:r >nul 2>&1
icacls "%DATA_DIR%" /grant:r Administrators:(OI)(CI)F >nul
icacls "%DATA_DIR%" /grant:r SYSTEM:(OI)(CI)F >nul
icacls "%DATA_DIR%" /grant:r Users:(OI)(CI)RX >nul

:: Install and configure service
sc.exe create RAMWatch binPath= "\"%INSTALL_DIR%\src\RAMWatch.Service\bin\Release\net10.0-windows\win-x64\publish\RAMWatch.Service.exe\"" start= auto DisplayName= "RAMWatch Monitor"
sc.exe description RAMWatch "RAM stability and system integrity monitor"
sc.exe failure RAMWatch reset= 86400 actions= restart/5000/restart/10000/restart/30000

:: Start service
sc.exe start RAMWatch

echo.
echo RAMWatch service installed and started.
echo Data directory: %DATA_DIR%
echo.
pause
