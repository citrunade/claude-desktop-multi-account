@echo off
rem Double-click to install the Claude account shortcuts.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
