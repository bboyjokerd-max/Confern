# cutout-diagnose.ps1 -- tell exactly WHAT is wrong with a Confern CUTOUT result.
#
# Why this exists: on 2026-10-14 the CUTOUT tool was shipping a matte instead of a cutout
# (source RGB was never merged back) and 107 green acceptance checks did not notice, because
# every one of them measured alpha only. A "feels not clean enough" report needs numbers that
# point at a specific defect, not adjectives. This script produces those numbers.
#
# It is deliberately pure ASCII: PowerShell 5.1 reads a BOM-less .ps1 as CP950, and CJK bytes
# inside the script would silently eat following quote characters (measured 2026-10-14).
#
# Usage:
#   tools\cutout-diagnose.ps1 -Image <photo> [-Exe <Confern.exe>] [-Refine guided|off|both]
#                             [-Work <dir>] [-Keep]
#
# Everything it reports is measured from the two files themselves (source vs output).
# It never trusts the product's own log lines.

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$Image,
  [string]$Exe,
  [ValidateSet('guided','off','both')][string]$Refine = 'both',
  [string]$Work,
  [switch]$Keep
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $Exe) {
  $repo = Split-Path -Parent $PSScriptRoot
  $Exe = Join-Path $repo 'src\Sidecar\bin\Release\net8.0-windows\Confern.exe'
}
if (-not (Test-Path $Exe))      { throw "Confern.exe not found: $Exe (build it first)" }
if (-not (Test-Path $Image))    { throw "image not found: $Image" }
if (-not $Work) { $Work = Join-Path $env:TEMP ('cutout-diagnose-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$Image = (Resolve-Path -LiteralPath $Image).Path
$ext   = [IO.Path]::GetExtension($Image)
$stem  = [IO.Path]::GetFileNameWithoutExtension($Image)
$run   = Join-Path $Work ('in' + $ext)
Copy-Item -LiteralPath $Image -Destination $run -Force

Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Refine -eq 'guided' -or $Refine -eq 'both') {
  & $Exe --remove-bg $run --log (Join-Path $Work 'guided.log') | Out-Null
}
if ($Refine -eq 'off' -or $Refine -eq 'both') {
  $runOff = Join-Path $Work ('in-off' + $ext)
  Copy-Item -LiteralPath $Image -Destination $runOff -Force
  & $Exe --remove-bg $runOff --remove-bg-refine off --log (Join-Path $Work 'off.log') | Out-Null
}
Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force

Add-Type -AssemblyName System.Drawing
if (-not ('CutoutProbe' -as [type])) {
Add-Type -Language CSharp -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CutoutProbe
{
    public static byte[] Plane(string p, out int w, out int h)
    {
        using (var b = new Bitmap(p))
        {
            w = b.Width; h = b.Height;
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), buf, y * w * 4, w * 4);
            b.UnlockBits(d);
            return buf;
        }
    }

    static int[] Distance(byte[] a, int w, int h)
    {
        var d = new int[w * h];
        const int BIG = 1 << 28;
        for (int i = 0; i < d.Length; i++) d[i] = (a[i * 4 + 3] >= 250) ? 0 : BIG;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, v = d[i];
                if (y > 0) { v = Math.Min(v, d[i - w] + 1); if (x > 0) v = Math.Min(v, d[i - w - 1] + 1); if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 1); }
                if (x > 0) v = Math.Min(v, d[i - 1] + 1);
                d[i] = v;
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x, v = d[i];
                if (y < h - 1) { v = Math.Min(v, d[i + w] + 1); if (x > 0) v = Math.Min(v, d[i + w - 1] + 1); if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 1); }
                if (x < w - 1) v = Math.Min(v, d[i + 1] + 1);
                d[i] = v;
            }
        return d;
    }

    // One report, measured from the source and the output only.
    public static string Report(string srcPath, string outPath)
    {
        int sw, sh, ow, oh;
        var s = Plane(srcPath, out sw, out sh);
        var o = Plane(outPath, out ow, out oh);
        if (sw != ow || sh != oh) return "SIZE MISMATCH source " + sw + "x" + sh + " output " + ow + "x" + oh;
        long px = (long)sw * sh;
        long opaque = 0, partial = 0, clear = 0, holes = 0, colourBad = 0, compared = 0;
        long near = 0, fog = 0, hairish = 0;
        var d = Distance(o, ow, oh);
        for (int i = 0; i < px; i++)
        {
            int A = o[i * 4 + 3];
            if (A >= 250) opaque++;
            else if (A == 0) clear++;
            else
            {
                partial++;
                if (d[i] <= 2) near++;
                else if (A < 128) { fog++; if (d[i] <= 4) hairish++; }
            }
            if (A >= 250)
            {
                compared++;
                if (s[i * 4] != o[i * 4] || s[i * 4 + 1] != o[i * 4 + 1] || s[i * 4 + 2] != o[i * 4 + 2]) colourBad++;
            }
        }
        for (int y = 1; y < oh - 1; y++)
            for (int x = 1; x < ow - 1; x++)
            {
                int i = (y * ow + x) * 4;
                if (o[i + 3] != 0) continue;
                bool allOpaque = true;
                for (int dy = -1; dy <= 1 && allOpaque; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (o[i + (dy * ow + dx) * 4 + 3] < 250) { allOpaque = false; break; }
                if (allOpaque) holes++;
            }
        // structure: components of visible alpha
        int comps = 0, mainArea = 0, orphanBlocks = 0; long orphanArea = 0;
        var seen = new bool[ow * oh];
        var stack = new int[ow * oh];
        for (int start = 0; start < ow * oh; start++)
        {
            if (seen[start] || o[start * 4 + 3] < 8) continue;
            int sp = 0; stack[sp++] = start; seen[start] = true; int area = 0;
            while (sp > 0)
            {
                int i = stack[--sp]; area++;
                int y = i / ow, x = i - y * ow;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= ow || ny >= oh) continue;
                        int j = ny * ow + nx;
                        if (seen[j] || o[j * 4 + 3] < 8) continue;
                        seen[j] = true; stack[sp++] = j;
                    }
            }
            comps++;
            if (area > mainArea) mainArea = area;
        }
        for (int start = 0; start < ow * oh; start++) { }
        // second pass for orphans
        var seen2 = new bool[ow * oh];
        for (int start = 0; start < ow * oh; start++)
        {
            if (seen2[start] || o[start * 4 + 3] < 8) continue;
            int sp = 0; stack[sp++] = start; seen2[start] = true; int area = 0;
            while (sp > 0)
            {
                int i = stack[--sp]; area++;
                int y = i / ow, x = i - y * ow;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= ow || ny >= oh) continue;
                        int j = ny * ow + nx;
                        if (seen2[j] || o[j * 4 + 3] < 8) continue;
                        seen2[j] = true; stack[sp++] = j;
                    }
            }
            if (area == mainArea) continue;
            if (area >= 50) { orphanBlocks++; orphanArea += area; }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format("  size                 : {0}x{1}", ow, oh));
        sb.AppendLine(string.Format("  opaque(A>=250)       : {0} ({1:F1}%)", opaque, 100.0 * opaque / px));
        sb.AppendLine(string.Format("  partial(0<A<255)     : {0} ({1:F2}% of image)", partial, 100.0 * partial / px));
        sb.AppendLine(string.Format("  clear(A=0)           : {0} ({1:F1}%)", clear, 100.0 * clear / px));
        sb.AppendLine("  --- subject colour (the 2026-10-14 defect) ---");
        sb.AppendLine(string.Format("  subject px compared  : {0}", compared));
        sb.AppendLine(string.Format("  RGB differs from src : {0}{1}", colourBad, colourBad == 0 ? "  OK" : "  <-- BROKEN, output is a matte"));
        sb.AppendLine("  --- transition band ---");
        sb.AppendLine(string.Format("  hugging subject(<=2px): {0} ({1:F1}% of partial) = real hair", near, partial == 0 ? 0 : 100.0 * near / partial));
        sb.AppendLine(string.Format("  faint & far (fog-ish) : {0} ({1:F1}% of partial)", fog, partial == 0 ? 0 : 100.0 * fog / partial));
        sb.AppendLine(string.Format("  of which <=4px away   : {0}  (if this is high the fog is really just hair)", hairish));
        sb.AppendLine("  --- structure ---");
        sb.AppendLine(string.Format("  components(A>=8)     : {0}   main={1}px", comps, mainArea));
        sb.AppendLine(string.Format("  orphan blocks(>=50px): {0}   total={1}px{2}", orphanBlocks, orphanArea,
            orphanArea > mainArea * 0.005 ? "  <-- visible leftovers (>=0.5% of subject)" : ""));
        sb.AppendLine(string.Format("  interior holes      : {0}{1}", holes, holes > 50 ? "  <-- subject has bites taken out" : ""));

        string verdict;
        if (colourBad > 0)
            verdict = "VERDICT: the output is a MATTE (colour lost) - this is the 2026-10-14 bug class, not a quality issue.";
        else if (partial == 0)
            verdict = "VERDICT: no soft transition at all - the edge is a hard 1-bit cut (hair is being lost).";
        else if (partial > 0 && 100.0 * near / partial < 35.0 && 100.0 * fog / partial > 40.0)
            verdict = "VERDICT: most of the soft band floats FAR from the subject -> fog/halo, not hair. A distance-gated cleanup (or a sharper matte) is the right fix.";
        else if (orphanArea > mainArea * 0.005)
            verdict = "VERDICT: disconnected leftover blocks are visible. Check whether the SOURCE really has content there before deleting them (measured: on a fur close-up they were real fur).";
        else if (holes > 50)
            verdict = "VERDICT: the subject has interior holes - the matte punched through it.";
        else
            verdict = "VERDICT: no measurable defect found. Colour is exact, the soft band hugs the subject, no leftovers, no holes. If it still looks unclean, the complaint is about DETAIL (hair strands not separated), which needs a higher-resolution matte, not a cleanup pass.";
        sb.Append("  ").Append(verdict);
        return sb.ToString();
    }
}
'@
}

function Show-One([string]$label, [string]$path) {
  Write-Host ("--- " + $label + " ---")
  if (-not (Test-Path $path)) { Write-Host "  (no output file)"; return }
  Write-Host ("  file                 : " + (Split-Path -Leaf $path) + "  " + (Get-Item $path).Length + " B")
  Write-Host ([CutoutProbe]::Report($Image, $path))
}

Write-Host ("=== CUTOUT diagnosis ===")
Write-Host ("  source : " + $Image)
Write-Host ("  exe    : " + $Exe)
Write-Host ("  work   : " + $Work)
Write-Host ""

if ($Refine -eq 'guided' -or $Refine -eq 'both') {
  $g = Join-Path $Work 'in (1).png'
  if (-not (Test-Path $g)) { $g = Join-Path $Work 'in.png' }
  Show-One 'default (guided refinement)' $g
}
if ($Refine -eq 'off' -or $Refine -eq 'both') {
  $o = Join-Path $Work 'in-off (1).png'
  if (-not (Test-Path $o)) { $o = Join-Path $Work 'in-off.png' }
  Show-One 'refinement off' $o
}

Write-Host ""
Write-Host "Reading the numbers:"
Write-Host "  * RGB differs from src must be 0, always. Anything else means the cutout lost its colours."
Write-Host "  * 'hugging subject' high (>=50%) and 'faint & far' low means the soft band is genuine hair."
Write-Host "  * 'faint & far' dominating means there is a halo/fog that can be removed WITHOUT touching hair."
Write-Host "  * orphan blocks are only dirt if the SOURCE is flat there; measure before deleting."
if (-not $Keep) { Write-Host ("(work dir kept: " + $Work + ")") }
