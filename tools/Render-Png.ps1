# Renders an HTML file to a PNG at an exact pixel size, using the headless
# Chrome or Edge already installed on the machine.
#
# The hero and social preview images are composed as HTML rather than drawn by
# hand: the layout is text, so it can be diffed, corrected and re-rendered, and
# a typo in a caption is a one-line fix rather than an image edit. Section 11 of
# the conventions is the reason it matters - an image cannot be grepped once
# links to it are cached.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Render-Png.ps1 `
#       -Html docs\src\social.html -Out docs\social-preview.png -Width 1280 -Height 640

param(
    [Parameter(Mandatory = $true)][string]$Html,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][int]$Width,
    [Parameter(Mandatory = $true)][int]$Height,
    [double]$Scale = 1.0
)

$ErrorActionPreference = 'Stop'

$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $browser) { throw "No Chrome or Edge found to render with." }

$htmlPath = (Resolve-Path $Html).Path
$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out }
           else { [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Out)) }
$outDir = Split-Path -Parent $outPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
if (Test-Path $outPath) { Remove-Item $outPath -Force }

$uri = 'file:///' + ($htmlPath -replace '\\', '/')
$args = @(
    '--headless=new'
    '--disable-gpu'
    '--hide-scrollbars'
    '--default-background-color=00000000'
    ("--force-device-scale-factor=$Scale")
    ("--window-size=$Width,$Height")
    ("--screenshot=$outPath")
    $uri
)

# Start-Process rather than the call operator: headless Chrome writes progress
# to stderr, and Windows PowerShell turns any native stderr line into an
# ErrorRecord, which fails the script on a render that actually succeeded.
$log = [System.IO.Path]::GetTempFileName()
Start-Process -FilePath $browser -ArgumentList $args -Wait -NoNewWindow `
    -RedirectStandardError $log -RedirectStandardOutput "$log.out" | Out-Null
Remove-Item $log, "$log.out" -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $outPath)) { throw "Render produced no file: $outPath" }

Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($outPath)
$w = $img.Width; $h = $img.Height
$img.Dispose()
Write-Host ("rendered {0}  ({1} x {2})" -f $Out, $w, $h) -ForegroundColor Green

# A size mismatch means the page overflowed its frame, which silently crops the
# artwork; better to fail than to publish a cropped preview.
$expectedW = [int][math]::Round($Width * $Scale)
$expectedH = [int][math]::Round($Height * $Scale)
if ($w -ne $expectedW -or $h -ne $expectedH) {
    throw ("Expected {0} x {1} but got {2} x {3}." -f $expectedW, $expectedH, $w, $h)
}
