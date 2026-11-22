@echo off
rem Removes the shortcuts and launchers. Sign-ins are kept unless you run:  uninstall.cmd -RemoveSignIns
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
