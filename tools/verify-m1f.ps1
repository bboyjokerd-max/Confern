param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Work = "$env:TEMP\m1f-verify"
)
# =============================================================================
#  M1f regression (S14): the settings file (spec 8) -- can it be saved, does a
#  change round-trip, and does it actually TAKE EFFECT in the running app.
#
#  S13 (clamping out-of-range values, falling back to defaults on a broken file)
#  lives in verify-m1d.ps1; this suite starts where that one stops: the file was
#  readable but nothing could WRITE it, and three of the nine spec settings were
#  never consulted at all (the trigger key and the capture timeout were wired to
#  command-line flags only, so editing settings.json looked like it worked).
#
#  ASCII-only on purpose: PS 5.1 reads BOM-less scripts as ANSI and would mangle
#  CJK literals. No CJK expectation is needed here, so none is built.
#
#  exit 0 = all pass, 1 = at least one FAIL, 2 = zero checks ran,
#  3 = inconclusive (a precondition could not be established)
# =============================================================================
$ErrorActionPreference = 'Continue'

$script:Pass = 0
$script:Fail = 0
$script:Inconclusive = 0
$script:Checks = 0

function Check($name, $ok, $detail) {
  $script:Checks++
  if ($ok) { $script:Pass++; "  PASS  $name$(if ($detail) { "  [$detail]" })" }
  else { $script:Fail++; "  FAIL  $name$(if ($detail) { "  [$detail]" })" }
}
function Inconclusive($name, $why) {
  $script:Checks++
  $script:Inconclusive++
  "  INCONCLUSIVE  $name  [$why]"
}

"=== setup ==="
if (-not (Test-Path -LiteralPath $Exe)) { "executable not found: $Exe"; exit 2 }
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$logDir = Join-Path $Work 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$script:counter = 0

# The user's own settings file must survive this suite untouched: if a test can
# change the real configuration, every later run of every suite becomes a
# different experiment (and "the defaults" stop meaning anything).
$realSettings = Join-Path $env:LOCALAPPDATA 'Sidecar\settings.json'
$realBefore = if (Test-Path -LiteralPath $realSettings) { (Get-FileHash -LiteralPath $realSettings -Algorithm SHA256).Hash } else { 'absent' }

function Invoke-Sidecar([string[]]$a, [string]$tag) {
  $script:counter++
  $log = Join-Path $logDir "$tag-$($script:counter).log"
  $full = $a + @('--quiet', '--log', $log)
  $p = Start-Process -FilePath $Exe -ArgumentList $full -PassThru -Wait -WindowStyle Hidden
  return @{ Exit = $p.ExitCode; Log = $log; Text = (Get-Content -LiteralPath $log -Encoding UTF8) -join "`n" }
}
function Get-Field([string]$text, [string]$name) {
  $m = [regex]::Match($text, [regex]::Escape($name) + '=(\S+)')
  if ($m.Success) { return $m.Groups[1].Value }
  return ''
}
function Get-SettingResult([string]$text) {
  $m = [regex]::Match($text, 'settings (?:probe result|set result): (.*)')
  if ($m.Success) { return $m.Groups[1].Value.Trim() }
  return ''
}

"=== S14.1-S14.7 save, round-trip, and the right to overwrite ==="
$missing = Join-Path $Work 'does-not-exist.json'
$r = Invoke-Sidecar @('--settings-probe', $missing) 'probe-missing'
$result = Get-SettingResult $r.Text
Check 'S14.1 no settings file -> still runs on spec defaults' ($r.Exit -eq 0 -and $result -ne '') "exit=$($r.Exit)"
Check 'S14.2 the defaults ARE the spec 8 values' (
  (Get-Field $result 'jpgQuality') -eq '85' -and (Get-Field $result 'webpQuality') -eq '85' -and
  (Get-Field $result 'capture') -eq '30' -and (Get-Field $result 'trigger') -eq 'Space' -and
  (Get-Field $result 'startWithWindows') -eq 'True' -and (Get-Field $result 'language') -eq 'system') $result

$s = Join-Path $Work 'settings.json'
$r = Invoke-Sidecar @('--settings-set', 'jpgQuality=91;triggerKey=VK_TAB;capture=45', '--settings-file', $s) 'set-1'
Check 'S14.3 a change is accepted and the file appears' ((Test-Path -LiteralPath $s) -and $r.Exit -eq 0) "exit=$($r.Exit)"
Check 'S14.4 the log says what was applied' ($r.Text -match 'settings set done: applied=3 rejected=0') ''

$r = Invoke-Sidecar @('--settings-probe', $s) 'probe-roundtrip'
$result = Get-SettingResult $r.Text
Check 'S14.5 what was written is what loads back' (
  (Get-Field $result 'jpgQuality') -eq '91' -and (Get-Field $result 'trigger') -eq 'Tab' -and
  (Get-Field $result 'capture') -eq '45') $result

# Overwrite is the whole point of this path: a settings file always already
# exists on the second save. AtomicWriter is "never overwrite" by default (that
# is the output-file red line), so this check is what proves the settings path
# asked for overwrite explicitly instead of throwing on the second save.
$r = Invoke-Sidecar @('--settings-set', 'webpQuality=77', '--settings-file', $s) 'set-2-overwrite'
$r2 = Invoke-Sidecar @('--settings-probe', $s) 'probe-after-overwrite'
$result = Get-SettingResult $r2.Text
Check 'S14.6 saving over an existing file works and replaces it' (
  $r.Exit -eq 0 -and (Get-Field $result 'webpQuality') -eq '77') "set exit=$($r.Exit)"
Check 'S14.7 the first values survived the overwrite' ((Get-Field $result 'jpgQuality') -eq '91') $result

"=== S14.8-S14.11 the file itself ==="
$r = Invoke-Sidecar @('--settings-set', 'jpgQuality=200;capture=9999', '--settings-file', $s) 'set-clamp'
$raw = Get-Content -LiteralPath $s -Raw -Encoding UTF8
Check 'S14.8 out-of-range values are clamped BEFORE they are written' (
  ($raw -match '"JpgQuality":\s*100') -and ($raw -match '"CaptureTimeoutSeconds":\s*120')) ''
Check 'S14.9 clamping is reported, not silent' ($r.Text -match 'clamped JpgQuality 200 -> 100') ''

$obj = $null
try { $obj = $raw | ConvertFrom-Json } catch { }
$keys = @('JpgQuality','WebpQuality','TriggerKey','StartWithWindows','Language','NamingCollision','CaptureTimeoutSeconds','EmergencyHotkey','Diagnostics')
$present = 0
if ($obj) { foreach ($k in $keys) { if ($obj.PSObject.Properties.Name -contains $k) { $present++ } } }
Check 'S14.10 the saved file is valid JSON holding all nine spec keys' ($present -eq 9) "keys=$present/9"
Check 'S14.11 the file stays human-readable (no \uXXXX escaping)' ($raw -match 'Ctrl\+Alt\+Shift\+Q') ''
Check 'S14.12 no .tmp-* residue next to the settings file' ((@(Get-ChildItem -LiteralPath $Work -Filter '*.tmp-*' -Recurse -File -ErrorAction SilentlyContinue).Count) -eq 0) ''

"=== S14.13-S14.16 refusals are loud, and a typo is not silent ==="
$r = Invoke-Sidecar @('--settings-set', 'jpgQualitee=90', '--settings-file', $s) 'reject-unknown-key'
Check 'S14.13 an unknown key is refused with exit 4' ($r.Exit -eq 4 -and $r.Text -match "reject unknown key 'jpgQualitee'") "exit=$($r.Exit)"

$r = Invoke-Sidecar @('--settings-set', 'triggerKey=Ctrl+Shift', '--settings-file', $s) 'reject-chord'
Check 'S14.14 a trigger chord is refused and EXPLAINED (spec 8 gap, not a silent no-op)' (
  $r.Exit -eq 4 -and $r.Text -match 'modifier combinations are not implemented') "exit=$($r.Exit)"

$r = Invoke-Sidecar @('--settings-set', 'namingCollision=overwrite', '--settings-file', $s) 'reject-fixed'
Check 'S14.15 the MVP-fixed setting is refused with its spec reason' (
  $r.Exit -eq 4 -and $r.Text -match 'fixed to auto-suffix') "exit=$($r.Exit)"

$typo = Join-Path $Work 'settings-typo.json'
Set-Content -LiteralPath $typo -Encoding UTF8 -Value '{ "JpgQuality": 90, "TriggerrKey": "Tab" }'
$r = Invoke-Sidecar @('--settings-probe', $typo) 'probe-typo'
$result = Get-SettingResult $r.Text
Check 'S14.16 a mistyped key is called out instead of silently defaulting' (
  $r.Text -match "unknown key 'TriggerrKey'" -and (Get-Field $result 'jpgQuality') -eq '90') ''

"=== S14.17-S14.20 the settings actually reach the running app ==="
$s2 = Join-Path $Work 'settings-live.json'
Invoke-Sidecar @('--settings-set', 'triggerKey=VK_TAB;capture=45', '--settings-file', $s2) 'set-live' | Out-Null
$r = Invoke-Sidecar @('--settings-file', $s2, '--cycle-test', '1') 'live-settings'
Check 'S14.17 the trigger key from the file reaches the detector' ($r.Text -match 'settings in effect: trigger=VK 0x09') ''
Check 'S14.18 the capture timeout from the file reaches the guard' ($r.Text -match 'watchdog=45000 ms') ''
Check 'S14.19 the evidence says WHERE the values came from' ($r.Text -match 'trigger from settings file, watchdog from settings file') ''

$r = Invoke-Sidecar @('--settings-file', $s2, '--trigger-key', 'VK_SPACE', '--cycle-test', '1') 'live-precedence'
Check 'S14.20 a command-line flag still wins over the file' (
  $r.Text -match 'settings in effect: trigger=VK 0x20' -and $r.Text -match 'trigger from command line') ''

"=== S14.21 the Shift warning required by spec 8 ==="
$s3 = Join-Path $Work 'settings-shift.json'
$r = Invoke-Sidecar @('--settings-set', 'triggerKey=Shift', '--settings-file', $s3) 'set-shift'
$raw3 = Get-Content -LiteralPath $s3 -Raw -Encoding UTF8
Check 'S14.21 choosing Shift warns about Explorer MOVE (spec 8) and still saves' (
  $r.Exit -eq 0 -and $r.Text -match 'WARNING' -and $r.Text -match 'MOVE' -and $raw3 -match '"TriggerKey":\s*"Shift"') "exit=$($r.Exit)"

"=== S14.22 this suite did not touch the real configuration ==="
$realAfter = if (Test-Path -LiteralPath $realSettings) { (Get-FileHash -LiteralPath $realSettings -Algorithm SHA256).Hash } else { 'absent' }
Check 'S14.22 the real settings file is byte-identical afterwards' ($realBefore -eq $realAfter) "$realBefore -> $realAfter"

""
"M1f summary: $($script:Pass)/$($script:Checks) checks passed"
"checks=$($script:Checks) pass=$($script:Pass) fail=$($script:Fail) inconclusive=$($script:Inconclusive)"
if ($script:Fail -gt 0) { exit 1 }
if ($script:Checks -eq 0) { exit 2 }
if ($script:Inconclusive -gt 0) { exit 3 }
exit 0
