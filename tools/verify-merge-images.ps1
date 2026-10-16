# verify-merge-images.ps1 -- merge-pdf accepting images (slice 2026-10-15)
#
# What this covers (all measured from the PRODUCT's own output, never assumed):
#   S1  the tool layer: 1 image -> no merge-pdf; 2 images -> merge-pdf; 2 PDFs -> merge-pdf (unchanged)
#   S2  three images -> ONE pdf with THREE pages, in the confirmed order
#   S3  the three page-size modes (original / first / a4) measured page by page
#   S4  an image+PDF batch is refused by name and writes NO file
#   S5  the sort window shows the page-size choice for images and hides it for PDFs
#   S6  sources are byte-identical afterwards (sha256), and the output never overwrites a source
#   S7  the sort window can actually be rendered to a PNG (and a dump with no rows fails by name)
#
# ASCII only on purpose: a BOM-less CJK .ps1 is read as CP950 by PowerShell 5.1 and produces
# fake syntax errors (see the 2026-10-14 log entry). Keep this file pure ASCII.
#
# Usage: powershell -File tools\verify-merge-images.ps1 -Exe <path to Confern.exe> [-Work <dir>]

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$Work = (Join-Path $env:TEMP 'confern-merge-images')
)

$ErrorActionPreference = 'Continue'
$script:checks = 0
$script:pass = 0
$script:fail = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks++
    if ($ok) { $script:pass++; "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name  [$detail]" }
}

function Run([string[]]$argv, [string]$tag) {
    # The product is a WinExe: plain `& $exe` silently does nothing (no output, no exit code,
    # no log file). Start-Process is the only shape that reliably reports both.
    $log = Join-Path $Work "$tag.log"
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $a = $argv + @('--quiet', '--log', $log, '--tray-exit-after', '45000')
    $p = Start-Process -FilePath $Exe -ArgumentList $a -PassThru -Wait -WindowStyle Hidden
    [pscustomobject]@{ Exit = $p.ExitCode; Log = $log; Lines = @(if (Test-Path $log) { Get-Content $log -Encoding UTF8 }) }
}

function Sha([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.Substring(0, 16) }

function MakeImage([string]$path, [int]$w, [int]$h, [string]$colour) {
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $bmp.SetResolution(96, 96)   # pin the DPI so the expected point sizes are exact
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromName($colour))
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# ============================================================================================
# fixtures: three images with DIFFERENT sizes, all at a known 96 dpi
#   at 96 dpi,  pt = px * 72 / 96 = px * 0.75
# ============================================================================================
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$imgA = Join-Path $Work 'A-300x100.png'   # -> 225.0 x 75.0 pt
$imgB = Join-Path $Work 'B-80x200.png'    # ->  60.0 x 150.0 pt
$imgC = Join-Path $Work 'C-150x150.png'   # -> 112.5 x 112.5 pt
MakeImage $imgA 300 100 'Red'
MakeImage $imgB  80 200 'Green'
MakeImage $imgC 150 150 'Blue'

$A4W = 210.0 * 72.0 / 25.4               # 595.275...
$A4H = 297.0 * 72.0 / 25.4               # 841.889...

# Build the PDF fixtures ONCE, before anything else: they are inputs for S1/S4/S5, and an earlier
# version of this script deleted them with a broad `*.pdf` cleanup - it then failed on its own
# fixtures. The output of an image merge is always named after the FIRST source, so the cleanup
# below targets that name and nothing else.
$mk = Run @('--convert', $imgA, '--target', 'PDF') 'mk-pdf'
$mkOut = Get-ChildItem $Work -Filter 'A-300x100.pdf' | Select-Object -First 1
if (-not $mkOut) { "  FATAL  could not build a PDF fixture: $($mk.Lines -join ' | ')"; exit 1 }
$pdfA = Join-Path $Work 'p1.pdf'; $pdfB = Join-Path $Work 'p2.pdf'
Copy-Item $mkOut.FullName $pdfA -Force
Copy-Item $mkOut.FullName $pdfB -Force
Remove-Item $mkOut.FullName -Force

"=== S1  the tool layer decides by FORMAT and by FILE COUNT ==="
$one = Run @('--tools-list', '--wheel-payload', $imgA) 's1-one'
$two = Run @('--tools-list', '--wheel-payload', "$imgA;$imgB") 's1-two'

$oneLine = ($one.Lines | Select-String -Pattern 'tool list: count=' | Select-Object -First 1)
$twoLine = ($two.Lines | Select-String -Pattern 'tool list: count=' | Select-Object -First 1)
Check 'S1.1 one image -> merge-pdf is NOT offered' `
      ($oneLine -and $oneLine.ToString() -notmatch 'merge-pdf') "line=$oneLine"
Check 'S1.2 two images -> merge-pdf IS offered' `
      ($twoLine -and $twoLine.ToString() -match 'merge-pdf') "line=$twoLine"

# a PDF pair, to prove the 2026-10-07 behaviour did not change
$twoPdf = Run @('--tools-list', '--wheel-payload', "$pdfA;$pdfB") 's1-pdf'
$pdfLine = ($twoPdf.Lines | Select-String -Pattern 'tool list: count=' | Select-Object -First 1)
Check 'S1.3 two PDFs -> merge-pdf IS offered (unchanged)' `
      ($pdfLine -and $pdfLine.ToString() -match 'merge-pdf') "line=$pdfLine"

"=== S2  three images -> ONE pdf, THREE pages, in the given order ==="
$order = "$imgA;$imgB;$imgC"
$before = @{}; foreach ($p in @($imgA, $imgB, $imgC)) { $before[$p] = (Sha $p) }
Get-ChildItem $Work -Filter 'A-300x100*.pdf' | Remove-Item -Force
$run = Run @('--merge-pdf', $order) 's2'
$out = Get-ChildItem $Work -Filter 'A-300x100*.pdf' | Select-Object -First 1
Check 'S2.1 the merge reported success' (($run.Lines -join "`n") -match 'worker result: ok=True') `
      (($run.Lines | Select-String 'worker result' | Select-Object -First 1).ToString())
# Count only the OUTPUT name (the PDF fixtures are called *.pdf too). What this pins is that the
# batch runs ONCE - a per-file loop would leave N files behind and every other number would agree.
Check 'S2.2 exactly one output file appeared' ((Get-ChildItem $Work -Filter 'A-300x100*.pdf').Count -eq 1) `
      "outputs=$((Get-ChildItem $Work -Filter 'A-300x100*.pdf').Count)"
Check 'S2.3 the output keeps the FIRST source name' ($out -and $out.Name -eq 'A-300x100.pdf') `
      "name=$(if ($out) { $out.Name } else { '(none)' })"

$ins = Run @('--merge-pdf-inspect', $out.FullName) 's2-ins'
function Pages([object]$r) {
    @($r.Lines | Select-String -Pattern 'merge-pdf inspect: page=(\d+) size=([\d.]+)x([\d.]+)') |
        ForEach-Object {
            $m = [regex]::Match($_.ToString(), 'page=(\d+) size=([\d.]+)x([\d.]+)')
            [pscustomobject]@{ Page = [int]$m.Groups[1].Value; W = [double]$m.Groups[2].Value; H = [double]$m.Groups[3].Value }
        }
}
$pages = Pages $ins
Check 'S2.4 the file has 3 pages' ($pages.Count -eq 3) "pages=$($pages.Count)"
Check 'S2.5 page order matches the given order (original sizes)' `
      ($pages.Count -eq 3 -and
       [math]::Abs($pages[0].W - 225.0) -lt 0.2 -and [math]::Abs($pages[0].H -  75.0) -lt 0.2 -and
       [math]::Abs($pages[1].W -  60.0) -lt 0.2 -and [math]::Abs($pages[1].H - 150.0) -lt 0.2 -and
       [math]::Abs($pages[2].W - 112.5) -lt 0.2 -and [math]::Abs($pages[2].H - 112.5) -lt 0.2) `
      (($pages | ForEach-Object { "$($_.Page):$($_.W)x$($_.H)" }) -join ' ')

"=== S3  the three page-size modes ==="
$modeExpect = @{
    original = @(@(225.0, 75.0), @(60.0, 150.0), @(112.5, 112.5))
    first    = @(@(225.0, 75.0), @(225.0, 75.0),  @(225.0, 75.0))
    a4       = @(@($A4W, $A4H),  @($A4W, $A4H),   @($A4W, $A4H))
}
foreach ($mode in @('original', 'first', 'a4')) {
    Get-ChildItem $Work -Filter 'A-300x100*.pdf' | Remove-Item -Force
    $r = Run @('--merge-pdf', $order, '--merge-page-size', $mode) "s3-$mode"
    $o = Get-ChildItem $Work -Filter 'A-300x100*.pdf' | Select-Object -First 1
    $pg = Pages (Run @('--merge-pdf-inspect', $o.FullName) "s3-$mode-ins")
    $want = $modeExpect[$mode]
    $ok = ($pg.Count -eq 3)
    if ($ok) {
        for ($i = 0; $i -lt 3; $i++) {
            if ([math]::Abs($pg[$i].W - $want[$i][0]) -gt 0.2 -or [math]::Abs($pg[$i].H - $want[$i][1]) -gt 0.2) { $ok = $false }
        }
    }
    $got = ($pg | ForEach-Object { "$($_.Page):$($_.W)x$($_.H)" }) -join ' '
    $exp = (0..2 | ForEach-Object { "$($_+1):$([math]::Round($want[$_][0],1))x$([math]::Round($want[$_][1],1))" }) -join ' '
    Check "S3.$mode every page has the expected size" $ok "got=$got want=$exp"
    Check "S3.$mode the log names the mode" (($r.Lines -join "`n") -match "pageSize=$mode") `
          (($r.Lines | Select-String 'pageSize=' | Select-Object -First 1).ToString())
}

"=== S4  an image mixed with a PDF is refused, and nothing is written ==="
Get-ChildItem $Work -Filter 'A-300x100*.pdf' | Remove-Item -Force
$mix = Run @('--merge-pdf', "$imgA;$pdfA") 's4'
$mixText = $mix.Lines -join "`n"
# The engine's own words live in the WORKER's log file (a different file from the diagnostic's),
# so these assertions use what the diagnostic actually shows: the worker's result packet.
Check 'S4.1 the worker reported failure' ($mixText -match 'worker result: ok=False') `
      (($mix.Lines | Select-String 'worker result' | Select-Object -First 1).ToString())
Check 'S4.2 the failure is a named refusal, not a crash' ($mixText -match 'error=Unsupported') `
      (($mix.Lines | Select-String 'worker result' | Select-Object -First 1).ToString())
Check 'S4.3 no output file appeared' ((Get-ChildItem $Work -Filter 'A-300x100*.pdf').Count -eq 0) `
      "pdfs=$((Get-ChildItem $Work -Filter 'A-300x100*.pdf').Count)"

"=== S5  the sort window shows the page-size choice only for images ==="
$uiImg = Run @('--merge-ui-selftest', $order) 's5-img'
$uiPdf = Run @('--merge-ui-selftest', "$pdfA;$pdfB") 's5-pdf'
Check 'S5.1 the choice is SHOWN for an image batch' `
      (($uiImg.Lines -join "`n") -match 'the page-size choice is shown') 'not shown'
Check 'S5.2 the choice is HIDDEN for a PDF batch' `
      (($uiPdf.Lines -join "`n") -match 'the page-size choice is not shown') 'was shown'
Check 'S5.3 the window self-test is green with three files (20 sub-checks)' `
      (($uiImg.Lines -join "`n") -match 'merge ui selftest done: ok=True') `
      (($uiImg.Lines | Select-String 'selftest done' | Select-Object -First 1).ToString())

"=== S6  the sources are untouched ==="
$untouched = $true; $detail = @()
foreach ($p in @($imgA, $imgB, $imgC)) {
    $now = Sha $p
    if ($now -ne $before[$p]) { $untouched = $false }
    $detail += "$(Split-Path $p -Leaf):$($before[$p])->$now"
}
Check 'S6.1 every source is byte-identical (sha256)' $untouched ($detail -join ' ')
$summary = ($run.Lines | Select-String -Pattern 'merge-pdf summary' | Select-Object -First 1)
Check 'S6.2 the run reported one output from three sources' `
      ($summary -and $summary.ToString() -match '1 output from 3 source') "line=$summary"

"=== S7  the window can actually be rendered to a file (item 4b) ==="
# --merge-ui-dump was unusable in BOTH of its documented forms, and neither failure was loud:
#   * on its own there are no rows to draw - the rows come from --merge-ui-selftest - so the window
#     refuses to open, and the run failed after 9.1 s with "there is nothing to order";
#   * together with --merge-ui-selftest it took the SELFTEST branch and never reached the dump at
#     all: exit 0, no file, and not one line in the log. The silence is the defect, not the PNG.
# The crop window's --crop-ui-dump works because its rows come from a DIFFERENT flag
# (--crop-source) - which is why "the row order comes from --merge-ui-selftest" was documented
# and still broken.
$dump = Join-Path $Work 'merge-window.png'
Remove-Item $dump -Force -ErrorAction SilentlyContinue
$dumpRun = Run @('--merge-ui-selftest', $order, '--merge-ui-dump', $dump) 's7-dump'
$dumpBytes = 0
if (Test-Path $dump) { $dumpBytes = (Get-Item $dump).Length }
Check 'S7.1 the documented invocation writes a real PNG (exit 0, and a file > 5 KB)' `
      (($dumpRun.Exit -eq 0) -and ($dumpBytes -gt 5000)) `
      "exit=$($dumpRun.Exit) bytes=$dumpBytes"
$dumpText = $dumpRun.Lines -join "`n"
# The audit called the old name of this check an overclaim: it promised "a blank frame cannot pass"
# while only asserting that the words "N distinct colours" appear - and "0 distinct colours" matches
# that too. The number is now read out and given a floor: a frame carrying only the window chrome
# would sit in the low tens, the real window measured 590.
$colours = [regex]::Match($dumpText, '(\d+) distinct colours')
$colourCount = if ($colours.Success) { [int]$colours.Groups[1].Value } else { -1 }
Check "S7.2 the dump reports its size and colour count, and the count has a floor ($colourCount > 50)" `
      (($colours.Success) -and ($colourCount -gt 50)) `
      (($dumpRun.Lines | Select-String 'merge ui dump' | Select-Object -First 1).ToString())
# ④'s first fix made the dump work by taking "is a selftest running?" out of the picture - which meant
# giving BOTH flags silently stopped running the selftest, while the new help teaches that exact
# combination (audit finding). Two flags, two jobs: the dump AND the selftest.
Check 'S7.4 giving both flags also runs the selftest (it used to be skipped in silence)' `
      ($dumpText -match 'merge ui selftest done: ok=True') `
      "selftestLines=$(@($dumpRun.Lines | Select-String 'merge ui selftest').Count)"

$bare = Join-Path $Work 'merge-window-bare.png'
Remove-Item $bare -Force -ErrorAction SilentlyContinue
$bareRun = Run @('--merge-ui-dump', $bare) 's7-norows'
$bareText = $bareRun.Lines -join "`n"
Check 'S7.3 a dump with no rows FAILS BY NAME instead of writing an empty image' `
      (($bareRun.Exit -ne 0) -and (-not (Test-Path $bare)) -and
       ($bareText -match 'needs --merge-ui-selftest with at least 2 existing files')) `
      "exit=$($bareRun.Exit) exists=$(Test-Path $bare)"

""
"checks=$script:checks pass=$script:pass fail=$script:fail"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
