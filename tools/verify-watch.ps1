# verify-watch.ps1 -- watched folders (`confern watch`)
#
# Why this suite exists: a folder watcher is the one feature whose failure mode is SILENCE. If it
# never fires, nothing crashes, nothing is red, the folder just stays empty -- and an empty folder
# looks exactly like "nothing was dropped in". So every check below waits for a real file to appear
# on disk, or counts a line the product printed. "It started without throwing" is not a check.
#
# What this covers:
#   S1  the surface: every wrong way to ask exits 4 and says why (a typo must not become a watcher)
#   S2  the settings round trip: --add / --list / --remove really change settings.json, an existing
#       entry is REPLACED (not duplicated), and the computed Key is not written into the file
#   S3  the actual job: a file dropped in is converted, the result has the right magic bytes, the
#       original is untouched, and exactly ONE new file appears (a watcher that converts its own
#       output grows without end -- the default output folder IS the watched folder)
#   S4  what must NOT happen: files already there when it starts are ignored, non-images are
#       ignored, a file still being written is not handed to the engine, and the settle knob is
#       demonstrated to be real (settle > duration => nothing converted)
#   S5  it coexists with the resident tray app, and leaves nothing behind
#   S6  source-level guards: the new settings key is registered (otherwise the product warns about
#       its own field) and the state shows up in the one-line settings summary
#
# ASCII only on purpose: a BOM-less CJK .ps1 is read as CP950 by PowerShell 5.1 and produces fake
# syntax errors.
#
# Usage: powershell -File tools\verify-watch.ps1 -Exe <path to Confern.exe> [-Work <dir>]

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$Work = (Join-Path $env:TEMP 'confern-watch')
)

$ErrorActionPreference = 'Continue'
$script:checks = 0
$script:pass = 0
$script:fail = 0
# A check that fails to REGISTER is invisible: the block never runs, the total just gets smaller
# and the suite still exits 0. Pinning the count turns that into a loud FATAL.
$script:ExpectedChecks = 39

Add-Type -AssemblyName System.Drawing

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks++
    if ($ok) { $script:pass++; "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name  [$detail]" }
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

function Magic([string]$path, [int]$count) {
    $fs = [System.IO.File]::OpenRead($path)
    $buf = New-Object byte[] $count
    $read = $fs.Read($buf, 0, $count)
    $fs.Close()
    ($buf[0..($read - 1)] | ForEach-Object { '{0:X2}' -f $_ }) -join ''
}

function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

# Confern is a WinExe: `& $exe` returns no output and no exit code, so both streams go to files.
function Invoke-Cli([string[]]$argv, [string]$tag) {
    $out = Join-Path $Work ("$tag.out")
    $err = Join-Path $Work ("$tag.err")
    $nul = Join-Path $Work 'nul.txt'
    Remove-Item $out, $err -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $Exe -ArgumentList $argv -PassThru -Wait -WindowStyle Hidden `
         -RedirectStandardOutput $out -RedirectStandardError $err -RedirectStandardInput $nul
    [pscustomobject]@{
        Exit = $p.ExitCode
        Out  = [string](@(if (Test-Path $out) { Get-Content $out -Encoding UTF8 }) -join "`n")
        Err  = [string](@(if (Test-Path $err) { Get-Content $err -Encoding UTF8 }) -join "`n")
    }
}

# Start a foreground watcher, drop a file in after it is up, wait for it to exit on its own.
# `Drop` is a scriptblock so a check can decide WHAT lands in the folder (and when).
function Invoke-Watch([string]$folder, [string[]]$extra, [scriptblock]$drop, [string]$tag) {
    # NOT Start-Process. With -PassThru plus redirected streams and no -Wait, PowerShell 5.1 never
    # populates ExitCode (measured 2026-10-22: HasExited=True but ExitCode empty, and neither a
    # second WaitForExit nor Refresh fills it in). Going through System.Diagnostics.Process makes
    # ExitCode real, and the async readers keep the pipes from filling while we wait for the run to
    # end by itself.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $argv = @('watch', $folder) + $extra
    $psi.Arguments = (($argv | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$proc.Start()
    $so = $proc.StandardOutput.ReadToEndAsync()
    $se = $proc.StandardError.ReadToEndAsync()
    Start-Sleep -Milliseconds 1200          # let the watcher record the files already there
    if ($drop) { & $drop }
    if (-not $proc.WaitForExit(40000)) { try { $proc.Kill() } catch { } }
    $proc.WaitForExit()
    $sw.Stop()

    [pscustomobject]@{
        Exit    = $proc.ExitCode
        Seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
        Out     = [string]$so.Result
        Err     = [string]$se.Result
    }
}

# Drop a copy of the fixture into a folder. Kept separate so each check can decide the NAME
# (the pre-existing-files check needs a name the watcher has never seen).
function Drop-Fixture([string]$folder, [string]$name) {
    Copy-Item -LiteralPath $fixture -Destination (Join-Path $folder $name) -Force
}
function New-Scope([string]$name) {
    $dir = Join-Path $Work $name
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    [pscustomobject]@{
        Root     = $dir
        In       = (New-Item -ItemType Directory -Path (Join-Path $dir 'in') -Force).FullName
        Out      = (New-Item -ItemType Directory -Path (Join-Path $dir 'out') -Force).FullName
        Settings = (Join-Path $dir 'settings.json')
    }
}

# ---------------------------------------------------------------- setup
Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700
Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Work -Force | Out-Null
Set-Content -LiteralPath (Join-Path $Work 'nul.txt') -Value ''

$fixture = Join-Path $Work 'fixture.png'
New-Png $fixture 200 8388736

$s = New-Scope 'settings'
$rel = 'relative-folder-xyz'

"== S1 the surface =="
$r = Invoke-Cli @('watch') 's1a'
Check 'S1.1 watch with no folder exits 4' ($r.Exit -eq 4) "exit=$($r.Exit)"
Check 'S1.2 ... and says a folder is needed' ($r.Err -match 'watch needs a folder') $r.Err

$r = Invoke-Cli @('watch', $s.In, $fixture) 's1b'
Check 'S1.3 two folders is refused, not guessed' ($r.Exit -eq 4 -and $r.Err -match 'ONE folder') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('watch', $rel, '--to', 'JPG') 's1c'
Check 'S1.4 a relative folder is refused (the resident has a different cwd)' `
      ($r.Exit -eq 4 -and $r.Err -match 'must be absolute') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('watch', '--add', (Join-Path $Work 'nope-does-not-exist'), '--to', 'JPG') 's1d'
Check 'S1.5 --add refuses a folder that does not exist' `
      ($r.Exit -eq 4 -and $r.Err -match 'does not exist') "exit=$($r.Exit) err=$($r.Err)"

# The offered list must be attached: "unsupported" without the alternatives is a dead end.
$r = Invoke-Cli @('watch', '--add', $s.In, '--to', 'WEBP') 's1e'
Check 'S1.6 --add refuses a format this build cannot write, and lists what it can' `
      ($r.Exit -eq 4 -and $r.Err -match 'not a format this build writes' -and $r.Err -match 'offered=\[') `
      "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('watch', '--add') 's1f'
Check 'S1.7 --add without a value exits 4' ($r.Exit -eq 4 -and $r.Err -match 'needs a folder path') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('watch', '--list', $s.In) 's1g'
Check 'S1.8 --list refuses a bare folder (it lists everything)' `
      ($r.Exit -eq 4 -and $r.Err -match 'takes no folder') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('watch', '--add', $s.In, '--remove', $s.In) 's1h'
Check 'S1.9 --add and --remove together are refused' ($r.Exit -eq 4) "exit=$($r.Exit) err=$($r.Err)"

"== S2 the settings round trip =="
$r = Invoke-Cli @('watch', '--list', '--settings-file', $s.Settings) 's2a'
Check 'S2.1 an empty list exits 0 and says none' `
      ($r.Exit -eq 0 -and $r.Out -match '\(none\)' -and $r.Out -match 'total=0') "exit=$($r.Exit) out=$($r.Out)"

$r = Invoke-Cli @('watch', '--add', $s.In, '--to', 'PNG', '--quality', '70', '--settings-file', $s.Settings) 's2b'
Check 'S2.2 --add exits 0 and says it added' ($r.Exit -eq 0 -and $r.Out -match 'added') "exit=$($r.Exit) out=$($r.Out)"
Check 'S2.3 ... and warns that the tray app reads it at start' ($r.Out -match 'restart it to pick up') $r.Out

$json = if (Test-Path $s.Settings) { Get-Content -LiteralPath $s.Settings -Raw -Encoding UTF8 } else { '' }
Check 'S2.4 settings.json now carries the entry' `
      ($json -match '"WatchFolders"' -and $json -match '"Target":\s*"PNG"' -and $json -match '"Quality":\s*70') $json
# A computed property that leaks into the file is a field the user can edit and that does nothing.
Check 'S2.5 the computed Key is NOT written into the file' ($json -notmatch '"Key"') $json
Check 'S2.6 the other settings are still there (nothing was replaced wholesale)' `
      ($json -match '"JpgQuality"') $json

$r = Invoke-Cli @('watch', '--add', $s.In, '--to', 'JPG', '--settings-file', $s.Settings) 's2c'
Check 'S2.7 adding the same folder again REPLACES it' ($r.Exit -eq 0 -and $r.Out -match 'replaced') "out=$($r.Out)"

$r = Invoke-Cli @('watch', '--list', '--settings-file', $s.Settings) 's2d'
Check 'S2.8 ... so the list still holds exactly one entry, and it is the new one' `
      ($r.Exit -eq 0 -and $r.Out -match 'total=1' -and $r.Out -match 'JPG') "out=$($r.Out)"

$r = Invoke-Cli @('watch', '--remove', $s.In, '--settings-file', $s.Settings) 's2e'
Check 'S2.9 --remove exits 0 and the count drops to 0' ($r.Exit -eq 0 -and $r.Out -match 'remaining 0') "out=$($r.Out)"

$r = Invoke-Cli @('watch', '--remove', $s.In, '--settings-file', $s.Settings) 's2f'
Check 'S2.10 removing something that is not there exits 4 (not a silent success)' `
      ($r.Exit -eq 4 -and $r.Err -match 'not in the watched list') "exit=$($r.Exit) err=$($r.Err)"

"== S3 the actual job =="
$w = New-Scope 'convert'
$r = Invoke-Watch -folder $w.In -extra @('--to', 'JPG', '--watch-duration-ms', '6000', '--settings-file', $w.Settings) -drop { Drop-Fixture $w.In 'fixture.png' } -tag 's3a'
Check 'S3.1 a dropped file was converted, and the product says so' `
      ($r.Out -match 'converted=1 failed=0') "out=$($r.Out) err=$($r.Err)"
Check 'S3.2 the run ends by itself and exits 0' ($r.Exit -eq 0 -and $r.Seconds -lt 20) "exit=$($r.Exit) s=$($r.Seconds)"

$produced = @(Get-ChildItem -LiteralPath $w.In -Filter '*.jpg')
Check 'S3.3 the result is where it says: next to the source' ($produced.Count -eq 1) "jpg count=$($produced.Count)"
if ($produced.Count -ge 1) {
    Check 'S3.4 the result really is a JPEG (magic FFD8FF)' ((Magic $produced[0].FullName 3) -eq 'FFD8FF') (Magic $produced[0].FullName 3)
    Check 'S3.5 ... and it is a real image, not an empty stub' ($produced[0].Length -gt 1000) "bytes=$($produced[0].Length)"
} else {
    Check 'S3.4 the result really is a JPEG (magic FFD8FF)' $false 'no output file'
    Check 'S3.5 ... and it is a real image, not an empty stub' $false 'no output file'
}
Check 'S3.6 the original is untouched (same sha256)' `
      ((Sha (Join-Path $w.In 'fixture.png')) -eq (Sha $fixture)) 'source hash changed'

# The watcher watches the folder it writes into. If it did not remember what it produced, the
# output would come back as a new file and the folder would fill up with (1), (2), (3) ...
$all = @(Get-ChildItem -LiteralPath $w.In -File)
Check 'S3.7 exactly one new file appeared: it does not convert its own output' `
      ($all.Count -eq 2) "files=$($all.Count): $((@($all | ForEach-Object { $_.Name }) -join ', '))"

$w2 = New-Scope 'outdir'
$r = Invoke-Watch -folder $w2.In -extra @('--to', 'PNG', '-o', $w2.Out, '--watch-duration-ms', '6000', '--settings-file', $w2.Settings) -drop { Drop-Fixture $w2.In 'fixture.png' } -tag 's3b'
Check 'S3.8 -o sends the result to the other folder' `
      ($r.Out -match 'converted=1 failed=0' -and @(Get-ChildItem -LiteralPath $w2.Out -Filter '*.png').Count -eq 1 -and
       @(Get-ChildItem -LiteralPath $w2.In -Filter '*.png' | Where-Object { $_.Name -ne 'fixture.png' }).Count -eq 0) `
      "out=$($r.Out) outdir=$( @(Get-ChildItem -LiteralPath $w2.Out).Count )"

"== S4 what must NOT happen =="
$w3 = New-Scope 'preexisting'
Copy-Item $fixture (Join-Path $w3.In 'old-1.png') -Force
Copy-Item $fixture (Join-Path $w3.In 'old-2.png') -Force
$r = Invoke-Watch -folder $w3.In -extra @('--to', 'JPG', '--watch-duration-ms', '5000', '--settings-file', $w3.Settings) -drop { Drop-Fixture $w3.In 'brand-new.png' } -tag 's4a'
# "old files are not converted" and "nothing was converted at all" would both read as converted=0,
# so the same run also drops a NEW file: the count must be exactly 1, not 3 and not 0.
Check 'S4.1 files that were already there are ignored, the new one is converted' `
      ($r.Out -match 'converted=1 failed=0') "out=$($r.Out)"

$w4 = New-Scope 'nonimage'
$notepad = Join-Path $w4.In 'notes.txt'
$r = Invoke-Watch -folder $w4.In -extra @('--to', 'JPG', '--watch-duration-ms', '5000', '--settings-file', $w4.Settings) -tag 's4b' -drop {
    Set-Content -LiteralPath (Join-Path $w4.In 'notes.txt') -Value 'this is not an image'
}
Check 'S4.2 a non-image file is ignored (not converted, not failed)' `
      ($r.Out -match 'converted=0 failed=0') "out=$($r.Out)"

# A file that is still being written must not be handed to the engine. The settle window is what
# buys that, so the file is written in two halves with a pause longer than nothing but shorter
# than "finished": the conversion must happen once, on the complete file.
$w5 = New-Scope 'partial'
$bytes = [System.IO.File]::ReadAllBytes($fixture)
# The pause between the two halves must be SHORTER than the settle window. With settle=400 and a
# 900 ms pause the file really does look finished (that is a correct judgement, not a bug -- the
# engine then reports "unsupported format" and leaves no output), so this run uses settle=1200 with
# a 600 ms pause: the file is never quiet long enough until it is closed.
$r = Invoke-Watch -folder $w5.In -extra @('--to', 'JPG', '--watch-settle-ms', '1200', '--watch-duration-ms', '9000', '--settings-file', $w5.Settings) -tag 's4c' -drop {
    $target = Join-Path $w5.In 'slow.png'
    $half = [int]($bytes.Length / 2)
    $fs = [System.IO.File]::Open($target, 'Create', 'Write')
    $fs.Write($bytes, 0, $half); $fs.Flush(); Start-Sleep -Milliseconds 600
    $fs.Write($bytes, $half, $bytes.Length - $half); $fs.Close()
}
$slowOut = @(Get-ChildItem -LiteralPath $w5.In -Filter '*.jpg')
Check 'S4.3 a half-written file is not converted until it is complete' `
      ($r.Out -match 'converted=1 failed=0') "out=$($r.Out)"
Check 'S4.4 ... and the result is whole (valid JPEG, sane size)' `
      ($slowOut.Count -eq 1 -and (Magic $slowOut[0].FullName 3) -eq 'FFD8FF' -and $slowOut[0].Length -gt 1000) `
      "count=$($slowOut.Count)"

# The settle knob has to be REAL. Settle longer than the whole run => nothing is eligible.
$w6 = New-Scope 'settle'
$r = Invoke-Watch -folder $w6.In -extra @('--to', 'JPG', '--watch-settle-ms', '4000', '--watch-duration-ms', '2500', '--settings-file', $w6.Settings) -drop { Drop-Fixture $w6.In 'fixture.png' } -tag 's4d'
Check 'S4.5 the settle window is real: settle 4000 ms with a 2500 ms run converts nothing' `
      ($r.Out -match 'converted=0 failed=0') "out=$($r.Out)"

"== S5 coexistence and hygiene =="
# A watcher must work while the tray app is running -- that is the whole point of putting it in the
# resident process. This used to be impossible for ANY diag/CLI command (the log file was locked
# with FileShare.Read and the second process died with an unhandled IOException).
$w7 = New-Scope 'resident'
$residentSettings = $w7.Settings
$resident = Start-Process -FilePath $Exe -ArgumentList @('--settings-file', $residentSettings) -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 3
$residentAlive = -not $resident.HasExited
$r = Invoke-Watch -folder $w7.In -extra @('--to', 'JPG', '--watch-duration-ms', '6000', '--settings-file', $w7.Settings) -drop { Drop-Fixture $w7.In 'fixture.png' } -tag 's5a'
Check 'S5.1 watch works while the resident tray app is alive' `
      ($residentAlive -and $r.Out -match 'converted=1 failed=0') "alive=$residentAlive out=$($r.Out) err=$($r.Err)"
Check 'S5.2 ... and the resident survived it' (-not $resident.HasExited) 'resident exited'
try { $resident | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }

$tmpLeft = @(Get-ChildItem -LiteralPath $Work -Recurse -File -Force -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -like '.tmp-*' -or $_.Name -like '*.tmp-*' })
Check 'S5.3 no .tmp- leftovers anywhere under the work tree' ($tmpLeft.Count -eq 0) `
      "$($tmpLeft.Count) file(s): $((@($tmpLeft | ForEach-Object { $_.Name }) -join ', '))"

# `watch --list` must flag an entry that could not start. A settings file the product will accept
# but refuse to act on is the worst kind: it looks configured.
$bad = New-Scope 'badentry'
$badJson = @'
{ "JpgQuality": 85, "WatchFolders": [ { "Path": "C:\\definitely-not-here-xyz", "Target": "JPG" } ] }
'@
Set-Content -LiteralPath $bad.Settings -Value $badJson -Encoding UTF8
$r = Invoke-Cli @('watch', '--list', '--settings-file', $bad.Settings) 's5b'
Check 'S5.4 --list flags an entry that would not start instead of claiming it works' `
      ($r.Exit -eq 0 -and $r.Out -match 'would not start') "exit=$($r.Exit) out=$($r.Out)"

Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Check 'S5.5 no Confern process is left running after the suite' `
      (@(Get-Process Confern -ErrorAction SilentlyContinue).Count -eq 0) 'a Confern process survived'

"== S6 source-level guards =="
$store = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Sidecar\Settings\SettingsStore.cs'
$storeText = if (Test-Path $store) { Get-Content -LiteralPath $store -Raw -Encoding UTF8 } else { '' }
# SettingsStore warns about keys it does not know. Forgetting to register a NEW field makes the
# product complain about its own setting, which trains the user to ignore that warning.
Check 'S6.1 WatchFolders is registered in SettingsStore.KnownKeys' `
      ($storeText -match 'nameof\(Settings\.WatchFolders\)') 'not registered -> the file reports its own key as unknown'
Check 'S6.2 the one-line settings summary exposes the watch count' `
      ($storeText -match 'watch=\{' -or $storeText -match 'watch=\$') 'no watch= field in Describe'

# ---------------------------------------------------------------- summary
""
$color = if ($script:fail -gt 0) { 'Red' } else { 'Green' }
"checks=$($script:checks) pass=$($script:pass) fail=$($script:fail) expected=$($script:ExpectedChecks)"
if ($script:checks -ne $script:ExpectedChecks) {
    "FATAL: must register exactly $($script:ExpectedChecks) checks, registered $($script:checks)"
    exit 1
}
if ($script:fail -gt 0) { "FAILURES:"; exit 1 }
exit 0
