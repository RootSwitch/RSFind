@echo off
rem Builds RSFind.exe with the C# compiler that ships inside Windows
rem (.NET Framework 4.x). No SDK, no download, no NuGet, no network.
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: could not find the in-box csc.exe under %WINDIR%\Microsoft.NET
    exit /b 1
)

rem Every reference below is an assembly already installed with the framework.
rem Nothing here is restored, vendored, or downloaded.
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0RSFind.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll ^
    /r:System.IO.Compression.dll ^
    "%~dp0Themes.cs" "%~dp0Native.cs" "%~dp0Controls.cs" ^
    "%~dp0Matching.cs" "%~dp0TextFiles.cs" "%~dp0OfficeText.cs" ^
    "%~dp0Replacer.cs" "%~dp0FindEngine.cs" ^
    "%~dp0ViewRules.cs" "%~dp0Settings.cs" "%~dp0ResultsView.cs" ^
    "%~dp0ReplaceDialog.cs" "%~dp0RSFind.cs"
if errorlevel 1 (
    echo Build FAILED.
    exit /b 1
)
echo Built: %~dp0RSFind.exe
