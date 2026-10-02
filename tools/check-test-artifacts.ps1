param(
  [Parameter(Mandatory=$true)][string]$Folder,
  [string]$LogDir = "$env:LOCALAPPDATA\Confern"
)

# Human-facing self-check after a test session. ASCII-only output on purpose
# (PS 5.1 reads BOM-less scripts as ANSI and would mangle CJK literals).
$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath $Folder)) { "folder not found: $Folder"; exit 2 }

"=== 1. leftover temp files (.tmp-*) ==="
$temps = @(Get-ChildItem -LiteralPath $Folder -File -Filter '*.tmp-*' -Recurse -ErrorAction SilentlyContinue)
if ($temps.Count -eq 0) {
  "  PASS  no .tmp-* leftovers (spec 7.2: a crash must not leave half-written outputs)"
} else {
  "  FAIL  $($temps.Count) leftover temp file(s):"
  $temps | ForEach-Object { "          $($_.FullName)  $($_.Length)B  $($_.LastWriteTime)" }
}

"=== 2. what is in the folder now ==="
Get-ChildItem -LiteralPath $Folder -File -Recurse -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTime -Descending | Select-Object -First 15 |
  ForEach-Object { "  {0,-42} {1,12:N0} B  {2}" -f $_.Name, $_.Length, $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') }

"=== 3. read-only check on every source-looking image ==="
"  (sources must be untouched: same size, same timestamp. Compare with what you remember,"
"   or re-run this after converting a second time - only converted files should appear above.)"

"=== 4. conversion log (last 25 conversion lines) ==="
$uiLog = Join-Path $LogDir 'logs\confern.log'
$workerLog = Join-Path $LogDir 'worker.log'
foreach ($log in @($uiLog, $workerLog)) {
  if (-not (Test-Path -LiteralPath $log)) { "  (no $log yet)"; continue }
  "  --- $log ---"
  Get-Content -LiteralPath $log -Encoding UTF8 |
    Select-String -Pattern 'converted|worker result|worker summary|convert summary|progress instrument|progress faded|ERROR|WARNING|FAIL' |
    Select-Object -Last 25 | ForEach-Object { "    $($_.Line)" }
}

"=== 5. did anything fail during the session? ==="
if (Test-Path -LiteralPath $uiLog) {
  $fails = @(Get-Content -LiteralPath $uiLog -Encoding UTF8 | Select-String -Pattern 'error=[A-Z][A-Za-z]+' |
             Where-Object { $_.Line -notmatch 'error=None' })
  if ($fails.Count -eq 0) { "  PASS  no failed conversions recorded" }
  else { "  NOTE  $($fails.Count) failed conversion(s) - the messages are meant to be readable:"; $fails | ForEach-Object { "        $($_.Line)" } }
}

"=== 6. leftover worker processes ==="
$procs = @(Get-Process -Name Confern -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { "  (no Confern process running)" }
else { $procs | ForEach-Object { "  pid=$($_.Id)  started=$($_.StartTime)  mem=$([Math]::Round($_.WorkingSet64/1MB,1)) MB" } }
