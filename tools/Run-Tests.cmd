@echo off
rem Builds and runs the engine tests with the C# compiler that ships inside
rem Windows. No SDK, no NuGet, no network. Same compiler the app is built with,
rem so a test cannot pass against a different language version than it ships on.
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: could not find the in-box csc.exe under %WINDIR%\Microsoft.NET
    exit /b 1
)

set "ROOT=%~dp0.."

"%CSC%" /nologo /target:exe /optimize+ /out:"%~dp0EngineTests.exe" ^
    "%ROOT%\Matching.cs" "%ROOT%\TextFiles.cs" "%ROOT%\FindEngine.cs" ^
    "%~dp0EngineTests.cs"
if errorlevel 1 (
    echo Build FAILED.
    exit /b 1
)

"%~dp0EngineTests.exe"
if errorlevel 1 (
    echo Tests FAILED.
    exit /b 1
)

call "%~dp0charcheck.cmd"
if errorlevel 1 exit /b 1

echo All checks passed.
