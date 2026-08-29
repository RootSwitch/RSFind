# Verifies that the engine tests can actually fail.
#
# The house rule is that a check is not trusted until the defect it is meant to
# catch has been planted and watched to fail. Doing that by hand once is worth
# little six months later, so the plants live here and can be re-run after any
# refactor. Each plant is a literal string swap in a source file, applied to a
# copy of the tree, never to the working files.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Plant-Defects.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { Write-Host "ERROR: no in-box csc.exe found"; exit 1 }

# file, the text to break, what to break it into, and the check that must fail.
$plants = @(
    @{ file = 'TextFiles.cs'
       from = 'if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)'
       to   = 'if (false)'
       want = 'UTF-32LE BOM is not mistaken for UTF-16LE'
       why  = 'BOM ordering: UTF-16 must not claim a UTF-32 file' },

    @{ file = 'TextFiles.cs'
       from = 'if (DetectBom(bytes, out bomLength) != null) return false;'
       to   = 'if (false) return false;'
       want = 'UTF-16 with a BOM is text, not binary'
       why  = 'the binary sniff must consult the BOM before it counts NUL bytes' },

    @{ file = 'TextFiles.cs'
       from = 'UTF8Encoding strict = new UTF8Encoding(false, true);'
       to   = 'UTF8Encoding strict = new UTF8Encoding(false, false);'
       want = 'an ANSI byte is not decoded into a replacement character'
       why  = 'a lenient UTF-8 decode swallows ANSI bytes and reports success' },

    @{ file = 'TextFiles.cs'
       from = 'if (start < text.Length)'
       to   = 'if (start <= text.Length)'
       want = 'a trailing newline does not add a phantom line'
       why  = 'a file ending in a newline must not grow an empty last line' },

    @{ file = 'Matching.cs'
       from = 'length = m.Length > 0 ? m.Length : 1;'
       to   = 'length = m.Length;'
       want = 'a zero-width regex terminates'
       why  = 'a pattern that can match empty must not report endless hits' },

    @{ file = 'Matching.cs'
       from = 'if (!wholeWord || IsWholeWordAt(line, at, literal.Length))'
       to   = 'if (true)'
       want = 'whole word rejects a longer run'
       why  = 'whole word is what stops nvme0n1 answering a search for nvme0' },

    @{ file = 'Matching.cs'
       from = 'if (pattern[0] == ''.'' && pattern.IndexOf(''*'') < 0 && pattern.IndexOf(''?'') < 0)'
       to   = 'if (false)'
       want = 'a bare .log is read as *.log'
       why  = 'typing .log and getting nothing back reads as a broken search' },

    @{ file = 'FindEngine.cs'
       from = 'if (fh.Hits.Count >= opts.MaxHitsPerFile)'
       to   = 'if (false)'
       want = 'the per-file cap holds'
       why  = 'an uncapped file is how a search for a common letter runs out of memory' },

    @{ file = 'FindEngine.cs'
       from = 'if (!Masks.Allows(Path.GetFileName(f), include, exclude)) continue;'
       to   = 'if (false) continue;'
       want = 'a mask that excludes every match returns nothing'
       why  = 'the mask has to be applied during the walk, not just parsed' }
)

$work = Join-Path $root 'testdata\plant'
$failures = 0
$i = 0

foreach ($plant in $plants) {
    $i++
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    foreach ($name in 'Matching.cs', 'TextFiles.cs', 'FindEngine.cs') {
        Copy-Item (Join-Path $root $name) (Join-Path $work $name)
    }
    Copy-Item (Join-Path $root 'tools\EngineTests.cs') (Join-Path $work 'EngineTests.cs')

    $target = Join-Path $work $plant.file
    $text = [IO.File]::ReadAllText($target)
    if ($text.IndexOf($plant.from) -lt 0) {
        Write-Host ("[{0}] PLANT MISSED  {1}" -f $i, $plant.why) -ForegroundColor Yellow
        Write-Host ("    the source no longer contains: {0}" -f $plant.from)
        $failures++
        continue
    }
    [IO.File]::WriteAllText($target, $text.Replace($plant.from, $plant.to))

    # Native output is captured through cmd rather than PowerShell's 2>&1.
    # Windows PowerShell wraps a native command's stderr in ErrorRecords, which
    # under ErrorActionPreference Stop aborts this script on the very failure it
    # exists to observe - a defect being caught would look like a script crash.
    $log = Join-Path $work 'out.txt'
    $exe = Join-Path $work 'Planted.exe'
    $sources = @('Matching.cs', 'TextFiles.cs', 'FindEngine.cs', 'EngineTests.cs') |
               ForEach-Object { '"' + (Join-Path $work $_) + '"' }
    cmd /c ('"' + $csc + '" /nologo /target:exe /out:"' + $exe + '" ' +
            ($sources -join ' ') + ' > "' + $log + '" 2>&1')
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("[{0}] BUILD FAILED  {1}" -f $i, $plant.why) -ForegroundColor Yellow
        Write-Host (Get-Content $log -Raw)
        $failures++
        continue
    }

    # The planted binary writes its corpus beside itself, so it cannot disturb
    # the copy the real test run uses.
    cmd /c ('"' + $exe + '" > "' + $log + '" 2>&1')
    $code = $LASTEXITCODE
    $text = if (Test-Path $log) { (Get-Content $log -Raw) } else { '' }
    if ($null -eq $text) { $text = '' }

    if ($code -eq 0) {
        Write-Host ("[{0}] NOT CAUGHT    {1}" -f $i, $plant.why) -ForegroundColor Red
        Write-Host ("    the suite still passed with the defect in place")
        $failures++
    }
    elseif ($text -notlike ("*" + $plant.want + "*")) {
        Write-Host ("[{0}] WRONG CHECK   {1}" -f $i, $plant.why) -ForegroundColor Red
        Write-Host ("    expected the failure to name: {0}" -f $plant.want)
        Write-Host ("    got: {0}" -f $text.Trim())
        $failures++
    }
    else {
        Write-Host ("[{0}] caught        {1}" -f $i, $plant.why) -ForegroundColor Green
    }
}

if (Test-Path $work) { Remove-Item -Recurse -Force $work }
$testdata = Join-Path $root 'testdata'
if ((Test-Path $testdata) -and -not (Get-ChildItem $testdata)) { Remove-Item -Force $testdata }

Write-Host ""
if ($failures -gt 0) {
    Write-Host ("{0} of {1} plants were not caught by the check they belong to." -f $failures, $plants.Count) -ForegroundColor Red
    exit 1
}
Write-Host ("All {0} planted defects were caught." -f $plants.Count) -ForegroundColor Green
exit 0
