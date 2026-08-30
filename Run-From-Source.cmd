@echo off
rem Runs RSFind from the .cs sources, without RSFind.exe.
rem
rem Same app, compiled in memory on each launch: slower to start and heavier in
rem memory than the exe, but it ships no binary, which is what you want when
rem handing it to someone who will not run a stranger's exe or onto a machine
rem that blocks unsigned ones. To build the exe instead, run Build-RSFind.cmd.
rem
rem Takes an optional folder to search:  Run-From-Source.cmd C:\logs
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0Run-From-Source.ps1" "%~1"
