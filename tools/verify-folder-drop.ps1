# verify-folder-drop.ps1 -- dropping a FOLDER onto the wheel (slice 2026-10-15)
#
# What this covers (every number is read back from the product's own log):
#   S1  a folder expands: count / skipped / recursion / junk filtered
#   S2  the three verdicts a folder can produce (Ready+merge-pdf / Mixed / one image -> no merge)
#   S3  natural sort: scan2 must come BEFORE scan10 in the page order
#   S4  a directly dropped FILE list keeps the order the user gave (and is never filtered)
#   S5  the expansion is idempotent (a second pass over files re-sorts nothing)
#   S6  peak memory does NOT grow linearly with the page count
#   S7  a Mixed batch says WHICH file is the odd one out, and the summary cannot grow into a page
#   S8  a 200-page merge reports progress, and writes the batch exactly once (no wasted pass)
#   S9  the two limits actually fire: 1000 files (capped) and 8 levels deep (and both say so)
#
# ASCII only on purpose: a BOM-less CJK .ps1 is read as CP950 by PowerShell 5.1 and produces
# fake syntax errors. All assertions below use the ASCII fields of the product's log
# (in= / folders= / out= / skipped= / capped= / order=[...]), never a localized message.
#
# Usage: powershell -File tools\verify-folder-drop.ps1 -Exe <path to Confern.exe> [-Work <dir>]

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$Work = (Join-Path $env:TEMP 'confern-folder-drop')
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
    # Confern is a WinExe: plain `& $exe` silently does nothing. Start-Process is the only shape
    # that reports both the exit code and a log. --tray-exit-after is mandatory or it never ends.
    $log = Join-Path $Work "$tag.log"
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $a = $argv + @('--quiet', '--log', $log, '--tray-exit-after', '90000')
    Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $Exe -ArgumentList $a -PassThru -Wait -WindowStyle Hidden
    [pscustomobject]@{ Exit = $p.ExitCode; Log = $log; Lines = @(if (Test-Path $log) { Get-Content $log -Encoding UTF8 }) }
}

function Line([object]$r, [string]$pattern) {
    ($r.Lines | Select-String -Pattern $pattern | Select-Object -First 1)
}

# Any line matches (used where the SAME run logs the same shape twice: the first expansion and
# then the idempotent second pass - Select-Object -First 1 would silently give the wrong one).
function Has([object]$r, [string]$pattern) {
    [bool]($r.Lines | Select-String -Pattern $pattern -Quiet)
}

# The sort window's line is "... in the given order =[a.png,b.png]" - note the SPACE before '='.
# Reading the order back with 'order=\[' finds nothing at all (that is how the first version of
# this suite reported order=[] on a run that had plainly worked).
function OrderOf([object]$r) {
    $l = Line $r 'window created for'
    if (-not $l) { return '' }
    $m = [regex]::Match($l.ToString(), 'order\s*=\s*\[([^\]]*)\]')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

# The notice field, e.g. the localized "pick one kind" sentence. The line that carries it ends
# differently per call site - the wheel line ends it with " latency=", the --tools-list line with
# ", runtime classification" - so the caller passes the stop pattern. Callers assert only on ASCII
# INSIDE the sentence (format labels, fixture file names, a count digit): the sentence is localized
# and a CJK literal in this file would be read as CP950 by PowerShell 5.1.
function NoticeOf([object]$r, [string]$anchor, [string]$end) {
    $l = Line $r $anchor
    if (-not $l) { return '' }
    $m = [regex]::Match($l.ToString(), 'notice=(.*?)' + $end)
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

# The merge runs in a SEPARATE worker process, and everything it says about its own work (progress,
# per-file retries, the "merged N image(s)" summary) is written to that process's OWN log file,
# flushed a line at a time. Read THAT one, found by "written during this run".
#
# Do NOT read the shared worker.log: it is a MIRROR that is flushed in batches, and this suite
# kills Confern with Stop-Process -Force right after "worker result" shows up. A Force kill skips
# Dispose, so the mirrored lines are lost - measured: the shared file grew by 0 lines for every
# one of this suite's runs, while the hand-run merges (which exit by themselves) all showed up in
# it. The first version of these checks read the mirror and reported "no progress, no merge" for a
# run that had plainly produced a 200-page PDF.
function WorkerLogFor([datetime]$since) {
    $dir = Join-Path $env:LOCALAPPDATA 'Confern'
    if (-not (Test-Path $dir)) { return @() }
    $f = Get-ChildItem $dir -Filter 'worker-*.log' -ErrorAction SilentlyContinue |
         Where-Object { $_.LastWriteTime -ge $since } |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    return @(Get-Content $f.FullName -Encoding UTF8 -ErrorAction SilentlyContinue)
}

function MakeImage([string]$path, [int]$w, [int]$h) {
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $bmp.SetResolution(96, 96)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::LightBlue)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# ============================================================================================
# fixtures
# ============================================================================================
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$imgA = Join-Path $Work 'plain.png'
MakeImage $imgA 120 80

# a scan folder: two images at the top, one inside a SUBfolder, plus junk that must be skipped
$scan = Join-Path $Work 'scan-batch'
New-Item -ItemType Directory -Force -Path (Join-Path $scan 'part2') | Out-Null
MakeImage (Join-Path $scan 'scan10.png') 200 100
MakeImage (Join-Path $scan 'scan2.png')  100 200
MakeImage (Join-Path $scan 'part2\scan1.png') 60 60
Set-Content (Join-Path $scan 'notes.txt') 'not an image' -Encoding ASCII
Set-Content (Join-Path $scan 'Thumbs.db') 'junk' -Encoding ASCII

# a folder holding exactly ONE image (must NOT offer merge-pdf) and a mixed one
$solo = Join-Path $Work 'solo'; New-Item -ItemType Directory -Force -Path $solo | Out-Null
MakeImage (Join-Path $solo 'only.png') 70 70
$pdfPair = Join-Path $Work 'pdfs'; New-Item -ItemType Directory -Force -Path $pdfPair | Out-Null
[void](Run @('--convert', $imgA, '--target', 'PDF') 'mk-pdf')
$made = Get-ChildItem $Work -Filter 'plain.pdf' | Select-Object -First 1
if (-not $made) { "  FATAL  could not build a PDF fixture"; exit 1 }
Copy-Item $made.FullName (Join-Path $pdfPair 'one.pdf') -Force
Copy-Item $made.FullName (Join-Path $pdfPair 'two.pdf') -Force
$mixed = Join-Path $Work 'mixed'; New-Item -ItemType Directory -Force -Path $mixed | Out-Null
MakeImage (Join-Path $mixed 'shot.png') 90 90
Copy-Item $made.FullName (Join-Path $mixed 'doc.pdf') -Force

"=== S1  a folder expands (count / skipped / recursion / junk) ==="
# NOTE: this section has to go through the REAL payload path. --tools-list logs nothing of its
# own (it is handed a null logger and only folds the notice into its "how" text, which is
# localized), so the ASCII counters below would never appear there.
# NOTE: the merge writes its output NEXT TO THE FIRST SOURCE - i.e. straight into the folder we just
# dropped. Dropping $scan itself would leave a PDF inside it, and every later --tools-list run
# would then see images AND that PDF and report Mixed (that is exactly how the first version of
# this suite broke its own fixture). So the merge gets a pristine copy of its own.
$scanMerge = Join-Path $Work 'scan-merge'
Copy-Item $scan $scanMerge -Recurse -Force
$r = Run @('--wheel-payload', $scanMerge, '--wheel-tools', '--wheel-tool', 'merge-pdf',
           '--merge-ui-autoconfirm') 's1'
$expand = Line $r 'payload paths: in='
$ok = $expand -and ($expand.ToString() -match 'in=1 folders=1 out=3 skipped=2 capped=False')
Check 'S1.1 one folder in -> three files out, two skipped' $ok "line=$expand"
Check 'S1.2 recursion reached the subfolder (3 not 2)' ($expand -and $expand.ToString() -match 'out=3') "line=$expand"
Check 'S1.3 the two junk files were skipped, not taken' ($expand -and $expand.ToString() -match 'skipped=2') "line=$expand"
Check 'S1.4 the cap was not hit' ($expand -and $expand.ToString() -match 'capped=False') "line=$expand"

"=== S2  the three verdicts a folder can produce ==="
$listRun = Run @('--tools-list', '--wheel-payload', $scan) 's1-list'
$scanList = Line $listRun 'tool list: count='
Check 'S2.1 a folder of images is Ready and OFFERS merge-pdf' `
      ($scanList -and $scanList.ToString() -match 'status=Ready' -and $scanList.ToString() -match 'merge-pdf') "line=$scanList"

$soloRun = Run @('--tools-list', '--wheel-payload', $solo) 's2-solo'
$soloList = Line $soloRun 'tool list: count='
Check 'S2.2 a folder holding ONE image does NOT offer merge-pdf' `
      ($soloList -and $soloList.ToString() -match 'status=Ready' -and $soloList.ToString() -notmatch 'merge-pdf') "line=$soloList"

$mixedRun = Run @('--tools-list', '--wheel-payload', $mixed) 's2-mixed'
$mixedList = Line $mixedRun 'tool list: count='
Check 'S2.3 an image mixed with a PDF is Mixed' ($mixedList -and $mixedList.ToString() -match 'status=Mixed') "line=$mixedList"

$pdfRun = Run @('--tools-list', '--wheel-payload', $pdfPair) 's2-pdf'
$pdfList = Line $pdfRun 'tool list: count='
Check 'S2.4 a folder of PDFs still offers merge-pdf (2026-10-07 behaviour unchanged)' `
      ($pdfList -and $pdfList.ToString() -match 'merge-pdf') "line=$pdfList"

"=== S3  natural sort: scan2 before scan10 (page order!) ==="
$ui = $r
$order = OrderOf $ui
Check 'S3.1 the sort window opened with all three files' `
      ($order -eq 'scan1.png,scan2.png,scan10.png') "order=[$order]"
Check 'S3.2 scan2 comes before scan10 (a dictionary sort would swap them)' `
      ($order -match 'scan2\.png,scan10\.png') "order=[$order]"
$merged = Line $ui 'worker result:'
Check 'S3.3 the folder merge actually produced a file' ($merged -and $merged.ToString() -match 'ok=True') "line=$merged"

"=== S4  directly dropped files keep the given order and are never filtered ==="
$rev = Run @('--wheel-payload', "$imgA;$scan\scan10.png", '--wheel-tools', '--wheel-tool', 'merge-pdf',
             '--merge-ui-autoconfirm') 's4'
$revOrder = OrderOf $rev
Check 'S4.1 two dropped FILES keep the order they were given in' `
      ($revOrder -eq 'plain.png,scan10.png') "order=[$revOrder]"

$junk = Run @('--tools-list', '--wheel-payload', "$imgA;$Work\scan-batch\notes.txt") 's4-junk'
$junkLine = Line $junk 'tool list: count='
Check 'S4.2 a dropped file is kept even if unsupported (it becomes Mixed, not Empty)' `
      ($junkLine -and $junkLine.ToString() -match 'status=Mixed') "line=$junkLine"

"=== S5  the expansion is idempotent ==="
Check 'S5.1 a batch of FILES is never treated as a folder (folders=0, no re-sort)' `
      (Has $rev 'payload paths: in=2 folders=0 out=2 skipped=0') `
      "no 'in=2 folders=0 out=2' line in the S4 run"

"=== S7  a Mixed batch must say WHICH file is the odd one out (item 1, 2026-10-15) ==="
# The bare "pick one kind" sentence never said WHICH file was the odd one - and in the case that
# started this, the odd file was the product's OWN previous output sitting in that folder, so the
# user had no way to tell it apart from a file he had put there himself.
$odd = Join-Path $Work 'odd-one'
New-Item -ItemType Directory -Force -Path $odd | Out-Null
MakeImage (Join-Path $odd 'page1.png') 100 100
MakeImage (Join-Path $odd 'page2.png') 100 100
Copy-Item $made.FullName (Join-Path $odd 'odd.pdf') -Force

# The REAL payload path: this is the line the wheel shows. The dump and the log stay OUTSIDE the
# dropped folder so the fixture keeps exactly the three files the user put there (S1 got this wrong
# once: the merge wrote its PDF into the folder it was merging, and the next run saw a mixed folder).
$wr = Run @('--wheel-payload', $odd, '--wheel-dump', (Join-Path $Work 's7-wheel.png')) 's7-wheel'
$wheelLine = Line $wr 'wheel from OLE payload:'
Check 'S7.1 2 PNG + 1 PDF is still Mixed (item 1 explains, it does not re-judge)' `
      ($wheelLine -and $wheelLine.ToString() -match 'status=Mixed') "line=$wheelLine"

$note = NoticeOf $wr 'wheel from OLE payload:' '\s+latency='
Check 'S7.2 the notice names both categories that are in the batch' `
      ($note -match 'PNG' -and $note -match 'PDF') "notice=[$note]"
Check 'S7.3 it names the MINORITY file - the one the user has to take out' `
      ($note -match 'odd\.pdf') "notice=[$note]"
Check 'S7.4 it does not accuse the majority files' ($note -notmatch 'page1\.png') "notice=[$note]"
Check 'S7.5 every category carries its real count (2 PNG, 1 PDF)' `
      ($note -match 'PNG.{0,3}2' -and $note -match 'PDF.{0,3}1') "notice=[$note]"

# Both call sites must print the SAME sentence. Two copies of the wording is exactly how the old
# sentence drifted between the wheel and --tools-list in the first place.
$oddRun = Run @('--tools-list', '--wheel-payload', $odd) 's7-list'
$oddNote = NoticeOf $oddRun 'tool list: count=' ',\s'
Check 'S7.6 the tool list prints the identical sentence (one owner, two call sites)' `
      (($oddNote.Length -gt 0) -and ($oddNote -eq $note)) "wheel=[$note] list=[$oddNote]"
Check 'S7.7 a batch of ONE category raises no mixed notice at all (no false alarm)' `
      ((NoticeOf $listRun 'tool list: count=' ',\s') -eq '-') `
      "scan notice=[$(NoticeOf $listRun 'tool list: count=' ',\s')]"

# The cap. A FOLDER cannot be used here: expansion drops unsupported files, so all five categories
# survive only when the files are dropped directly (that asymmetry is S4.2's contract, not a bug).
$fiveDir = Join-Path $Work 'five-kinds'
New-Item -ItemType Directory -Force -Path $fiveDir | Out-Null
foreach ($f in @('a.png', 'b.pdf', 'c.mp4', 'd.mp3', 'e.xyz')) {
    Set-Content (Join-Path $fiveDir $f) 'x' -Encoding ASCII
}
$fivePayload = (@('a.png', 'b.pdf', 'c.mp4', 'd.mp3', 'e.xyz') |
                ForEach-Object { Join-Path $fiveDir $_ }) -join ';'
$fiveRun = Run @('--tools-list', '--wheel-payload', $fivePayload) 's7-five'
$fiveNote = NoticeOf $fiveRun 'tool list: count=' ',\s'
Check 'S7.8 five categories: only three are listed, the rest is summarised' `
      ($fiveNote -match 'MP4' -and $fiveNote -notmatch 'MP3') "notice=[$fiveNote]"
Check 'S7.9 the summary says how many there really were, and stays one short line' `
      (($fiveNote -match '5') -and ($fiveNote.Length -lt 60)) `
      "notice=[$fiveNote] len=$($fiveNote.Length)"

"=== S6  peak memory must NOT grow linearly with the page count ==="
$scale = Join-Path $Work 'scale'
$base = Join-Path $scale 'base.png'
New-Item -ItemType Directory -Force -Path $scale | Out-Null
MakeImage $base 3000 2400     # 7.2 MP, like a real scan
$peaks = @{}
$workerTails = @{}
foreach ($n in @(20, 200)) {
    $sub = Join-Path $scale "n$n"
    New-Item -ItemType Directory -Force -Path $sub | Out-Null
    1..$n | ForEach-Object { Copy-Item $base (Join-Path $sub ("p{0:d4}.png" -f $_)) -Force }
    $list = (Get-ChildItem $sub -Filter *.png | Sort-Object Name | ForEach-Object { $_.FullName }) -join ';'
    $log = Join-Path $Work "scale-$n.log"
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $runStart = Get-Date
    Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $pr = Start-Process -FilePath $Exe -ArgumentList @('--merge-pdf', $list, '--quiet', '--log', $log,
        '--tray-exit-after', '300000') -PassThru -WindowStyle Hidden

    # The real work happens in a SEPARATE worker process, so watching the process we launched
    # reports ~0 MB (measured - that is how the first attempt went wrong). Watch them all.
    $peak = 0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 240) {
        foreach ($proc in (Get-Process Confern -ErrorAction SilentlyContinue)) {
            try { if ($proc.PeakWorkingSet64 -gt $peak) { $peak = $proc.PeakWorkingSet64 } } catch { }
        }
        if ((Test-Path $log) -and (Get-Content $log -Encoding UTF8 -ErrorAction SilentlyContinue |
                                   Select-String 'worker result' -Quiet)) { break }
        Start-Sleep -Milliseconds 250
    }
    $pr.WaitForExit(30000) | Out-Null
    $peaks[$n] = [math]::Round($peak / 1MB, 0)
    Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    $workerTails[$n] = WorkerLogFor $runStart.AddSeconds(-1)
    Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$p20 = $peaks[20]; $p200 = $peaks[200]
Check 'S6.1 both scale runs finished' (($p20 -gt 0) -and ($p200 -gt 0)) "20=$p20 MB 200=$p200 MB"
$ratio = if ($p20 -gt 0) { $p200 / $p20 } else { 99 }
Check "S6.2 10x the pages does not cost 10x the memory (ratio $([math]::Round($ratio,2)))" `
      ($ratio -lt 1.6) "20=$p20 MB 200=$p200 MB ratio=$([math]::Round($ratio,2))"

"=== S8  a 200-page merge must say where it is, and must not do the work twice (item 2) ==="
# Everything below is read from the WORKER's own log for the 200-page run just above. Two
# different things are asserted, and the second one is not a nicety:
#   1. the pill gets one message per page (that is what makes "37/200" move), while the LOG stays
#      throttled - the user has said in so many words that repeated identical lines annoy him;
#   2. the batch is written ONCE. On 2026-10-16 this run exposed the opposite: the images branch
#      passed the SOURCE file as the first output name, so attempt 1 always hit
#      ERROR_ALREADY_EXISTS (0x800700B7) and the whole batch was decoded and laid out a second
#      time. 200 pages took 23.6 s instead of 11.5 s, and the log line blamed "another process".
$w200 = $workerTails[200]
$w200Text = ($w200 -join "`n")
Check 'S8.0 the 200-page run found ITS OWN worker log (otherwise every check below is vacuous)' `
      (@($w200 | Select-String 'merge-pdf:').Count -ge 1) "lines=$(@($w200).Count)"
Check 'S8.1 the batch was written exactly ONCE (no wasted pass, no name-collision retry)' `
      (((@($w200 | Select-String 'write retry')).Count -eq 0) -and
       ((@($w200 | Select-String 'merged 200 image\(s\)')).Count -eq 1)) `
      "retries=$((@($w200 | Select-String 'write retry')).Count) merged=$((@($w200 | Select-String 'merged ')).Count)"

$report = Line ([pscustomobject]@{ Lines = $w200 }) 'progress reporting:'
$sent = if ($report) { [regex]::Match($report.ToString(), '(\d+) message\(s\) on the pipe') } else { $null }
Check 'S8.2 the pipe carried one progress message per page (200)' `
      ($sent -and $sent.Success -and ($sent.Groups[1].Value -eq '200')) "reporting=[$report]"

# The pill redraws in place (ProgressPresenter.Update only swaps the snapshot), so the log is the
# only place a line count can be counted. 10 = one per 10%, plus the closing "reporting" line.
$progressLines = @($w200 | Select-String 'merge-pdf: progress \d+/\d+ ')
Check 'S8.3 the log stays throttled: at most 12 lines for 200 pages, not 200' `
      ($progressLines.Count -le 12) "progressLines=$($progressLines.Count)"

$seen = @()
foreach ($l in $progressLines) {
    $m = [regex]::Match($l.ToString(), 'progress (\d+)/(\d+)')
    if ($m.Success) { $seen += [int]$m.Groups[1].Value }
}
$increasing = $true
for ($i = 1; $i -lt $seen.Count; $i++) { if ($seen[$i] -le $seen[$i - 1]) { $increasing = $false } }
Check 'S8.4 the reported progress only ever moves forward and ends at the last page' `
      (($seen.Count -ge 4) -and $increasing -and ($seen[$seen.Count - 1] -eq 200)) `
      "seen=[$($seen -join ',')]"

# The outcome (not the progress) is what the user ends up with: a real 200-page PDF.
$mergedPdf = Join-Path $scale 'n200\p0001.pdf'
$ins = Run @('--merge-pdf-inspect', $mergedPdf) 's8-inspect'
$insLine = Line $ins 'merge-pdf inspect done:'
Check 'S8.5 the output is still one 200-page PDF (progress reporting changed nothing about it)' `
      ($insLine -and $insLine.ToString() -match 'pages=200 read=200 ok=True') "inspect=[$insLine]"

"=== S9  the two limits: 1000 files and 8 levels deep (item 3) ==="
# Both limits were only ever exercised as "not hit" (capped=False). A limit that has never fired is
# a promise with no evidence - and the DEPTH limit was worse than that: Collect() returned at the
# top level with no log line and no notice, so a deep folder quietly produced a SHORT merge whose
# page count, names and preview all still looked right.
#
# These runs use --payload-expand, not --wheel-payload. That is not a shortcut: --wheel-payload
# also opens the wheel and then DRIVES it (rotate, press Enter) - that is its job - so asking it
# "what did the folder expand to" costs a real conversion of the whole batch INTO THE SOURCE
# FOLDER. Measured: the 1001-file folder below came back with 5001 files (jpg/bmp/tif alongside
# every source). --payload-expand expands through the same owner (PayloadPaths) and writes nothing.
$cap = Join-Path $Work 'cap-1001'
New-Item -ItemType Directory -Force -Path $cap | Out-Null
MakeImage (Join-Path $Work 'cap-base.png') 90 90
1..1001 | ForEach-Object { Copy-Item (Join-Path $Work 'cap-base.png') (Join-Path $cap ("c{0:d4}.png" -f $_)) -Force }
$capBefore = (Get-ChildItem $cap -Recurse -File).Count
$capRun = Run @('--payload-expand', $cap) 's9-cap'
$capLine = Line $capRun 'payload expand:'
Check 'S9.1 the cap fires at 1000 files, not at 1001 and not later' `
      ($capLine -and $capLine.ToString() -match 'out=1000 .*truncated=True') `
      "files=$capBefore line=$capLine"
Check 'S9.2 the capped notice says which limit was hit (it names 1000)' `
      ((Line $capRun 'payload expand notice:') -match '1000') `
      "notice=[$(Line $capRun 'payload expand notice:')]"
Check 'S9.3 a capped batch is still usable - truncation is not a failure' `
      ($capLine -and $capLine.ToString() -match 'status=Ready') "line=$capLine"
Check 'S9.4 the diagnostic itself left the source folder alone (it converts nothing)' `
      ((Get-ChildItem $cap -Recurse -File).Count -eq $capBefore) `
      "before=$capBefore after=$((Get-ChildItem $cap -Recurse -File).Count)"

# Depth: root + d1..d12, one image each. depth 0 is the dropped folder itself, so files in d1..d8
# are collected (9 in total) and d9 is the first folder that is refused - and now REPORTED.
$deep = Join-Path $Work 'deep-12'
New-Item -ItemType Directory -Force -Path $deep | Out-Null
MakeImage (Join-Path $deep 'f0.png') 60 60
$cur = $deep
foreach ($level in 1..12) {
    $cur = Join-Path $cur ("d$level")
    New-Item -ItemType Directory -Force -Path $cur | Out-Null
    MakeImage (Join-Path $cur "f$level.png") 60 60
}
$deepRun = Run @('--payload-expand', $deep) 's9-deep'
$deepLine = Line $deepRun 'payload expand:'
Check 'S9.5 12 levels deep: levels 1..8 are taken (9 files) and it does not crash or recurse away' `
      ($deepLine -and $deepLine.ToString() -match 'out=9 .*truncated=True') "line=$deepLine"
Check 'S9.6 the depth limit is now SAID OUT LOUD (it used to be silent)' `
      ((Line $deepRun 'payload expand notice:') -match '8') `
      "notice=[$(Line $deepRun 'payload expand notice:')]"
Check 'S9.7 a deep folder still produces a usable batch, not an empty one' `
      ($deepLine -and $deepLine.ToString() -match 'status=Ready') "line=$deepLine"
Check 'S9.8 the two limits are reported by NAME, not by one vague sentence' `
      (("$(Line $capRun 'payload expand notice:')" -match '1000') -and
       ("$(Line $deepRun 'payload expand notice:')" -match '8') -and
       ("$(Line $capRun 'payload expand notice:')" -ne "$(Line $deepRun 'payload expand notice:')")) `
      "cap=[$(Line $capRun 'payload expand notice:')] deep=[$(Line $deepRun 'payload expand notice:')]"

""
"checks=$script:checks pass=$script:pass fail=$script:fail"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
