# verify-cli.ps1 -- the command line surface (`confern convert` / `confern formats`)
#
# Why this suite exists: the CLI is the one part of the product that is used from a script,
# where "it looked fine" is not evidence -- an exit code and a byte on disk are. Every check
# below reads back either (a) the process exit code, (b) a file on disk, or (c) a line the
# product itself wrote. Nothing is taken from the absence of a crash.
#
# What this covers:
#   S1  the subcommand surface: every way of getting it wrong exits 4 and says why, and a
#       typo does NOT silently become "start the tray icon"
#   S2  `formats` is honest: every format it lists as an OUTPUT can really be produced by this
#       exe, and a format that is not listed is refused with the list attached
#   S3  convert: the output lands where it says, has the right magic bytes, a batch of three
#       reports three, a second run never overwrites the first result, a missing -o folder is
#       reported instead of silently redirecting, a broken file exits 9
#   S4  quality actually reaches the encoder (three distinct sizes, three distinct hashes) and
#       an out-of-range value is clamped OUT LOUD
#   S5  the CLI works while the resident tray app is running (this used to CRASH: the default
#       log is opened with FileShare.Read, so a second process got an unhandled IOException)
#   S6  no side effects: no Run-key write, no .tmp- leftovers, no extra process left behind
#   S7  the old long-flag form still behaves exactly as before (suites depend on it)
#   S8  CliArgs.ValueFlags still matches the parser: every flag that reads its value from the
#       next argument is registered, so no value is ever mistaken for a file name. This is the
#       defect that made every "different quality" run silently use the default 85.
#
# ASCII only on purpose: a BOM-less CJK .ps1 is read as CP950 by PowerShell 5.1 and produces
# fake syntax errors. Assertions use exit codes, file bytes and ASCII fragments of the CLI
# text -- never a localized message.
#
# Usage: powershell -File tools\verify-cli.ps1 -Exe <path to Confern.exe> [-Work <dir>]

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$Work = (Join-Path $env:TEMP 'confern-cli')
)

$ErrorActionPreference = 'Continue'
$script:checks = 0
$script:pass = 0
$script:fail = 0
# A check that fails to REGISTER is invisible: a helper stops matching, the block never runs,
# the total just gets smaller and the suite still exits 0. Pinning the count turns that into a
# loud FATAL. Adding or removing a check means updating this number ON PURPOSE.
$script:ExpectedChecks = 46

Add-Type -AssemblyName System.Drawing

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks++
    if ($ok) { $script:pass++; "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name  [$detail]" }
}

# Run the CLI and capture BOTH streams. `& $exe` is not enough: Confern is a WinExe and the
# redirect files are also what makes stdout/stderr separable (the point of --cli-subcommand).
function Invoke-Cli([string[]]$argv, [string]$tag) {
    $out = Join-Path $Work ("$tag.out")
    $err = Join-Path $Work ("$tag.err")
    $nul = Join-Path $Work 'nul.txt'
    Remove-Item $out, $err -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $Exe -ArgumentList $argv -PassThru -Wait -WindowStyle Hidden `
         -RedirectStandardOutput $out -RedirectStandardError $err -RedirectStandardInput $nul
    [pscustomobject]@{
        Exit   = $p.ExitCode
        Out    = [string](@(if (Test-Path $out) { Get-Content $out -Encoding UTF8 }) -join "`n")
        Err    = [string](@(if (Test-Path $err) { Get-Content $err -Encoding UTF8 }) -join "`n")
    }
}

function New-Png([string]$path, [int]$size, [int]$argb) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb($argb))
    $g.FillEllipse([System.Drawing.Brushes]::White, 8, 8, [Math]::Max(4, $size - 16), [Math]::Max(4, $size - 16))
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# A noisy image: a flat colour is a bad fixture for a quality test, because JPEG can encode it
# identically at several quality levels and the "quality has no effect" bug hides behind that.
function New-NoisyPng([string]$path, [int]$w, [int]$h, [int]$seed) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $w, $h
    $rnd = New-Object System.Random $seed
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $v = $rnd.Next(0, 256)
            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($v, ($v * 7) % 256, ($v * 13) % 256))
        }
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Magic([string]$path, [int]$count) {
    $fs = [System.IO.File]::OpenRead($path)
    $buf = New-Object byte[] $count
    $read = $fs.Read($buf, 0, $count)
    $fs.Close()
    ($buf[0..($read - 1)] | ForEach-Object { '{0:X2}' -f $_ }) -join ''
}

function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

function JpgSize([string]$tag) {
    $f = @(Get-ChildItem -LiteralPath $tag -Filter *.jpg -ErrorAction SilentlyContinue)
    if ($f.Count -eq 0) { return -1 }
    $f[0].Length
}

# ---------------------------------------------------------------- setup
Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Work -Force | Out-Null
Set-Content -LiteralPath (Join-Path $Work 'nul.txt') -Value ''

# Nothing else may hold the default log: S5 starts the resident app on purpose, everything
# before it needs a clean slate (and a stale resident would make S6 measure the wrong thing).
Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

$src = Join-Path $Work 'trip.png'
New-Png $src 240 16711680
$noisy = Join-Path $Work 'noisy.png'
New-NoisyPng $noisy 300 220 7

"== S1 subcommand surface =="
$r = Invoke-Cli @('convert', '--to', 'JPG') 's1a'
Check 'S1.1 convert with no file exits 4' ($r.Exit -eq 4) "exit=$($r.Exit)"
Check 'S1.2 ... and says a file is needed' ($r.Err -match 'needs at least one file') $r.Err

$before = @(Get-Process Confern -ErrorAction SilentlyContinue).Count
$r = Invoke-Cli @('typo-command') 's1b'
Start-Sleep -Milliseconds 1200
$after = @(Get-Process Confern -ErrorAction SilentlyContinue).Count
Check 'S1.3 an unknown subcommand exits 4' ($r.Exit -eq 4) "exit=$($r.Exit)"
Check 'S1.4 ... and does NOT become "start the tray"' ($after -eq $before) "processes $before -> $after"
Check 'S1.5 ... and names the word it did not understand' ($r.Err -match "unknown command 'typo-command'") $r.Err

$r = Invoke-Cli @('formats', 'extra') 's1c'
Check 'S1.6 formats with an argument exits 4' ($r.Exit -eq 4 -and $r.Err -match 'takes no arguments') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('convert', $src, '--to') 's1d'
Check 'S1.7 --to without a value exits 4' ($r.Exit -eq 4 -and $r.Err -match '--to needs a format') "exit=$($r.Exit)"

$r = Invoke-Cli @('convert', $src, '-o') 's1e'
Check 'S1.8 -o without a value exits 4' ($r.Exit -eq 4 -and $r.Err -match 'needs a folder path') "exit=$($r.Exit)"

"== S2 formats tells the truth =="
$r = Invoke-Cli @('formats') 's2a'
Check 'S2.1 formats exits 0' ($r.Exit -eq 0) "exit=$($r.Exit) err=$($r.Err)"
Check 'S2.2 formats prints an IN section and an OUT section' (($r.Out -match '(?m)^IN ') -and ($r.Out -match '(?m)^OUT ')) 'missing section header'
Check 'S2.3 formats is quiet on stderr (subcommand form)' ($r.Err.Trim().Length -eq 0) $r.Err

# The OUT list must be REAL, not documentation: try every format it printed.
$outAt = $r.Out.IndexOf('OUT (what --to accepts)')
$tail = ''
if ($outAt -ge 0) { $tail = $r.Out.Substring($outAt) }
$outFormats = @([regex]::Matches($tail, '(?m)^[ \t]+([A-Z]{2,5})[ \t]') | ForEach-Object { $_.Groups[1].Value })
$produced = @()
$broken = @()
foreach ($fmt in $outFormats) {
    $dir = Join-Path $Work ("fmt-" + $fmt.ToLowerInvariant())
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $fr = Invoke-Cli @('convert', $src, '--to', $fmt, '-o', $dir) ("s2f-" + $fmt.ToLowerInvariant())
    $made = @(Get-ChildItem -LiteralPath $dir -ErrorAction SilentlyContinue)
    if ($fr.Exit -eq 0 -and $made.Count -eq 1 -and $made[0].Length -gt 0) { $produced += $fmt } else { $broken += "$fmt(exit=$($fr.Exit))" }
}
Check 'S2.4 every listed output format really converts' ($outFormats.Count -ge 4 -and $broken.Count -eq 0) ("listed=[$($outFormats -join ',')] broken=[$($broken -join ',')]")

$r = Invoke-Cli @('convert', $src, '--to', 'HEIC') 's2b'
Check 'S2.5 a format that is NOT offered exits 4 with the list' ($r.Exit -eq 4 -and $r.Err -match 'not a format this build writes' -and $r.Err -match 'offered=\[') "exit=$($r.Exit) err=$($r.Err)"

"== S3 convert =="
$out1 = Join-Path $Work 'o1'
New-Item -ItemType Directory -Path $out1 -Force | Out-Null
$r = Invoke-Cli @('convert', $src, '--to', 'JPG', '-o', $out1) 's3a'
$made = @(Get-ChildItem -LiteralPath $out1 -Filter *.jpg -ErrorAction SilentlyContinue)
Check 'S3.1 png -> jpg into -o exits 0' ($r.Exit -eq 0) "exit=$($r.Exit) out=$($r.Out) err=$($r.Err)"
Check 'S3.2 exactly one file appeared in that folder' ($made.Count -eq 1) "count=$($made.Count)"
Check 'S3.3 the file starts with the JPEG magic FFD8FF' ($made.Count -eq 1 -and (Magic $made[0].FullName 3) -eq 'FFD8FF') ($(if ($made.Count -eq 1) { Magic $made[0].FullName 3 } else { 'no-file' }))
Check 'S3.4 stdout has one "ok  <src> -> <dst>" line' ($r.Out -match 'ok\s+trip\.png -> trip\.jpg') $r.Out
Check 'S3.5 the summary line names the folder it actually wrote to' ($r.Out -match 'o1') $r.Out

$batch = Join-Path $Work 'batch'
New-Item -ItemType Directory -Path $batch -Force | Out-Null
1..3 | ForEach-Object { New-Png (Join-Path $batch "p$_.png") (120 + 20 * $_) (16711680 + $_ * 999) }
$r = Invoke-Cli (@('convert') + @(Get-ChildItem -LiteralPath $batch -Filter *.png | ForEach-Object { $_.FullName }) + @('--to', 'JPG')) 's3b'
$made = @(Get-ChildItem -LiteralPath $batch -Filter *.jpg -ErrorAction SilentlyContinue)
$tick = [string][char]0x2713
Check 'S3.6 three files convert in one call' ($r.Exit -eq 0 -and $made.Count -eq 3) "exit=$($r.Exit) count=$($made.Count)"
Check 'S3.7 ... and the summary says 3' ($r.Out -match [regex]::Escape("$tick 3")) $r.Out

$col = Join-Path $Work 'collide'
New-Item -ItemType Directory -Path $col -Force | Out-Null
$null = Invoke-Cli @('convert', $src, '--to', 'JPG', '-o', $col) 's3c'
$first = @(Get-ChildItem -LiteralPath $col -Filter *.jpg | Sort-Object Name)[0]
$hash1 = Sha $first.FullName
$bytes1 = $first.Length
$null = Invoke-Cli @('convert', $src, '--to', 'JPG', '-o', $col) 's3d'
$all = @(Get-ChildItem -LiteralPath $col -Filter *.jpg | Sort-Object Name)
Check 'S3.8 a second run into the same folder makes a SECOND file' ($all.Count -eq 2) "count=$($all.Count)"
Check 'S3.9 ... and the first file is byte-identical (never overwritten)' ((Sha $first.FullName) -eq $hash1 -and $first.Length -eq $bytes1) "hash $hash1 -> $(Sha $first.FullName)"

$missing = Join-Path $Work 'no-such-folder-xyz'
Remove-Item -LiteralPath $missing -Recurse -Force -ErrorAction SilentlyContinue
$r = Invoke-Cli @('convert', $src, '--to', 'TIF', '-o', $missing) 's3e'
$fellBack = Test-Path (Join-Path $Work 'trip.tif')
Check 'S3.10 a missing -o folder does not fail the conversion' ($r.Exit -eq 0) "exit=$($r.Exit)"
Check 'S3.11 ... it falls back next to the source, says so, and does NOT create the folder' `
      ($fellBack -and (-not (Test-Path $missing)) -and ($r.Out -match 'not usable')) ("fellBack=$fellBack created=$(Test-Path $missing) out=$($r.Out)")

$bad = Join-Path $Work 'broken.png'
[System.IO.File]::WriteAllBytes($bad, [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01))
$r = Invoke-Cli @('convert', $bad, '--to', 'JPG', '-o', $out1) 's3f'
Check 'S3.12 a corrupt file exits 9 (not 4, not 0)' ($r.Exit -eq 9) "exit=$($r.Exit) out=$($r.Out)"
Check 'S3.13 ... and the reason is on stdout, not only in a log' ($r.Out -match '(?m)^fail\s') $r.Out

"== S4 quality reaches the encoder =="
$qdir = Join-Path $Work 'quality'
foreach ($q in @(60, 85, 100, 20)) {
    $d = Join-Path $qdir "q$q"
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    $null = Invoke-Cli @('convert', $noisy, '--to', 'JPG', '--quality', $q, '-o', $d) ("s4q$q")
}
$s60 = JpgSize (Join-Path $qdir 'q60')
$s85 = JpgSize (Join-Path $qdir 'q85')
$s100 = JpgSize (Join-Path $qdir 'q100')
$s20 = JpgSize (Join-Path $qdir 'q20')
Check 'S4.1 quality 60/85/100 give three strictly increasing sizes' ($s60 -gt 0 -and $s60 -lt $s85 -and $s85 -lt $s100) "60=$s60 85=$s85 100=$s100"
$h60 = Sha (@(Get-ChildItem (Join-Path $qdir 'q60') -Filter *.jpg)[0].FullName)
$h100 = Sha (@(Get-ChildItem (Join-Path $qdir 'q100') -Filter *.jpg)[0].FullName)
Check 'S4.2 ... and different bytes, not just different lengths' ($h60 -ne $h100) "60=$h60 100=$h100"
New-Item -ItemType Directory -Path (Join-Path $qdir 'q20b') -Force | Out-Null
$r = Invoke-Cli @('convert', $noisy, '--to', 'JPG', '--quality', '20', '-o', (Join-Path $qdir 'q20b')) 's4clamp'
Check 'S4.3 an out-of-range quality is clamped OUT LOUD' ($r.Err -match 'note: quality 20' -and $r.Err -match 'using 60') $r.Err
Check 'S4.4 ... and the clamp lands on exactly the 60 result' ($s20 -eq $s60) "20=$s20 60=$s60"
$r = Invoke-Cli @('convert', $noisy, '--to', 'PNG', '--quality', '20', '-o', (Join-Path $qdir 'q20png')) 's4png'
$pngNote = $r.Err -match 'note: quality'
Check 'S4.5 no clamp note for a lossless target (PNG has no quality dial)' (-not $pngNote) $r.Err

"== S5 the CLI works while the resident app is running =="
Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$resident = Start-Process -FilePath $Exe -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 3
$alive = @(Get-Process Confern -ErrorAction SilentlyContinue).Count
Check 'S5.1 the resident app is actually running' ($alive -ge 1) "processes=$alive"
$r = Invoke-Cli @('formats') 's5a'
Check 'S5.2 formats still exits 0 with the resident running' ($r.Exit -eq 0) "exit=$($r.Exit) err=$($r.Err)"
Check 'S5.3 ... with real output, not an unhandled-exception crash' ($r.Out -match 'formats this build knows') ("exit=$($r.Exit) out=$($r.Out)")
$out2 = Join-Path $Work 'o-resident'
New-Item -ItemType Directory -Path $out2 -Force | Out-Null
$r = Invoke-Cli @('convert', $src, '--to', 'BMP', '-o', $out2) 's5b'
Check 'S5.4 convert still exits 0 with the resident running' ($r.Exit -eq 0 -and @(Get-ChildItem $out2 -Filter *.bmp).Count -eq 1) "exit=$($r.Exit) out=$($r.Out)"
$logs = Join-Path $env:LOCALAPPDATA 'Confern\logs'
$recent = @(Get-ChildItem $logs -Filter 'confern.*.log' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-3) })
$explained = $false
foreach ($f in $recent) {
    if ((Get-Content $f.FullName -Encoding UTF8 -Raw) -match 'is held by another Confern') { $explained = $true }
}
Check 'S5.5 the fallback log says WHY it is not the default one' ($recent.Count -ge 1 -and $explained) "fallback logs=$($recent.Count) explained=$explained"

"== S6 no side effects =="
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$beforeRun = ''
if (Test-Path $runKey) { $beforeRun = ((Get-ItemProperty $runKey).PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { $_.Name + '=' + $_.Value }) -join ';' }
$null = Invoke-Cli @('convert', $src, '--to', 'PNG', '-o', $out1) 's6a'
$afterRun = ''
if (Test-Path $runKey) { $afterRun = ((Get-ItemProperty $runKey).PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { $_.Name + '=' + $_.Value }) -join ';' }
Check 'S6.1 the CLI never writes an autostart Run key' ($beforeRun -eq $afterRun) 'Run key changed'

$tmpLeft = @(Get-ChildItem -LiteralPath $out1 -Filter '.tmp-*' -Force -ErrorAction SilentlyContinue) +
           @(Get-ChildItem -LiteralPath $Work -Filter '.tmp-*' -Force -ErrorAction SilentlyContinue)
Check 'S6.2 no .tmp- leftovers after the CLI runs' ($tmpLeft.Count -eq 0) "$($tmpLeft.Count) leftover(s)"

$residentCount = @(Get-Process Confern -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $resident.Id }).Count
Check 'S6.3 the resident app survived the CLI runs (the CLI does not kill or duplicate it)' ($residentCount -eq 1) "resident=$residentCount"

Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

"== S7 the old long-flag form still works =="
$legacy = Join-Path $Work 'legacy'
New-Item -ItemType Directory -Path $legacy -Force | Out-Null
$old = Join-Path $legacy 'trip.png'
Copy-Item -LiteralPath $src -Destination $old -Force
$r = Invoke-Cli @('--convert', $old, '--target', 'BMP', '--quiet') 's7a'
Check 'S7.1 --convert / --target / --quiet still exits 0' ($r.Exit -eq 0 -and @(Get-ChildItem $legacy -Filter *.bmp).Count -eq 1) "exit=$($r.Exit)"
Check 'S7.2 ... and stays silent on stderr with --quiet' ($r.Err.Trim().Length -eq 0) $r.Err

$r = Invoke-Cli @('--version') 's7b'
Check 'S7.3 --version exits 0 and prints 0.1.0' ($r.Exit -eq 0 -and $r.Out -match '0\.1\.0') "exit=$($r.Exit) out=$($r.Out)"

$r = Invoke-Cli @('--help') 's7c'
$helpHasCli = $false
foreach ($blob in @($r.Out, $r.Err)) { if ($blob -match 'convert <file>') { $helpHasCli = $true } }
Check 'S7.4 --help mentions the convert command' $helpHasCli 'help text has no "convert <file>"'

"== S8 the value-flag table matches the parser =="
$repo = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$progPath = Join-Path $repo 'src\Sidecar\Program.cs'
$cliPath = Join-Path $repo 'src\Sidecar\Cli\CliArgs.cs'
$lines = @(Get-Content $progPath -Encoding UTF8)
$cur = $null
$parseFlags = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'case\s+"(--[^"]+)":') { $cur = $matches[1] }
    elseif ($cur -and ($lines[$i] -match 'args\[i \+ 1\]|args\[i\+1\]|args\[\+\+i\]|\+\+i < args\.Length')) {
        if (-not $parseFlags.Contains($cur)) { $parseFlags.Add($cur) }
    }
}
$cliText = Get-Content $cliPath -Encoding UTF8 -Raw
$tableBlock = [regex]::Match($cliText, 'ValueFlags\s*=\s*new HashSet<string>\(StringComparer\.Ordinal\)\s*\{(?<body>[^}]*)\}')
$tableFlags = @([regex]::Matches($tableBlock.Groups['body'].Value, '"(--[^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$missing = @($parseFlags | Where-Object { $tableFlags -notcontains $_ })
$extra = @($tableFlags | Where-Object { $parseFlags -notcontains $_ })
Check 'S8.1 every value-taking flag in the parser is registered' ($missing.Count -eq 0) "missing=[$($missing -join ',')]"
Check 'S8.2 ... and the table has no flag the parser does not read' ($extra.Count -eq 0) "extra=[$($extra -join ',')]"
Check 'S8.3 the two lists are non-trivial (the scan itself still works)' ($parseFlags.Count -ge 30 -and $tableFlags.Count -ge 30) "parser=$($parseFlags.Count) table=$($tableFlags.Count)"

# ---------------------------------------------------------------- summary
""
"checks=$($script:checks) pass=$($script:pass) fail=$($script:fail) (expected $($script:ExpectedChecks))"
if ($script:checks -ne $script:ExpectedChecks) {
    "FATAL: registered $($script:checks) checks but ExpectedChecks is $($script:ExpectedChecks) -- one check did not run"
    exit 3
}
if ($script:fail -gt 0) { exit 1 }
exit 0
