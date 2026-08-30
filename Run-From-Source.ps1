# Runs RSFind from the .cs sources, compiling them in memory with Add-Type.
# Needs only Windows PowerShell 5.1 / .NET Framework 4.x, both inbox on Win 10/11.
#
# This is the no-binary path: the same app as RSFind.exe, but slower to start
# and heavier in memory because it compiles on every launch. Build-RSFind.cmd
# produces the exe, which is what to use day to day.
#
# Normally launched by Run-From-Source.cmd, since Windows opens a .ps1 in an
# editor on double-click rather than running it.
#
# Optionally takes the folder to search, so the launcher can pass one through:
#   Run-From-Source.cmd C:\logs

param([string]$Folder)

$scriptPath = $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptPath

# WinForms needs an STA thread. powershell.exe defaults to STA, but relaunch
# defensively if this was started MTA (-MTA, or an unusual host).
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    Start-Process powershell.exe -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA',
        '-WindowStyle', 'Hidden', '-File', "`"$scriptPath`"", "`"$Folder`""
    )
    return
}

# Same list as Build-RSFind.cmd, in the same order. If one gains a file and the
# other does not, this is the half that fails with a missing-type error rather
# than silently building something different.
$sources = @('Themes.cs', 'Native.cs', 'Controls.cs',
             'Matching.cs', 'TextFiles.cs', 'OfficeText.cs',
             'Replacer.cs', 'FindEngine.cs',
             'ViewRules.cs', 'Settings.cs', 'ResultsView.cs',
             'ReplaceDialog.cs', 'RSFind.cs') |
    ForEach-Object { Join-Path $root $_ }

$missing = $sources | Where-Object { -not (Test-Path $_) }
if ($missing) {
    [void][Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms')
    [System.Windows.Forms.MessageBox]::Show(
        "These source files are missing:`r`n`r`n" + ($missing -join "`r`n"),
        'RSFind') | Out-Null
    return
}

# Every reference is an assembly already installed with the framework, which is
# the same claim Build-RSFind.cmd makes. System.Xml and System.IO.Compression
# are what read .xlsx and .docx.
$references = @(
    'System.dll', 'System.Core.dll',
    'System.Windows.Forms.dll', 'System.Drawing.dll',
    'System.Xml.dll', 'System.IO.Compression.dll'
)

try {
    Add-Type -Path $sources -ReferencedAssemblies $references -ErrorAction Stop
}
catch {
    # A compile error here is almost always a C# 5 slip: the in-box compiler
    # predates string interpolation, null-conditionals and the rest, and its
    # message points at a character rather than saying the feature is too new.
    [void][Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms')
    [System.Windows.Forms.MessageBox]::Show(
        "RSFind did not compile.`r`n`r`n" + $_.Exception.Message,
        'RSFind') | Out-Null
    return
}

# Tell the app it is running from source, which refuses the Explorer entry.
# That entry records the program to launch, and from here that is powershell.exe
# rather than RSFind - a right-click item that opens a PowerShell window under a
# key named RSFind. Build the exe if you want the context menu.
[RSFind.MainForm]::RunningFromSource = $true

[RSFind.MainForm]::Run($Folder)
