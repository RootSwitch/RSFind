# Renders the app icon from the app's OWN brand code, and packs it into a
# multi-size .ico that the build stamps into the exe.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Make-Icon.ps1
#
# Run this when the mark changes. The .ico is committed rather than built on
# every compile, so Build-*.cmd stays a plain call to the in-box C# compiler
# with no PowerShell in the path.
#
# WHY IT RENDERS Brand.PaintMark RATHER THAN favicon.svg
#
# The mark exists twice already - as favicon.svg and as PaintMark's GDI+ calls,
# with a comment on PaintMark saying to change both together. Generating the
# .ico from the SVG would make it three, and the third would be the one nobody
# remembers. Rendering PaintMark means the file icon in Explorer and the window
# icon in the taskbar come from the same function and cannot drift, which is the
# exact failure RSMultiTerm's make-icon.js was written to end - its header
# records a header logo that had quietly diverged from the taskbar one.
#
# WHY EVERY ENTRY IS A DIB AND NOT A PNG
#
# An .ico is a directory of images, and since Vista each may be either a BMP or
# a PNG. PNG is smaller and is the obvious choice right up until some shell
# surface renders it as a black square. The DIB path is what RSMultiTerm ships
# and what has been on this machine's taskbar for months, so it is the one with
# evidence behind it. The whole file is around 370 KB uncompressed, which is
# nothing next to the exe it is stamped into.
#
# WHY THERE IS A GROUND TILE UNDER THE MARK
#
# favicon.svg opens with a 64-unit rounded rect at rx 12 in the ground color,
# and PaintMark starts at the rules - so the two are the same drawing only from
# the tile inward. That is not an oversight in either. The lens is filled with
# the ground so the rules stop at the glass rather than running through it,
# which needs the ground to be behind it; inside the app it always is, because
# the mark is painted onto a panel already that color.
#
# An icon has no such luxury. It lands on Explorer's white, on a dark taskbar,
# on whatever a shortcut sits over - and with no tile the filled lens is a dark
# cutout on an unknown background, invisible the moment that background is also
# dark. So the tile is drawn, which also makes the icon match favicon.svg
# exactly rather than approximately.
#
# It is drawn by Brand.PaintTile, the app's own, rather than by geometry
# restated here - the same reason this renders PaintMark instead of the SVG.
# Brand.CreateIcon calls PaintTile too, so the window icon and the file icon
# are one picture rather than two that have to be kept in step.
#
# WHY THE COLORS ARE CHECKED AGAINST favicon.svg RATHER THAN JUST TAKEN
#
# favicon.svg states its reasoning: the hexes are static "deliberately",
# because a mark is an identity and people find it by sight. The theme,
# meanwhile, is a live thing that starts on "classic" and can be edited. Those
# two agree today. Reading the theme and asserting it still matches the
# favicon's palette keeps the icon generated from the app's own values while
# refusing to quietly ship a different identity if they ever diverge - the same
# move Make-Dist.ps1 makes when it compares three source lists instead of
# keeping a fourth.
#
# THE 16px CAVEAT, worth knowing before judging the result
#
# PaintMark scales every stroke off a 64-unit tile, so at 16px its 4-unit
# strokes land on a single pixel and fine detail turns to fuzz. RSMultiTerm
# solved this with a separate simplified mark for sizes at or below 32. Neither
# of these two tools has one. That is a known limit, not a bug in this script:
# if the small sizes ever read badly enough to matter, the fix is a reduced
# variant of PaintMark, not a change here.
[CmdletBinding()]
param(
    # Namespace and output name both follow the repo folder, which is how both
    # of these repos are laid out. Passing it explicitly is for anyone who
    # copies this script into a third.
    [string]$App = '',
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ($App -eq '') { $App = Split-Path -Leaf $root }
$out = Join-Path $root ("{0}.ico" -f $App)

Add-Type -AssemblyName System.Drawing

# The app is compiled here as a library so PaintMark can be called directly -
# it holds a Main, which a library is allowed; it simply is not an entry point.
#
# Both the sources and the /r: list are READ OUT OF Build-<App>.cmd rather than
# restated here, and that is not tidiness. RSPaster compiles against the WinRT
# metadata in System32\WinMetadata plus two GAC facades for its OCR grab, so a
# hand-copied reference list in this script was wrong for it on the very first
# run. The build script is the one place that knows how the app compiles, so it
# stays the one place - the same reasoning Make-Dist.ps1 gives for comparing
# source lists instead of keeping its own.
#
# Compilation goes through the same in-box csc the build uses rather than
# Add-Type's CodeDom, so whatever builds there builds here by construction. The
# result is loaded from bytes and the file deleted straight away, because
# loading an assembly by path locks it for the life of the process.
if (-not ([System.Management.Automation.PSTypeName]("$App.Brand")).Type) {
    $cmdPath = Join-Path $root ("Build-{0}.cmd" -f $App)
    if (-not (Test-Path -LiteralPath $cmdPath)) { throw "Missing $cmdPath" }
    $cmdText = [IO.File]::ReadAllText($cmdPath)

    # The %VARS% the batch file sets for itself, then the environment. Repeated
    # because they nest - RSPaster's SR is written in terms of GACM.
    $vars = @{}
    foreach ($m in [regex]::Matches($cmdText, '(?m)^\s*set\s+"([A-Za-z0-9_]+)=([^"]*)"')) {
        $vars[$m.Groups[1].Value] = $m.Groups[2].Value
    }
    function Expand-Cmd([string]$s) {
        for ($i = 0; $i -lt 6; $i++) {
            foreach ($k in $vars.Keys) { $s = $s.Replace("%$k%", $vars[$k]) }
            $s = [Environment]::ExpandEnvironmentVariables($s)
        }
        return $s
    }

    # Both CSC candidates, in the order the batch file prefers them.
    $csc = @([regex]::Matches($cmdText, 'set\s+"CSC=([^"]+)"') |
        ForEach-Object { Expand-Cmd $_.Groups[1].Value } |
        Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1
    if (-not $csc) { throw "No csc.exe at any path Build-$App.cmd names." }

    $refs = @([regex]::Matches($cmdText, '/r:(?:"([^"]+)"|([^\s"^]+))') | ForEach-Object {
        $v = if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value }
        '/r:' + (Expand-Cmd $v)
    })
    $sources = @([regex]::Matches($cmdText, '"%~dp0([A-Za-z0-9_]+\.cs)"') |
        ForEach-Object { Join-Path $root $_.Groups[1].Value })
    if ($sources.Count -eq 0) { throw "No sources listed in $cmdPath" }

    $dll = Join-Path ([IO.Path]::GetTempPath()) ("{0}-brand-{1}.dll" -f $App, $PID)
    try {
        & $csc /nologo /target:library "/out:$dll" $refs $sources
        if ($LASTEXITCODE -ne 0) { throw "Compile failed - see the compiler output above." }
        [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($dll)) | Out-Null
    } finally {
        if (Test-Path -LiteralPath $dll) { Remove-Item -LiteralPath $dll -Force }
    }
}

$brand = [Type]("$App.Brand")
$th    = [Type]("$App.Th")
if (-not $brand) { throw "$App.Brand did not compile - the mark lives there." }

$theme = $th::T
$logoA = $theme.LogoA
$logoB = $theme.LogoB
$ground = $theme.Panel

# The identity check. favicon.svg holds exactly three colors - the ground, and
# one stroke for each half of the mark - so comparing them as a SET says the
# palettes agree without this script having to know which attribute carries
# which, and without a fourth copy of the hexes living here.
$svgPath = Join-Path $root 'favicon.svg'
if (-not (Test-Path -LiteralPath $svgPath)) { throw "Missing $svgPath - the icon's palette is checked against it." }
$svg = [IO.File]::ReadAllText($svgPath, [Text.Encoding]::UTF8)
$inSvg = @([regex]::Matches($svg, '(?:fill|stroke)="(#[0-9a-fA-F]{6})"') |
    ForEach-Object { $_.Groups[1].Value.ToLower() } | Sort-Object -Unique)
$inTheme = @(@($ground, $logoA, $logoB) |
    ForEach-Object { [System.Drawing.ColorTranslator]::ToHtml($_).ToLower() } | Sort-Object -Unique)

if (Compare-Object $inSvg $inTheme) {
    # The parentheses around the concatenation are load-bearing: -f binds
    # tighter than +, so without them only the second half is formatted and the
    # message prints its own {0} and {1} instead of the colors that drifted.
    throw (("The mark's palette has drifted. favicon.svg has {0}; the classic theme has {1}. " +
            "They are the same identity and must agree - fix whichever one moved, then re-run.") -f
           ($inSvg -join ' '), ($inTheme -join ' '))
}


# One icon image, as the 32-bit bottom-up DIB an .ico entry expects: a 40-byte
# BITMAPINFOHEADER whose height is doubled to cover the AND mask, then the BGRA
# rows, then the mask itself. The mask is left as zeros because the alpha
# channel already carries transparency - it exists only because the format
# predates alpha and still insists on it.
function New-IconDib([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            $rect = New-Object System.Drawing.Rectangle(0, 0, $px, $px)
            $brand::PaintTile($g, $rect, $ground)
            $brand::PaintMark($g, $rect, $logoA, $logoB)
        } finally { $g.Dispose() }

        $lockRect = New-Object System.Drawing.Rectangle(0, 0, $px, $px)
        $data = $bmp.LockBits($lockRect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $stride = $data.Stride
            $topDown = New-Object byte[] ($stride * $px)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $topDown, 0, $topDown.Length)
        } finally { $bmp.UnlockBits($data) }
    } finally { $bmp.Dispose() }

    $rowBytes = $px * 4
    $xor = New-Object byte[] ($rowBytes * $px)
    for ($y = 0; $y -lt $px; $y++) {
        # DIB rows run bottom-up, so the last source row is written first.
        [Array]::Copy($topDown, ($px - 1 - $y) * $stride, $xor, $y * $rowBytes, $rowBytes)
    }

    $maskRow = [Math]::Ceiling($px / 32.0) * 4
    $andMask = New-Object byte[] ($maskRow * $px)

    $header = New-Object byte[] 40
    $w = New-Object System.IO.BinaryWriter (New-Object System.IO.MemoryStream(,$header))
    try {
        $w.Write([UInt32]40)                            # biSize
        $w.Write([Int32]$px)                            # biWidth
        $w.Write([Int32]($px * 2))                      # biHeight, color plus mask
        $w.Write([UInt16]1)                             # biPlanes
        $w.Write([UInt16]32)                            # biBitCount
        $w.Write([UInt32]0)                             # biCompression = BI_RGB
        $w.Write([UInt32]($xor.Length + $andMask.Length))
    } finally { $w.Dispose() }

    $blob = New-Object byte[] ($header.Length + $xor.Length + $andMask.Length)
    [Array]::Copy($header, 0, $blob, 0, $header.Length)
    [Array]::Copy($xor, 0, $blob, $header.Length, $xor.Length)
    [Array]::Copy($andMask, 0, $blob, $header.Length + $xor.Length, $andMask.Length)

    # Alpha coverage, so a mark that failed to draw cannot ship as a blank
    # square that looks like a missing icon rather than a broken build.
    $opaque = 0
    for ($i = 3; $i -lt $xor.Length; $i += 4) { if ($xor[$i] -ne 0) { $opaque++ } }

    return [pscustomobject]@{ Px = $px; Data = $blob; Opaque = $opaque; Total = $px * $px }
}

$entries = @(foreach ($px in ($Sizes | Sort-Object)) { New-IconDib $px })

$blank = @($entries | Where-Object { $_.Opaque -eq 0 })
if ($blank.Count -gt 0) {
    throw ("PaintMark drew nothing at: {0}px - refusing to write a blank icon." -f
        (($blank | ForEach-Object { $_.Px }) -join ', '))
}

# The mark stands on a filled tile, so an icon that is mostly transparent means
# the tile stopped being drawn. That failure is invisible in isolation - the
# mark still looks like the mark - and only shows itself later as an icon that
# dissolves into a dark taskbar. A stroke-only mark covers roughly a third of
# the square; a tiled one covers everything but the rounded corners.
$thin = @($entries | Where-Object { ($_.Opaque / $_.Total) -lt 0.9 })
if ($thin.Count -gt 0) {
    throw (("The ground tile is missing at {0}. Brand.PaintTile should fill all but " +
            "the rounded corners; coverage this low is a mark drawn on nothing.") -f
           (($thin | ForEach-Object { '{0}px ({1:P0})' -f $_.Px, ($_.Opaque / $_.Total) }) -join ', '))
}

# ICONDIR, then one 16-byte ICONDIRENTRY per image, then the images.
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
try {
    $bw.Write([UInt16]0)                 # reserved
    $bw.Write([UInt16]1)                 # type: icon
    $bw.Write([UInt16]$entries.Count)

    $offset = 6 + $entries.Count * 16
    foreach ($e in $entries) {
        # 256 is stored as 0: the field is one byte and 256 does not fit.
        $dim = if ($e.Px -ge 256) { 0 } else { $e.Px }
        $bw.Write([byte]$dim)            # width
        $bw.Write([byte]$dim)            # height
        $bw.Write([byte]0)               # palette size
        $bw.Write([byte]0)               # reserved
        $bw.Write([UInt16]1)             # planes
        $bw.Write([UInt16]32)            # bit depth
        $bw.Write([UInt32]$e.Data.Length)
        $bw.Write([UInt32]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Data, 0, $e.Data.Length) }
    $bw.Flush()
    [IO.File]::WriteAllBytes($out, $ms.ToArray())
} finally { $bw.Dispose(); $ms.Dispose() }

# Read the file back and check it two ways, because neither way is enough on
# its own.
#
# First the directory, parsed here rather than trusted: every requested size
# must be present, and each blob must be exactly the length its dimensions
# imply. That catches a truncated or misaligned write, which is the failure
# mode that still produces a file Explorer will happily show as a blank square.
#
# Then the loader, which proves Windows can actually decode what was written -
# but only up to 128px. System.Drawing.Icon cannot load a 256px entry at all
# and silently returns the 128 instead. That is the loader, not the file:
# RSMultiTerm's shipped icon.ico, which has been correct on this machine's
# taskbar for months, answers 128 to the same question. Asserting on it would
# be asserting that a working icon is broken.
$LOADER_MAX = 128

$bytes = [IO.File]::ReadAllBytes($out)
$count = [BitConverter]::ToUInt16($bytes, 4)
$problems = @()
if ($count -ne $entries.Count) { $problems += "directory lists $count entries, wrote $($entries.Count)" }

for ($i = 0; $i -lt $count; $i++) {
    $at = 6 + $i * 16
    $px = $bytes[$at]; if ($px -eq 0) { $px = 256 }     # 0 means 256
    $len = [BitConverter]::ToUInt32($bytes, $at + 8)
    $off = [BitConverter]::ToUInt32($bytes, $at + 12)

    $expect = 40 + ($px * $px * 4) + ([Math]::Ceiling($px / 32.0) * 4 * $px)
    if ($len -ne $expect) { $problems += "${px}px: blob is $len bytes, expected $expect" }
    if ($off + $len -gt $bytes.Length) { $problems += "${px}px: runs past the end of the file" }
    if ($bytes[$off] -ne 40) { $problems += "${px}px: not a BITMAPINFOHEADER" }

    if ($px -le $LOADER_MAX) {
        $probe = New-Object System.Drawing.Icon($out, $px, $px)
        try {
            if ($probe.Width -ne $px) { $problems += "${px}px: loader returned $($probe.Width)px" }
        } finally { $probe.Dispose() }
    }
}

if ($problems.Count -gt 0) {
    # Delete it rather than leave it: a build that runs next will stamp
    # whatever is sitting here, and a bad icon is not obvious once stamped.
    Remove-Item -LiteralPath $out -Force
    throw ("{0} was not a valid multi-size icon (deleted): {1}" -f
        (Split-Path -Leaf $out), ($problems -join '; '))
}

$kb = [Math]::Round((Get-Item -LiteralPath $out).Length / 1KB)
Write-Host ("{0} written - {1} sizes, {2} KB" -f (Split-Path -Leaf $out), $entries.Count, $kb)
foreach ($e in $entries) {
    Write-Host ("  {0,3}px  {1,6:P0} covered" -f $e.Px, ($e.Opaque / $e.Total))
}
Write-Host ("  theme: classic  logo-a {0}  logo-b {1}" -f
    [System.Drawing.ColorTranslator]::ToHtml($logoA),
    [System.Drawing.ColorTranslator]::ToHtml($logoB))
