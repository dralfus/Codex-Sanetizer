@echo off
setlocal
echo %date% %time% logon_command_started>> C:\SandboxProject\.sandbox-jobs\wsb-logon.txt
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\SandboxProject\scripts\SandboxWorkerBootstrap.ps1
exit /b %ERRORLEVEL%
