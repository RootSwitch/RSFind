@echo off
rem Runs the house style check. Called by Run-Tests.cmd; safe to run alone.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0charcheck.ps1"
exit /b %errorlevel%
