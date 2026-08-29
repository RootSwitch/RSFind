# Fails if any tracked text file contains an em-dash, an en-dash, or a British
# spelling.
#
# House style is " - ". Those characters arrive invisibly through pasted text
# and editor autocorrect, and a mixed file is not something review catches.
#
# The spellings are checked for the same reason. CSS, the DOM and the Win32 API
# are all American, so a British spelling in prose ends up sitting two lines
# from `background-color` and `ForeColor` and reads as a typo rather than a
# dialect. Comments are cheap to get wrong and cheap to leave wrong; a README's
# first table and a social preview image are neither, and an image cannot be
# grepped once links to it are cached.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\charcheck.ps1

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$banned = @{
    [char]0x2014 = 'em-dash (U+2014)'
    [char]0x2013 = 'en-dash (U+2013)'
    [char]0x2015 = 'horizontal bar (U+2015)'
}

# Left out on purpose: anything that is also a valid American word or an API
# name. "cancelled" appears in both dialects, and Win32 spells things its own
# way, so only unambiguous prose spellings are listed.
$britishPattern = 'colour|behaviour|neighbour|centre|favourite|licence|defence|' +
                  'catalogue|programme|travelling|modelling|labelled|signalling|' +
                  '\bgrey\b|(?:organi|recogni|initiali|normali|optimi|customi|' +
                  'visuali|minimi|maximi|apologi|analy)se[sd]?\b'

$extensions = '*.cs', '*.ps1', '*.cmd', '*.md', '*.txt', '*.json', '*.yml'
$skipDirs = '\\\.git\\', '\\bin\\', '\\obj\\'

$failures = 0
foreach ($ext in $extensions) {
    foreach ($file in Get-ChildItem -Path $root -Filter $ext -Recurse -File) {
        $skip = $false
        foreach ($d in $skipDirs) { if ($file.FullName -match $d) { $skip = $true } }
        if ($skip) { continue }

        $isSelf = $file.FullName -eq $MyInvocation.MyCommand.Path
        $lineNo = 0
        foreach ($line in (Get-Content -LiteralPath $file.FullName -Encoding UTF8)) {
            $lineNo++
            $rel = $file.FullName.Substring($root.Length + 1)
            foreach ($ch in $banned.Keys) {
                if ($line.IndexOf($ch) -ge 0) {
                    Write-Host ("{0}:{1}: {2}" -f $rel, $lineNo, $banned[$ch]) -ForegroundColor Red
                    Write-Host ("    {0}" -f $line.Trim())
                    $failures++
                }
            }
            # This file necessarily spells out every word it bans, so it is
            # exempt from the spelling scan. It is still checked for dashes.
            if (-not $isSelf) {
                foreach ($m in [regex]::Matches($line, $britishPattern, 'IgnoreCase')) {
                    Write-Host ("{0}:{1}: British spelling '{2}'" -f $rel, $lineNo, $m.Value) -ForegroundColor Red
                    Write-Host ("    {0}" -f $line.Trim())
                    $failures++
                }
            }
        }
    }
}

if ($failures -gt 0) {
    Write-Host ""
    Write-Host ("charcheck FAILED: {0} occurrence(s). Use ' - ' and American spelling." -f $failures) -ForegroundColor Red
    exit 1
}
Write-Host "charcheck passed." -ForegroundColor Green
exit 0
