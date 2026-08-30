# Builds dist\RSFind.zip: the files someone needs to run or rebuild RSFind,
# and nothing else.
#
# The exe is rebuilt first rather than zipped as found, because a distribution
# carrying a binary that does not match the sources beside it is worse than one
# carrying no binary at all.
#
# Left out deliberately: the repo plumbing (.git, .gitignore, .gitattributes),
# CHANGELOG.md, favicon.svg, docs\src\, and tools\ itself. None of it is needed
# to run or rebuild, and a dev script in a distribution just raises questions.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Make-Dist.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist '_stage'
$payload = Join-Path $stage 'RSFind'
$zip = Join-Path $dist 'RSFind.zip'

# The sources ship as a set read off disk rather than as a third hand-kept
# list. Build-RSFind.cmd and Run-From-Source.ps1 each carry one already, and
# DEPLOY.md warns that they can drift apart; a third copy here would add a
# third way to ship sources that cannot rebuild the exe sitting beside them.
#
# So the three are compared instead. A file added to the build and forgotten
# elsewhere fails the release rather than shipping, and a stray .cs dropped in
# the root fails too - an unexplained source file in a distribution is exactly
# the thing a reader of this zip would be right to ask about.
$onDisk = @(Get-ChildItem -LiteralPath $root -Filter '*.cs' -File |
    ForEach-Object { $_.Name })

$buildText = Get-Content -LiteralPath (Join-Path $root 'Build-RSFind.cmd') -Raw
$inBuild = @([regex]::Matches($buildText, '%~dp0([A-Za-z0-9_]+\.cs)') |
    ForEach-Object { $_.Groups[1].Value })

$runText = Get-Content -LiteralPath (Join-Path $root 'Run-From-Source.ps1') -Raw
$inRun = @([regex]::Matches($runText, "'([A-Za-z0-9_]+\.cs)'") |
    ForEach-Object { $_.Groups[1].Value })

foreach ($other in @(
        @{ Name = 'Build-RSFind.cmd';    List = $inBuild },
        @{ Name = 'Run-From-Source.ps1'; List = $inRun })) {
    $diff = Compare-Object -ReferenceObject ($onDisk | Sort-Object) `
                           -DifferenceObject ($other.List | Sort-Object)
    if ($diff) {
        $lines = $diff | ForEach-Object {
            if ($_.SideIndicator -eq '<=') {
                "  in the folder but not in {0}: {1}" -f $other.Name, $_.InputObject
            } else {
                "  in {0} but not in the folder: {1}" -f $other.Name, $_.InputObject
            }
        }
        throw ("Source lists disagree, so the zip would ship sources that do " +
               "not match the exe:`r`n" + ($lines -join "`r`n"))
    }
}

# Everything the zip ships, relative to the repo root. The two images are here
# only because README.md renders them, and a README showing broken images
# reads as a broken download. DEPLOY.md earns its place because the first
# thing a recipient of this zip hits is Windows refusing to run a downloaded
# exe, and that is the file that explains it.
$manifest = @('RSFind.exe') + $onDisk + @(
    'Run-From-Source.ps1'
    'Run-From-Source.cmd'
    'Build-RSFind.cmd'
    'README.md'
    'DEPLOY.md'
    'LICENSE'
    'docs\hero-quadrants.png'
    'docs\replace.png'
)

Write-Host 'Rebuilding the exe so it matches the sources shipped beside it...'
& (Join-Path $root 'Build-RSFind.cmd')
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Not packaging a stale binary.' }

# A missing file must stop the run. A zip that is quietly short of a file
# still unzips and still looks fine.
$missing = @()
foreach ($rel in $manifest) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $rel))) { $missing += $rel }
}
if ($missing.Count -gt 0) {
    throw ("Missing from the working tree: {0}" -f ($missing -join ', '))
}

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

foreach ($rel in $manifest) {
    $dest = Join-Path $payload $rel
    $destDir = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $root $rel) -Destination $dest
}

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $payload -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host ''
Write-Host 'Packaged:'
foreach ($rel in $manifest) {
    $size = (Get-Item -LiteralPath (Join-Path $root $rel)).Length
    Write-Host ("  {0,-24} {1,8:N0} bytes" -f $rel, $size)
}
$zipSize = (Get-Item -LiteralPath $zip).Length
Write-Host ''
Write-Host ("{0}  ({1:N0} bytes, {2} files)" -f $zip, $zipSize, $manifest.Count) -ForegroundColor Green

# The binary is unsigned and DEPLOY.md tells the recipient to Unblock-File it,
# which strips the one signal Windows had about where the file came from.
# Something has to be publishable to verify against first, or "unblock it" is
# the whole story. The exe hash is printed separately because that is the form
# AppLocker wants.
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$exeHash = (Get-FileHash -LiteralPath (Join-Path $root 'RSFind.exe') -Algorithm SHA256).Hash
Set-Content -LiteralPath "$zip.sha256" -Value ("{0}  RSFind.zip" -f $zipHash) -Encoding ascii

Write-Host ''
Write-Host 'SHA256 - publish these with the release:'
Write-Host ("  RSFind.zip  {0}" -f $zipHash)
Write-Host ("  RSFind.exe  {0}" -f $exeHash)
Write-Host ("  written to  {0}" -f "$zip.sha256")
