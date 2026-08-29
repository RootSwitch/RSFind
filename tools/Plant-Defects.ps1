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
       why  = 'the mask has to be applied during the walk, not just parsed' },

    @{ file = 'FindEngine.cs'
       from = 'if (OfficeText.IsSupported(path))'
       to   = 'if (false)'
       want = 'a workbook is searched even though exclude-binary is on'
       why  = 'the office branch must run before the binary sniff, not after' },

    @{ file = 'FindEngine.cs'
       from = 'if (OfficeText.IsKnownUnreadable(path))'
       to   = 'if (false)'
       want = 'an unreadable format is counted'
       why  = 'a .pdf contributing nothing silently reads as "the phrase is not there"' },

    # This one has to span two lines, and finding that out was the useful part.
    # Two independent things keep formula text out: only <v> and <is> are read,
    # and <f> is skipped before either can see it. Disabling the Skip alone
    # changes nothing, and adding <f> to the value reader alone is dead code
    # behind the Skip. Neither single-line plant was caught, which is not a gap
    # in the test - it is the property being guarded twice.
    @{ file = 'OfficeText.cs'
       from = @'
                    if (r.LocalName == "f" && !r.IsEmptyElement) { Skip(r); continue; }
                    if (r.LocalName == "v" && !r.IsEmptyElement) { v = ReadTextOf(r); continue; }
'@
       to   = @'
                    if ((r.LocalName == "v" || r.LocalName == "f") && !r.IsEmptyElement) { v = ReadTextOf(r); continue; }
'@
       want = 'an uncalculated formula cell contributes nothing'
       why  = 'searching formulas answers "SUM" with every totaled column in the book' },

    @{ file = 'OfficeText.cs'
       from = 'else if (r.LocalName == "rPh") inPhonetic = true;'
       to   = 'else if (false) inPhonetic = true;'
       want = 'a phonetic run is not appended to the value it annotates'
       why  = 'phonetic hints would double every value in a Japanese workbook' },

    @{ file = 'OfficeText.cs'
       from = 'case "del":'
       to   = 'case "del-disabled":'
       want = 'text inside a tracked deletion is not extracted'
       why  = 'matching deleted text reports a phrase that is not in the document' },

    @{ file = 'OfficeText.cs'
       from = 'if (type == "s")'
       to   = 'if (false)'
       want = 'a shared string resolves through its index'
       why  = 'the shared string table is where almost every cell value lives' },

    @{ file = 'OfficeText.cs'
       from = 'settings.DtdProcessing = DtdProcessing.Prohibit;'
       to   = 'settings.DtdProcessing = DtdProcessing.Parse;'
       want = 'a DTD in an Office file is refused, not expanded'
       why  = 'an XML parser that expands entities turns a file search into a file reader' },

    # Replace is the one feature here that can destroy a directory, so every
    # guard on it gets a plant.
    @{ file = 'Replacer.cs'
       from = 'if (info.LastWriteTimeUtc != file.LastWriteUtc)'
       to   = 'if (false)'
       want = 'an edit elsewhere in the file is refused too'
       why  = 'replacing into a file edited since the search writes from stale offsets' },

    @{ file = 'Replacer.cs'
       from = 'if (!string.Equals(current, hit.Line, StringComparison.Ordinal))'
       to   = 'if (false)'
       want = 'an edit that kept the size and the timestamp is caught by re-reading the line'
       why  = 'the timestamp check alone misses an edit that kept the same size' },

    @{ file = 'Replacer.cs'
       from = 'if (file.Extracted)'
       to   = 'if (false)'
       want = 'the refusal says it came from an Office file'
       why  = 'writing extracted text back over a workbook would destroy the zip' },

    @{ file = 'Replacer.cs'
       from = 'if (file.Transformed)'
       to   = 'if (false)'
       want = 'the refusal names the option to turn off'
       why  = 'stripped offsets point at cleaned text, not at the bytes on disk' },

    @{ file = 'Replacer.cs'
       from = 'if (!string.Equals(roundTrip, text, StringComparison.Ordinal))'
       to   = 'if (false)'
       want = 'a replacement the encoding cannot store is not written'
       why  = 'an ANSI codepage turns what it cannot store into a question mark and reports success' },

    @{ file = 'Replacer.cs'
       from = 'if (!change.Selected) continue;'
       to   = 'if (false) continue;'
       want = 'an unchecked change is not written'
       why  = 'the checkbox in the preview is the whole consent mechanism' },

    @{ file = 'Replacer.cs'
       from = 'if (now.Length != expectedLength || now.LastWriteTimeUtc.Ticks != expectedTicks)'
       to   = 'if (false)'
       want = 'undo does not restore over a later edit'
       why  = 'undo must not overwrite work someone did after the replace' },

    @{ file = 'Replacer.cs'
       from = 'if (options.PreserveCase) replacement = PreserveCase(matched, replacement);'
       to   = 'if (false) replacement = PreserveCase(matched, replacement);'
       want = 'the capital is preserved'
       why  = 'without case preservation a docs pass lowercases every sentence opener' },

    @{ file = 'Replacer.cs'
       from = 'if (HasUpper(replacement)) return replacement;'
       to   = 'if (false) return replacement;'
       want = 'an upper case match does not upper case a deliberate replacement'
       why  = 'reshaping a replacement that carries its own capitals overrules the person' },

    @{ file = 'TextFiles.cs'
       from = 'if (!string.Equals(stripped, text, StringComparison.Ordinal))'
       to   = 'if (true)'
       want = 'strip-ANSI does not make a file without escapes unreplaceable'
       why  = 'marking every file transformed makes Replace refuse everything by default' },

    @{ file = 'ViewRules.cs'
       from = 'return topIndex >= newCount;'
       to   = 'return false;'
       want = 'a short result set after a scrolled long one pulls the view home'
       why  = 'a viewport left past the end renders blank with no scrollbar to get back' },

    @{ file = 'ViewRules.cs'
       from = 'if (FileKeepsEverything(filter, relativePath)) return true;'
       to   = 'if (false) return true;'
       want = 'every hit in a matching file is kept, whatever the line says'
       why  = 'filtering by host must keep the whole file, since the host is only in its name' },

    @{ file = 'ViewRules.cs'
       from = 'return Contains(lineText, filter) || Contains(location, filter);'
       to   = 'return Contains(lineText, filter);'
       want = 'a hit matching its location label is kept'
       why  = 'a workbook hit has a cell reference where a log hit has a line number' }

    # There is no plant for "a list that grows is left alone". The obvious
    # guard for it was written, planted, and shown to be dead code: a valid top
    # index is always below the count, so growth cannot trigger the rule. The
    # guard was removed rather than kept with an untestable plant beside it.
)

$plantRoot = Join-Path $root 'testdata\plant'
$failures = 0
$i = 0

foreach ($plant in $plants) {
    $i++
    # Each plant gets its own directory rather than reusing one.
    #
    # Reusing a single folder meant deleting the previous Planted.exe at the
    # top of every iteration, and Windows does not always release an
    # executable the instant its process exits - a scanner or a lingering
    # handle turns that delete into an access-denied that aborts the run
    # halfway through, reported as a permissions error rather than as anything
    # to do with the plants. A fresh directory never contends for a lock.
    $work = Join-Path $plantRoot $i.ToString('00')
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    foreach ($name in 'Matching.cs', 'TextFiles.cs', 'OfficeText.cs', 'Replacer.cs', 'ViewRules.cs', 'FindEngine.cs') {
        Copy-Item (Join-Path $root $name) (Join-Path $work $name)
    }
    Copy-Item (Join-Path $root 'tools\EngineTests.cs') (Join-Path $work 'EngineTests.cs')

    # Line endings are normalized on both sides before matching. A plant that
    # spans two lines otherwise depends on how the repo happened to be checked
    # out, and would report itself as MISSED on a machine where it is fine.
    $target = Join-Path $work $plant.file
    $text = [IO.File]::ReadAllText($target).Replace("`r`n", "`n")
    $plant.from = $plant.from.Replace("`r`n", "`n")
    $plant.to = $plant.to.Replace("`r`n", "`n")
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
    $sources = @('Matching.cs', 'TextFiles.cs', 'OfficeText.cs', 'Replacer.cs', 'ViewRules.cs', 'FindEngine.cs', 'EngineTests.cs') |
               ForEach-Object { '"' + (Join-Path $work $_) + '"' }
    cmd /c ('"' + $csc + '" /nologo /target:exe /r:System.Xml.dll ' +
            '/r:System.IO.Compression.dll /out:"' + $exe + '" ' +
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

# Best effort. A binary still held by a scanner is not a reason to report the
# run as failed, and the next run writes into fresh numbered directories
# regardless of what is left here.
try {
    if (Test-Path $plantRoot) { Remove-Item -Recurse -Force $plantRoot -ErrorAction Stop }
} catch {
    Write-Host ("note: {0} could not be removed yet ({1})" -f $plantRoot, $_.Exception.Message)
}
$testdata = Join-Path $root 'testdata'
try {
    if ((Test-Path $testdata) -and -not (Get-ChildItem $testdata)) {
        Remove-Item -Force $testdata -ErrorAction Stop
    }
} catch { }

Write-Host ""
if ($failures -gt 0) {
    Write-Host ("{0} of {1} plants were not caught by the check they belong to." -f $failures, $plants.Count) -ForegroundColor Red
    exit 1
}
Write-Host ("All {0} planted defects were caught." -f $plants.Count) -ForegroundColor Green
exit 0
