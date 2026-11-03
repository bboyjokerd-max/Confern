# verify-usage.ps1 -- the free-trial meter for the paid tool (CUTOUT), 2026-11-03.
#
#   powershell -File tools\verify-usage.ps1 -Exe <path to Confern.exe> [-LogDir <dir>]
#
# WHAT THIS PROVES, AND WHAT IT DOES NOT
#   1. The meter is a FILE (%LOCALAPPDATA%\Confern\usage.json) and --usage-report is the only way
#      this suite reads it. Reading the JSON directly would prove what the file looks like, not
#      what the product computes - and "how many free uses are left" is a number a buyer asks
#      about, so it must come from the product's own answer.
#   2. A brand-new meter means 10 uses. That is the ONE number that must never drift: it appears in
#      the help text, in the purchase page and in the user's head.
#   3. The counter is SPENT ON SUCCESS ONLY. A refused run (model missing) writes nothing, so it
#      must not cost the user a use. This is asserted by comparing the report before and after the
#      refusal - not by trusting the code path.
#   4. Tampering fails CLOSED. Editing the number in the file breaks its signature, and a broken
#      signature means "used up", never "10 uses again". Deleting the whole file does start over -
#      that is stated in the help text and is deliberately not defended (no DRM: see UsageStore).
#   5. BOTH DOORS ARE SHUT. The wheel (the product's only real entry) and --remove-bg are two
#      different code paths; a gate on one of them is a free tool for anyone who reads the help.
#   6. The refusal is a REFUSAL: exit 12, no output file, source untouched, and the reason on the
#      record.
#
# NOT covered here:
#   - the purchase page itself (there is no store listing yet). The refusal message names the tool
#     and the number; wiring a URL in is a separate, user-visible UI decision.
#   - anything about a license key: the trial meter is this suite's whole subject.
#
# ENCODING: ASCII only (Windows PowerShell 5.1 reads a BOM-less file in the ANSI code page, and a
# single non-ASCII character turns the script into a parse error). CRLF, no BOM.

param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [string]$LogDir = "$env:TEMP\sidecar-verify"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "FATAL: no exe at '$Exe'"; exit 2 }
if (-not [System.IO.Path]::IsPathRooted($Exe)) { $Exe = (Resolve-Path -LiteralPath $Exe).Path }
$repo = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$Work = Join-Path $LogDir 'usage'
$results = New-Object System.Collections.ArrayList
$inconclusive = New-Object System.Collections.ArrayList

function Check([string]$name, [bool]$ok, [string]$detail = '') {
  [void]$results.Add([pscustomobject]@{ Name = $name; Pass = $ok })
  if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name" }
  if ($detail -ne '') { Write-Host "      $detail" }
}
function Inconclusive([string]$reason) {
  [void]$inconclusive.Add($reason); Write-Host "INCONCLUSIVE  $reason"
}
$script:ExpectedChecks = 0
function Expect([int]$n) { $script:ExpectedChecks += $n }

# One check that needs the portrait model. Registering an Inconclusive still consumes exactly one
# slot, so a machine without the model reports "11 registered, some inconclusive" instead of a
# misleading smaller number (the ExpectedChecks pin is what catches a silently skipped block).
function CheckNeed([bool]$ready, [string]$name, [bool]$ok, [string]$detail = '') {
  if ($ready) { Check $name $ok $detail } else { Inconclusive "needs the portrait model: $name" }
}

function Get-LogLines([string]$p) {
  if (Test-Path -LiteralPath $p) { return @(Get-Content -LiteralPath $p -Encoding UTF8) } else { return @() }
}
function HasLine([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -Quiet) }
function GetLine([string[]]$l, [string]$p) {
  $hit = @($l | Select-String -Pattern $p)
  if ($hit.Count -eq 0) { return '' }
  return [string]$hit[$hit.Count - 1].Line
}
function Get-Sha([string]$p) {
  if (-not (Test-Path -LiteralPath $p)) { return 'MISSING' }
  return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
}

# One headless run. Confern is a WinExe (plain `& $exe` returns before it does anything), so
# Start-Process is the only shape that yields both an exit code and a log.
function Run([string[]]$argv, [string]$tag, [bool]$resident = $false) {
  $log = Join-Path $Work "$tag.log"
  Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
  $a = @() + $argv + @('--quiet', '--log', $log, '--no-autostart')
  if ($resident) { $a += @('--tray-exit-after', '90000') }
  $p = Start-Process -FilePath $Exe -ArgumentList $a -Wait -PassThru -WindowStyle Hidden
  return [pscustomobject]@{ Tag = $tag; Exit = $p.ExitCode; Log = $log; Lines = (Get-LogLines $log) }
}

# The meter this suite writes. Without it every run below would spend the USER's ten free uses
# (and, after ten, this suite would start failing on exit 12 - a self-inflicted red).
$env:CONFERN_USAGE = Join-Path $Work 'usage.json'
$meterPath = $env:CONFERN_USAGE

if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$portrait = Join-Path $repo 'fixtures\rembg\real-512.png'
$havePortrait = Test-Path -LiteralPath $portrait
$exeDir = Split-Path -Parent $Exe
if (-not [System.IO.Path]::IsPathRooted($exeDir)) { $exeDir = [System.IO.Path]::GetFullPath($exeDir) }
$model = ''
foreach ($cand in @((Join-Path $exeDir 'models\model_fp16.onnx'), (Join-Path $repo 'models\model_fp16.onnx'))) {
  if (Test-Path -LiteralPath $cand) { $model = $cand; break }
}
$haveModel = ($model -ne '') -and $havePortrait

Write-Host "verify-usage: exe=$Exe meter=$meterPath model=$(if ($haveModel) { $model } else { 'MISSING' })"
Write-Host ""

# ---------------------------------------------------------------- U1.1-U1.2 a fresh meter
Remove-Item -LiteralPath $meterPath -Force -ErrorAction SilentlyContinue
$r = Run @('--usage-report') 'fresh'
Check 'U1.1 a brand-new meter means 10 free uses, and --usage-report is what says so' `
      (($r.Exit -eq 0) -and (HasLine $r.Lines 'usage feature: cutout used=0 limit=10 remaining=10') -and
       (HasLine $r.Lines 'tampered=False')) `
      "exit=$($r.Exit) line='$(GetLine $r.Lines 'usage feature:')'"
Check 'U1.2 asking the meter creates no file (a read is a read)' `
      (-not (Test-Path -LiteralPath $meterPath)) "exists=$(Test-Path -LiteralPath $meterPath)"

# ---------------------------------------------------------------- U1.3-U1.5 the diagnostic seam
$r = Run @('--usage-set', 'cutout=3') 'set3'
$r2 = Run @('--usage-report') 'set3-report'
$macOk = $false
if (Test-Path -LiteralPath $meterPath) {
  $macOk = ((Get-Content -LiteralPath $meterPath -Raw) -match '"mac"\s*:\s*"[0-9A-Fa-f]{64}"')
}
Check 'U1.3 --usage-set writes a signed record, and 3 used leaves 7' `
      (($r.Exit -eq 0) -and $macOk -and
       (HasLine $r2.Lines 'usage feature: cutout used=3 limit=10 remaining=7')) `
      "exit=$($r.Exit) mac=$macOk line='$(GetLine $r2.Lines 'usage feature:')'"

$r = Run @('--usage-set', 'bogus=1') 'set-bogus'
Check 'U1.4 an unknown feature name is refused (a typo must not hand out unlimited uses)' `
      (($r.Exit -eq 2) -and (HasLine $r.Lines 'is not one of')) "exit=$($r.Exit)"

$r = Run @('--usage-set', 'cutout=many') 'set-badcount'
Check 'U1.5 a non-numeric count is refused' (($r.Exit -eq 2) -and (HasLine $r.Lines 'is not a count')) `
      "exit=$($r.Exit)"

# ---------------------------------------------------------------- U1.6 tampering fails closed
$tampered = $false
if (Test-Path -LiteralPath $meterPath) {
  $raw = Get-Content -LiteralPath $meterPath -Raw
  [System.IO.File]::WriteAllText($meterPath, $raw.Replace('"cutout": 3', '"cutout": 0'))
  $tampered = $true
}
$r = Run @('--usage-report') 'tampered'
Check 'U1.6 editing the number breaks the signature and the trial stays used up (never 10 again)' `
      ($tampered -and (HasLine $r.Lines 'tampered=True') -and
       (HasLine $r.Lines 'usage feature: cutout .*remaining=0') -and
       (HasLine $r.Lines 'WARNING usage:.*signature does not match')) `
      "line='$(GetLine $r.Lines 'usage report:')'"

# ---------------------------------------------------------------- U1.7 both doors, refused
$d1 = Join-Path $Work 'refused-cli'
New-Item -ItemType Directory -Force -Path $d1 | Out-Null
$src1 = Join-Path $d1 'real-512.png'
if ($havePortrait) { Copy-Item -LiteralPath $portrait -Destination $src1 -Force }
$sha1 = Get-Sha $src1
$null = Run @('--usage-set', 'cutout=10') 'exhaust'
$r = Run @('--remove-bg', $src1) 'refused-cli' $false
$files = @(Get-ChildItem -LiteralPath $d1 -File | Where-Object { $_.Name -ne 'real-512.png' })
CheckNeed $haveModel 'U1.7 with the trial used up, --remove-bg is refused with exit 12 and writes nothing' `
      (($r.Exit -eq 12) -and (HasLine $r.Lines '--remove-bg REFUSED: the cutout trial is used up') -and
       ($files.Count -eq 0) -and ((Get-Sha $src1) -eq $sha1)) `
      "exit=$($r.Exit) otherFiles=$($files.Count) untouched=$((Get-Sha $src1) -eq $sha1)"

# ---------------------------------------------------------------- U1.8 a refusal costs nothing
$null = Run @('--usage-set', 'cutout=0') 'reset0'
$d2 = Join-Path $Work 'refused-model'
New-Item -ItemType Directory -Force -Path $d2 | Out-Null
$src2 = Join-Path $d2 'real-512.png'
if ($havePortrait) { Copy-Item -LiteralPath $portrait -Destination $src2 -Force }
$absent = Join-Path $Work 'no-such-model.onnx'
$r = Run @('--remove-bg', $src2, '--remove-bg-model', $absent) 'refused-model'
$r2 = Run @('--usage-report') 'refused-model-report'
CheckNeed ($havePortrait) 'U1.8 a run that produces nothing (model missing) spends no free use' `
      (($r.Exit -eq 3) -and (HasLine $r.Lines 'ModelUnavailable|REFUSED by the engine') -and
       (HasLine $r2.Lines 'usage feature: cutout used=0 limit=10 remaining=10')) `
      "exit=$($r.Exit) line='$(GetLine $r2.Lines 'usage feature:')'"

# ---------------------------------------------------------------- U1.9 success spends exactly one
$d3 = Join-Path $Work 'success'
New-Item -ItemType Directory -Force -Path $d3 | Out-Null
$src3 = Join-Path $d3 'real-512.png'
if ($havePortrait) { Copy-Item -LiteralPath $portrait -Destination $src3 -Force }
$r = Run @('--remove-bg', $src3, '--remove-bg-model', $model) 'success'
$r2 = Run @('--usage-report') 'success-report'
CheckNeed $haveModel 'U1.9 one successful CUTOUT spends exactly one use (0 -> 1, not 0 -> 2)' `
      (($r.Exit -eq 0) -and (HasLine $r.Lines 'usage: cutout used 1/10') -and
       (HasLine $r2.Lines 'usage feature: cutout used=1 limit=10 remaining=9')) `
      "exit=$($r.Exit) line='$(GetLine $r2.Lines 'usage feature:')'"

# ---------------------------------------------------------------- U1.10-U1.11 the wheel door
# The wheel is the product's only real entry, so it gets its own gate check. --wheel-payload runs
# the resident path headlessly (the same seam verify-folder-drop uses) and commits through the
# tool layer exactly as Enter does.
$d4 = Join-Path $Work 'wheel-exhausted'
New-Item -ItemType Directory -Force -Path $d4 | Out-Null
$src4 = Join-Path $d4 'real-512.png'
if ($havePortrait) { Copy-Item -LiteralPath $portrait -Destination $src4 -Force }
$null = Run @('--usage-set', 'cutout=10') 'wheel-exhaust'
$r = Run @('--wheel-payload', $src4, '--wheel-tools', '--wheel-tool', 'remove-bg') 'wheel-exhausted' $true
$sawGate = HasLine $r.Lines 'ENTER: usage gate cutout'
$files = @(Get-ChildItem -LiteralPath $d4 -File | Where-Object { $_.Name -ne 'real-512.png' })
CheckNeed $haveModel 'U1.10 the wheel refuses a CUTOUT commit when the trial is used up (and starts no worker)' `
      ($sawGate -and
       (HasLine $r.Lines 'ENTER: remove-bg refused by the trial quota') -and
       ($files.Count -eq 0)) `
      "exit=$($r.Exit) gate=$sawGate otherFiles=$($files.Count)"

$d5 = Join-Path $Work 'wheel-allowed'
New-Item -ItemType Directory -Force -Path $d5 | Out-Null
$src5 = Join-Path $d5 'real-512.png'
if ($havePortrait) { Copy-Item -LiteralPath $portrait -Destination $src5 -Force }
$null = Run @('--usage-set', 'cutout=9') 'wheel-one-left'
$r = Run @('--wheel-payload', $src5, '--wheel-tools', '--wheel-tool', 'remove-bg') 'wheel-allowed' $true
$r2 = Run @('--usage-report') 'wheel-allowed-report'
CheckNeed $haveModel 'U1.11 with one use left the wheel runs it, and the success spends that use' `
      ((HasLine $r.Lines 'ENTER: usage gate cutout remaining=1 allowed=True') -and
       (HasLine $r.Lines 'usage: cutout succeeded -> spent one trial use, remaining=0') -and
       (HasLine $r2.Lines 'usage feature: cutout used=10 limit=10 remaining=0')) `
      "exit=$($r.Exit) line='$(GetLine $r2.Lines 'usage feature:')'"

# ---------------------------------------------------------------- totals
Expect 11
$fail = @($results | Where-Object { -not $_.Pass }).Count
$registered = $results.Count + $inconclusive.Count
if ($registered -eq 0) { Write-Host "FATAL: no checks ran - refusing to report success"; exit 2 }
Write-Host ""
Write-Host "checks=$registered pass=$($results.Count - $fail) fail=$fail inconclusive=$($inconclusive.Count) expected=$($script:ExpectedChecks)"
if ($registered -ne $script:ExpectedChecks) {
  Write-Host "FATAL: registered $registered checks but this suite must register exactly $($script:ExpectedChecks)."
  Write-Host "       A check block silently did not register (P1-2). Refusing to report a result."
  exit 1
}
Write-Host "usage summary: trial meter $($results.Count - $fail)/$($results.Count) checks passed"
if ($inconclusive.Count -gt 0) {
  Write-Host "INCONCLUSIVE ($($inconclusive.Count)):"
  $inconclusive | ForEach-Object { Write-Host "  $_" }
}
if ($fail -gt 0) {
  Write-Host "FAILURES:"
  $results | Where-Object { -not $_.Pass } | ForEach-Object { Write-Host "  $($_.Name)" }
  exit 1
}
if ($inconclusive.Count -gt 0) { exit 3 }
