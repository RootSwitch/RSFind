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
    /r:System.Xml.dll /r:System.IO.Compression.dll ^
    "%ROOT%\Matching.cs" "%ROOT%\TextFiles.cs" "%ROOT%\OfficeText.cs" ^
    "%ROOT%\FindEngine.cs" "%~dp0EngineTests.cs"
if errorlevel 1 (
    echo Build FAILED.
    exit /b 1
)

rem A sentinel beside the test corpus, checked afterwards. The suite writes its
rem files under testdata\engine-tests and must remove only that; an earlier
rem version deleted the whole of testdata coming and going, which quietly ate a
rem sample corpus a person had left there. This is the cheapest possible guard
rem against that returning, and it fails loudly rather than silently.
set "SENTINEL=%ROOT%\testdata\sentinel-must-survive.txt"
if not exist "%ROOT%\testdata" mkdir "%ROOT%\testdata"
echo A test must remove only what it created. > "%SENTINEL%"

"%~dp0EngineTests.exe"
if errorlevel 1 (
    echo Tests FAILED.
    exit /b 1
)

if not exist "%SENTINEL%" (
    echo BLAST RADIUS FAILED: the suite deleted a file it did not create.
    exit /b 1
)
del "%SENTINEL%"
rem Leave testdata itself alone if anything else is in it.
rmdir "%ROOT%\testdata" 2>nul

call "%~dp0charcheck.cmd"
if errorlevel 1 exit /b 1

echo All checks passed.
