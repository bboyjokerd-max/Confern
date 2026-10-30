# verify-recent.ps1 -- the "recent conversions" list (2026-10-23): the store, the CLI surface, and the
# half of the tray menu that can be asserted without a mouse.
#
# WHAT THIS SUITE IS ALLOWED TO SAY
#   The feature has two halves. The tray submenu needs a real right-click, so its own self-test
#   (--tray-selftest, run by verify-m1g) drives the SAME dispatch code a click ends up in and records
#   what it would have opened. The other half is plain data: what got recorded, in what order, what
#   gets pruned, what happens when the file is corrupt. That half is what this suite pins down,
#   through `confern recent` (and through the CLI's own `convert`, so the entries come from a REAL
#   conversion, not from a hand-written JSON file).
#
# WHY EVERY ASSERTION IS A BYTE, AN EXIT CODE OR A FILE
#   The product is a GUI subsystem exe. Nothing here trusts "it looked fine": the list is read back
#   from recent.json, the paths are checked to exist on disk, and the pruning case DELETES the output
#   first and then requires the entry to be gone.
#
# TEST ISOLATION
#   The store lives at %LOCALAPPDATA%\Confern\recent.json -- the same file the user's real tray uses.
#   This suite therefore MOVES IT ASIDE at the start and puts it back at the end. If the user had an
#   empty/absent file, it stays absent. A verification run must not cost the user his history.
#
# ASCII ONLY on purpose: a BOM-less CJK .ps1 is read as CP950 by PowerShell 5.1 and turns into fake
# syntax errors. The Chinese strings in the product are never asserted here by their text -- only by
# structure (non-empty, non-ASCII where a label must be localized).
#
# Usage: powershell -File tools\verify-recent.ps1 -Exe <path to Confern.exe> [-Work <dir>]

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$Work = (Join-Path $env:TEMP 'confern-recent')
)

$ErrorActionPreference = 'Continue'
$script:checks = 0
$script:pass = 0
$script:fail = 0
# A check that fails to REGISTER is invisible: a helper stops matching, the block never runs, the
# total just gets smaller and the suite still exits 0. Pinning the count turns that into a loud FATAL.
# Adding or removing a check means updating this number ON PURPOSE.
$script:ExpectedChecks = 40

Add-Type -AssemblyName System.Drawing

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks++
    if ($ok) { $script:pass++; "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name  [$detail]" }
}

$store = Join-Path $env:LOCALAPPDATA 'Confern\recent.json'
$backup = Join-Path $Work 'recent.json.backup'
$hadStore = Test-Path $store

if (-not (Test-Path $Work)) { New-Item -ItemType Directory -Path $Work -Force | Out-Null }
$nul = Join-Path $Work 'nul.txt'
New-Item -ItemType File -Path $nul -Force | Out-Null
if ($hadStore) { Copy-Item $store $backup -Force }

# Run the CLI and capture BOTH streams. `& $Exe` is not enough for a WinExe; the redirect files also
# make stdout and stderr separable, which is the whole point of --cli-subcommand.
function Invoke-Cli([string[]]$argv, [string]$tag) {
    $out = Join-Path $Work ("$tag.out")
    $err = Join-Path $Work ("$tag.err")
    Remove-Item $out, $err -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $Exe -ArgumentList $argv -PassThru -Wait -WindowStyle Hidden `
         -RedirectStandardOutput $out -RedirectStandardError $err -RedirectStandardInput $nul
    [pscustomobject]@{
        Exit = $p.ExitCode
        Out  = [string](@(if (Test-Path $out) { Get-Content $out -Encoding UTF8 }) -join "`n")
        Err  = [string](@(if (Test-Path $err) { Get-Content $err -Encoding UTF8 }) -join "`n")
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

function Get-StoreText() {
    if (-not (Test-Path $store)) { return '' }
    return [string](@(Get-Content $store -Encoding UTF8) -join "`n")
}

function Get-StoreEntries() {
    if (-not (Test-Path $store)) { return @() }
    try { return @(Get-Content $store -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return @() }
}

"### $Exe"
"### work: $Work"
"### store: $store (pre-existing: $hadStore)"

# Start from a known state. The user's file (if any) is already copied to $backup.
Remove-Item $store -Force -ErrorAction SilentlyContinue

# --------------------------------------------------------------------------------------------
"=== S1 the CLI surface (no files needed) ==="
$r = Invoke-Cli @('recent') 'S1-empty'
Check 'S1.1 confern recent exits 0' ($r.Exit -eq 0) "exit=$($r.Exit) err=$($r.Err)"
Check 'S1.2 it prints a header that names the feature' ($r.Out -match 'recent conversions \(\d+ listed, newest first\)') $r.Out
Check 'S1.3 it says where the list is stored' ($r.Out -match 'file: .*recent\.json') $r.Out
Check 'S1.4 an empty list says so instead of printing nothing' (
    $r.Out -match '\(none\) - convert a file and it shows up here and in the tray menu' -and
    $r.Out -match 'total=0') $r.Out

$r = Invoke-Cli @('recent', '--limit', '5') 'S1-limit'
Check 'S1.5 --limit is accepted on an empty list' ($r.Exit -eq 0 -and $r.Out -match 'total=0') "exit=$($r.Exit)"

$r = Invoke-Cli @('recent', 'foo') 'S1-arg'
# A positional argument here is a question this feature cannot answer. Saying so beats guessing.
Check 'S1.6 recent with an argument exits 4 and explains itself' (
    $r.Exit -eq 4 -and $r.Err -match 'recent takes no arguments') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('recent', '--limit') 'S1-limit-missing'
Check 'S1.7 --limit without a number exits 4' ($r.Exit -eq 4 -and $r.Err -match 'needs a number') `
      "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('recent', '--clear') 'S1-clear-empty'
Check 'S1.8 --clear on an empty list exits 0 and says it cleared' (
    $r.Exit -eq 0 -and $r.Out -match 'recent: cleared') "exit=$($r.Exit) out=$($r.Out)"

$help = Invoke-Cli @('--help') 'S1-help'
Check 'S1.9 --help documents the three recent flags' (
    $help.Out -match '--recent-list' -and $help.Out -match '--recent-clear' -and $help.Out -match '--recent-limit') `
    "helpChars=$($help.Out.Length)"

# --------------------------------------------------------------------------------------------
"=== S2 a real conversion lands in the list ==="
$src = Join-Path $Work 'src'
if (Test-Path $src) { Remove-Item $src -Recurse -Force }
New-Item -ItemType Directory -Path $src -Force | Out-Null
$one = Join-Path $src 'one.png'
$two = Join-Path $src 'two.png'
New-Png $one 64 ([int]0xFF3366CC)
New-Png $two 64 ([int]0xFFCC6633)

$c = Invoke-Cli @('convert', $one, $two, '--to', 'JPG') 'S2-convert'
Check 'S2.1 the conversion itself succeeded' ($c.Exit -eq 0 -and $c.Out -match 'ok    one\.png') `
      "exit=$($c.Exit) out=$($c.Out)"
Check 'S2.2 recent.json now exists' (Test-Path $store) "store=$store"

$entries = Get-StoreEntries
Check 'S2.3 two entries were recorded' ($entries.Count -eq 2) "count=$($entries.Count)"

$r = Invoke-Cli @('recent') 'S2-list'
Check 'S2.4 the CLI lists both of them' ($r.Out -match 'total=2') $r.Out
# Newest first: two.png was converted second, so it must be printed first.
$posTwo = $r.Out.IndexOf('two.jpg')
$posOne = $r.Out.IndexOf('one.jpg')
Check 'S2.5 the newest conversion is listed first' (
    $posTwo -ge 0 -and $posOne -ge 0 -and $posTwo -lt $posOne) "twoAt=$posTwo oneAt=$posOne"
Check 'S2.6 every entry says which tool produced it' (
    ([regex]::Matches($r.Out, '\[convert\]')).Count -eq 2) $r.Out
Check 'S2.7 the listed outputs really exist on disk' (
    $entries.Count -eq 2 -and (@($entries | Where-Object { -not (Test-Path $_.OutputPath) })).Count -eq 0) `
    "missing=$(@($entries | Where-Object { -not (Test-Path $_.OutputPath) } | ForEach-Object { $_.OutputPath }) -join ',')"
Check 'S2.8 the JSON carries source, output, tool and a timestamp' (
    $entries.Count -eq 2 -and $entries[0].SourcePath -and $entries[0].OutputPath -and
    $entries[0].Tool -eq 'convert' -and $entries[0].At) "entry0=$($entries[0] | ConvertTo-Json -Compress)"

# --------------------------------------------------------------------------------------------
"=== S3 the list tells the truth about files that are gone ==="
$goneEntry = @($entries | Where-Object { $_.OutputPath -match 'one\.jpg$' })[0]
$gonePath = $goneEntry.OutputPath
Remove-Item $gonePath -Force
$r = Invoke-Cli @('recent') 'S3-prune'
Check 'S3.1 a deleted output is no longer listed' ($r.Out -notmatch 'one\.jpg') $r.Out
Check 'S3.2 the total drops by exactly one' ($r.Out -match 'total=1') $r.Out
Check 'S3.3 the prune was written back to the file, not just hidden' (
    (Get-StoreText) -notmatch 'one\.jpg') (Get-StoreText)
Check 'S3.4 the surviving entry is still there' (
    (Get-StoreText) -match 'two\.jpg') (Get-StoreText)

# --------------------------------------------------------------------------------------------
"=== S4 the list is capped, and the cap keeps the newest ==="
Remove-Item $store -Force -ErrorAction SilentlyContinue
$many = Join-Path $Work 'many'
if (Test-Path $many) { Remove-Item $many -Recurse -Force }
New-Item -ItemType Directory -Path $many -Force | Out-Null
$batch = @()
for ($i = 1; $i -le 13; $i++) {
    $p = Join-Path $many ('f{0:d2}.png' -f $i)
    New-Png $p 32 ([int](0xFF000000 -bor ($i * 700000)))
    $batch += $p
}
# One conversion call with 13 files: the list must keep the newest 12 and drop the oldest.
# The array is built in a variable first on purpose: `Invoke-Cli @('convert') + $batch ...` is parsed
# by PowerShell as several arguments with a literal "+" between them (the first version of this suite
# did exactly that and the exe got "+" as a file name, exit 4).
$argv = @('convert') + $batch + @('--to', 'JPG')
$c = Invoke-Cli -argv $argv -tag 'S4-convert'
Check 'S4.1 converting 13 files succeeded' ($c.Exit -eq 0) "exit=$($c.Exit) out=$($c.Out)"
$r = Invoke-Cli @('recent') 'S4-list'
Check 'S4.2 the list is capped at 12' ($r.Out -match 'total=12') $r.Out
Check 'S4.3 the JSON holds exactly 12 entries' ((Get-StoreEntries).Count -eq 12) "count=$((Get-StoreEntries).Count)"
# The first file of the batch is the oldest, so it must be the one that fell off the end.
Check 'S4.4 the oldest conversion is the one that was dropped' (
    $r.Out -notmatch 'f01\.jpg' -and $r.Out -match 'f13\.jpg') $r.Out

# --------------------------------------------------------------------------------------------
"=== S5 a corrupt store must not break the feature (or the tray) ==="
Remove-Item $store -Force -ErrorAction SilentlyContinue
$junk = Join-Path $Work 'junk.json'
[System.IO.File]::WriteAllText($junk, '{ this is not json at all', (New-Object System.Text.UTF8Encoding($false)))
Copy-Item $junk $store -Force
$r = Invoke-Cli @('recent') 'S5-corrupt'
Check 'S5.1 a corrupt store still exits 0' ($r.Exit -eq 0) "exit=$($r.Exit) err=$($r.Err)"
Check 'S5.2 it reports an empty list instead of crashing' ($r.Out -match 'total=0') $r.Out
Check 'S5.3 it says the file was unreadable' ($r.Err -match 'is unreadable') $r.Err

$heal = Join-Path $Work 'heal.png'
New-Png $heal 48 ([int]0xFF228822)
$c = Invoke-Cli @('convert', $heal, '--to', 'JPG') 'S5-heal'
$r = Invoke-Cli @('recent') 'S5-list'
# The output name is NOT asserted: the work folder survives between runs and the product never
# overwrites (a second run legitimately writes "heal (1).jpg"). What matters is that the conversion
# is recorded at all -- so this asserts on the SOURCE, which is stable.
Check 'S5.4 converting after a corrupt store works and is recorded' (
    $c.Exit -eq 0 -and $r.Out -match 'total=1' -and $r.Out -match 'from .*heal\.png') "exit=$($c.Exit) out=$($r.Out)"

$r = Invoke-Cli @('recent', '--clear') 'S5-clear'
Check 'S5.5 --clear empties the file itself' ((Get-StoreText).Trim() -eq '[]') (Get-StoreText)
$r = Invoke-Cli @('recent') 'S5-after-clear'
Check 'S5.6 the list is empty after --clear' ($r.Out -match 'total=0') $r.Out

# --------------------------------------------------------------------------------------------
"=== S6 the tray half: the submenu is rebuilt from a provider, and the click maps to a path ==="
# This is the same code path a real right-click ends up in (--tray-selftest calls the shared
# Dispatch). The label text itself is asserted in verify-m1g, which owns the tray.
$trayLog = Join-Path $Work 'tray.err'
$p = Start-Process -FilePath $Exe -ArgumentList @('--tray-selftest', '--no-autostart') -PassThru -Wait `
     -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Work 'tray.out') `
     -RedirectStandardError $trayLog -RedirectStandardInput $nul
$trayText = [string](@(if (Test-Path $trayLog) { Get-Content $trayLog -Encoding UTF8 }) -join "`n")
Check 'S6.1 --tray-selftest is still green' (
    $p.ExitCode -eq 0 -and $trayText -match 'tray: selftest done: checks=\d+ failed=0 ok=True') `
    "exit=$($p.ExitCode)"
Check 'S6.2 the submenu was refreshed from the provider it was given' (
    $trayText -match 'tray: PASS recent submenu lists the entries it was given') ''
Check 'S6.3 clicking a recent entry hands over the path at that position' (
    $trayText -match 'tray: PASS clicking a recent entry hands over the right path') ''
Check 'S6.4 the submenu follows the list on every refresh (not built once)' (
    $trayText -match 'tray: PASS the submenu follows the list it is given on every refresh') ''
Check 'S6.5 an empty list still shows one greyed line instead of an empty menu' (
    $trayText -match 'tray: recent menu refreshed \(empty\) items=1' -and
    $trayText -match 'tray: PASS clear reached its handler and emptied the submenu') ''
# The tray is a RESIDENT process, and `confern convert` may write the same recent.json while it is
# alive. A store that only reads at construction would keep showing the list from the moment the tray
# started -- and each process would look perfectly healthy on its own. The self-test simulates a
# second process with a second store instance over the same file, so this is a real cross-instance
# observation, not a claim.
Check 'S6.6 the store picks up a change written by another process' (
    $trayText -match 'tray: PASS recent store reloads when another process changed the file' -and
    $trayText -match 'recent: reloaded .* \(another process changed it\) count=') ''

# Source-level guards: the two things that would silently undo the feature without any test failing.
$traySrc = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Sidecar\Tray\TrayIcon.cs'
$srcText = [string](@(if (Test-Path $traySrc) { Get-Content $traySrc -Encoding UTF8 }) -join "`n")
Check 'S6.7 the submenu is attached with MF_POPUP (a real submenu, not a command)' (
    $srcText -match 'Native\.MF_POPUP, recent, MenuRecent') ''
Check 'S6.8 the recent list is rebuilt before the menu is shown' (
    $srcText -match 'RefreshRecentMenu\(\);\s*\r?\n\s*Native\.GetCursorPos') ''
$storeSrc = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Sidecar\Recent\RecentStore.cs'
$storeText = [string](@(if (Test-Path $storeSrc) { Get-Content $storeSrc -Encoding UTF8 }) -join "`n")
Check 'S6.9 the store writes atomically and overwrites on purpose' (
    $storeText -match 'AtomicWriter\.Write\(_path' -and $storeText -match 'overwrite: true') ''

# --------------------------------------------------------------------------------------------
# Put the user's list back exactly as it was.
Remove-Item $store -Force -ErrorAction SilentlyContinue
if ($hadStore -and (Test-Path $backup)) {
    Copy-Item $backup $store -Force
    Remove-Item $backup -Force -ErrorAction SilentlyContinue
    "### restored the pre-existing recent.json"
} else {
    $dir = Split-Path -Parent $store
    if ((Test-Path $dir) -and @(Get-ChildItem $dir -Force -ErrorAction SilentlyContinue).Count -eq 0) {
        Remove-Item $dir -Force -ErrorAction SilentlyContinue
    }
    "### the store did not exist before this run, and does not exist after it"
}

""
"checks=$($script:checks) pass=$($script:pass) fail=$($script:fail)"
if ($script:checks -ne $script:ExpectedChecks) {
    "FATAL expected $($script:ExpectedChecks) checks but $($script:checks) were registered"
    exit 3
}
if ($script:fail -ne 0) { exit 1 }
exit 0
