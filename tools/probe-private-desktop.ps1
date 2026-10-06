<#
  probe-private-desktop.ps1  --  feasibility probe for "run the acceptance checks on a PRIVATE
  Windows desktop (CreateDesktop) so that real input no longer fights the user for the mouse".

  ASCII ONLY.  PowerShell 5.1 reads a BOM-less source file as ANSI; any non-ASCII byte inside a
  string or comment can silently eat the following line. Keep this file pure 7-bit.

  WHAT IT MEASURES (all numbers come from the probe itself, nothing is assumed):
    Q1  Does a process on a private desktop capture THAT desktop (not black, not the user's)?
    Q2  Does SetCursorPos / SendInput issued on a private desktop move the USER's cursor?
    Q3  Can a GUI / drop-target window exist on a private desktop, and can a shell be found there?

  SAFETY:
    * The probe never sends a mouse BUTTON event anywhere. Only MOVE / SetCursorPos, and only
      from inside the private desktop.
    * Injection is skipped entirely with -SkipInjection, and it is also skipped automatically
      when the user's cursor does not stand still during the 1 s baseline.
    * Everything the probe creates is removed at the end (-KeepArtifacts keeps the temp dir).
    * The probe never touches the repository, the product build or any existing verify script.

  USAGE:
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe-private-desktop.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe-private-desktop.ps1 -SkipInjection
#>
[CmdletBinding()]
param(
  [string]$WorkRoot = '',
  [switch]$SkipInjection,
  [switch]$KeepArtifacts,
  [int]$DropTargetSeconds = 12
)

$ErrorActionPreference = 'Continue'
if ($WorkRoot -eq '') {
  $WorkRoot = Join-Path $env:TEMP ('dsh-pd-probe-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$DeskName = 'DshPdProbe_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$script:HDesk = [IntPtr]::Zero
$script:Launched = New-Object System.Collections.ArrayList
$script:HelperExe = ''
$script:Failure = ''

function Say([string]$m) { Write-Host $m }
function Rule([string]$t) {
  Write-Host ''
  Write-Host ('=' * 72)
  Write-Host $t
  Write-Host ('=' * 72)
}

# ---------------------------------------------------------------------------
# The helper.  Every measurement that must happen ON the private desktop runs
# inside this exe: the calling thread's desktop decides what GetDC(NULL),
# GetCursorPos and SendInput operate on, so it cannot be done from the probe
# process (which lives on the user's desktop).
# ---------------------------------------------------------------------------
$helperSrc = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class PdHelper
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt;
    }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct WNDCLASS {
        public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
    }
    public delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint c);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateDCW(string drv, string dev, string port, IntPtr dm);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int i);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformationW(IntPtr h, int i, StringBuilder b, int n, out uint need);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint rem);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EnumDesktopWindows(IntPtr d, EnumProc cb, IntPtr p);
    [DllImport("shell32.dll")] public static extern void DragAcceptFiles(IntPtr h, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint DragQueryFileW(IntPtr h, uint i, StringBuilder f, uint c);
    [DllImport("shell32.dll")] public static extern void DragFinish(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint f, IntPtr b);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string n);

    const uint WM_DROPFILES = 0x0233;
    const int PM_REMOVE = 1;

    static WndProc _keep;
    static string _dropLog = "";
    static IntPtr _dropWnd = IntPtr.Zero;

    static long Tick() { return (long)GetTickCount64(); }

    static string Hex(uint c) { return "0x" + c.ToString("X6"); }
    // 0x00RRGGBB, so the marker colour magenta reads as 0x00FF00FF on both paths.
    static string HexCol(Color c) { return "0x00" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

    public static string ThreadDesktopName()
    {
        var sb = new StringBuilder(256); uint need;
        IntPtr h = GetThreadDesktop(GetCurrentThreadId());
        if (GetUserObjectInformationW(h, 2, sb, 512, out need)) return sb.ToString();
        return "unknown";
    }

    static void Pump(int ms)
    {
        long end = Tick() + ms;
        MSG m;
        while (Tick() < end)
        {
            while (PeekMessageW(out m, IntPtr.Zero, 0, 0, PM_REMOVE)) { TranslateMessage(ref m); DispatchMessageW(ref m); }
            Thread.Sleep(10);
        }
    }

    static string EnumMine()
    {
        var list = new System.Collections.Generic.List<string>();
        EnumProc cb = delegate(IntPtr h, IntPtr x) {
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            list.Add("class=" + c.ToString() + " visible=" + IsWindowVisible(h));
            return true;
        };
        EnumDesktopWindows(IntPtr.Zero, cb, IntPtr.Zero); GC.KeepAlive(cb);
        if (list.Count == 0) return "(none)";
        return string.Join(" | ", list.ToArray());
    }

    static IntPtr PlainProc(IntPtr h, uint m, IntPtr w, IntPtr l) { return DefWindowProcW(h, m, w, l); }

    // --------------------------------------------------------------- captures
    // Four independent ways to get pixels out of THIS desktop. A and C are the ones the
    // product actually uses (Native.GetDC(NULL) and Graphics.CopyFromScreen).
    static void SaveHbmp(IntPtr hbmp, string path)
    {
        try { using (var img = Image.FromHbitmap(hbmp)) { img.Save(path, ImageFormat.Png); } }
        catch { }
    }

    static void GridLine(StringBuilder sb, Bitmap b)
    {
        int total = 0, nonblack = 0;
        for (int gy = 0; gy < 9; gy++)
            for (int gx = 0; gx < 16; gx++)
            {
                Color c = b.GetPixel((int)((gx + 0.5) * b.Width / 16.0), (int)((gy + 0.5) * b.Height / 9.0));
                total++;
                if ((int)c.R + c.G + c.B > 12) nonblack++;
            }
        sb.AppendLine("    grid_samples=" + total + " nonblack=" + nonblack + " pct=" + (100 * nonblack / total));
    }

    static void CaptureAttempts(StringBuilder sb, string outFile, IntPtr hwnd, int sw, int sh)
    {
        string dir = Path.GetDirectoryName(outFile);

        // A -- raw GetDC(NULL) + BitBlt (the screen DC of the calling thread's desktop)
        IntPtr sdc = GetDC(IntPtr.Zero);
        sb.AppendLine("A_GetDC_NULL hdc=0x" + sdc.ToInt64().ToString("X") + " err=" + Marshal.GetLastWin32Error());
        if (sdc != IntPtr.Zero)
        {
            uint px = GetPixel(sdc, 200, 150);
            int perr = Marshal.GetLastWin32Error();
            sb.AppendLine("A_GetPixel(200,150)=0x" + px.ToString("X8") + " err=" + perr + " (0xFFFFFFFF=CLR_INVALID)");
            IntPtr mdc = CreateCompatibleDC(sdc);
            IntPtr hb = CreateCompatibleBitmap(sdc, sw, sh);
            IntPtr old = SelectObject(mdc, hb);
            bool ok = BitBlt(mdc, 0, 0, sw, sh, sdc, 0, 0, 0x00CC0020);
            int err = Marshal.GetLastWin32Error();
            sb.AppendLine("A_BitBlt(src=screen DC of this desktop) ok=" + ok + " err=" + err + " (6=ERROR_INVALID_HANDLE)");
            if (ok)
            {
                string p = Path.Combine(dir, "cap-A-getdc-null.png");
                SaveHbmp(hb, p);
                sb.AppendLine("A_png=" + p);
                try { using (var b = new Bitmap(p)) { GridLine(sb, b); } } catch { }
            }
            SelectObject(mdc, old); DeleteObject(hb); DeleteDC(mdc);
            ReleaseDC(IntPtr.Zero, sdc);
        }

        // B -- CreateDC("DISPLAY")
        IntPtr ddc = CreateDCW("DISPLAY", null, null, IntPtr.Zero);
        sb.AppendLine("B_CreateDC(DISPLAY) hdc=0x" + ddc.ToInt64().ToString("X") + " err=" + Marshal.GetLastWin32Error());
        if (ddc != IntPtr.Zero)
        {
            sb.AppendLine("B_GetDeviceCaps HORZRES=" + GetDeviceCaps(ddc, 8) + " VERTRES=" + GetDeviceCaps(ddc, 10));
            IntPtr mdc = CreateCompatibleDC(ddc);
            IntPtr hb = CreateCompatibleBitmap(ddc, sw, sh);
            IntPtr old = SelectObject(mdc, hb);
            bool ok = BitBlt(mdc, 0, 0, sw, sh, ddc, 0, 0, 0x00CC0020);
            int err = Marshal.GetLastWin32Error();
            sb.AppendLine("B_BitBlt(src=DISPLAY DC) ok=" + ok + " err=" + err);
            if (ok)
            {
                string p = Path.Combine(dir, "cap-B-createdc-display.png");
                SaveHbmp(hb, p);
                sb.AppendLine("B_png=" + p);
                try { using (var b = new Bitmap(p)) { GridLine(sb, b); } } catch { }
            }
            SelectObject(mdc, old); DeleteObject(hb); DeleteDC(mdc);
            DeleteDC(ddc);
        }

        // C -- THE PRODUCT CALL: Graphics.CopyFromScreen, exactly as GlassSurface.CaptureFromScreen
        //      and EvidenceScreenGrab do it.
        string pngC = Path.Combine(dir, "cap-C-copyfromscreen.png");
        try
        {
            using (var b = new Bitmap(400, 300, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(b))
                    g.CopyFromScreen(0, 0, 0, 0, new Size(400, 300));
                b.Save(pngC, ImageFormat.Png);
                sb.AppendLine("C_CopyFromScreen threw=NO  png=" + pngC);
                Color c = b.GetPixel(100, 75);
                sb.AppendLine("C_png_pixel_100_75=" + HexCol(c) + "  (magenta=0x00FF00FF would mean the marker window was grabbed)");
                GridLine(sb, b);
            }
        }
        catch (Exception ex)
        {
            int ne = (ex is System.ComponentModel.Win32Exception) ? ((System.ComponentModel.Win32Exception)ex).NativeErrorCode : -1;
            sb.AppendLine("C_CopyFromScreen threw=YES " + ex.GetType().Name + " HResult=0x" + ex.HResult.ToString("X8") + " nativeError=" + ne);
            sb.AppendLine("C_consequence=GlassSurface.CaptureFromScreen() returns null -> colour fallback; EvidenceScreenGrab() logs FAILED");
        }

        // D -- PrintWindow(PW_RENDERFULLCONTENT) on a window of this desktop: the documented
        //      capture route that does not need the input desktop at all.
        string pngD = Path.Combine(dir, "cap-D-printwindow.png");
        try
        {
            using (var b = new Bitmap(400, 300, PixelFormat.Format32bppArgb))
            {
                bool ok;
                int err;
                using (var g = Graphics.FromImage(b))
                {
                    IntPtr dc = g.GetHdc();
                    ok = PrintWindow(hwnd, dc, 2);
                    err = Marshal.GetLastWin32Error();
                    g.ReleaseHdc(dc);
                }
                sb.AppendLine("D_PrintWindow(PW_RENDERFULLCONTENT) ok=" + ok + " err=" + err);
                b.Save(pngD, ImageFormat.Png);
                sb.AppendLine("D_png=" + pngD + " pixel_100_75=" + HexCol(b.GetPixel(100, 75)));
                GridLine(sb, b);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("D_PrintWindow threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    static IntPtr DropProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_DROPFILES)
        {
            uint n = 0;
            try { n = DragQueryFileW(w, 0xFFFFFFFF, null, 0); } catch { }
            var line = new StringBuilder();
            line.Append("t" + Tick() + " WM_DROPFILES count=" + n);
            for (uint i = 0; i < n && i < 8; i++)
            {
                var sb = new StringBuilder(1024);
                DragQueryFileW(w, i, sb, 1024);
                line.Append(" file[" + i + "]=" + sb.ToString());
            }
            try { File.AppendAllText(_dropLog, line.ToString() + "\r\n"); } catch { }
            try { DragFinish(w); } catch { }
            return IntPtr.Zero;
        }
        return DefWindowProcW(h, m, w, l);
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) { return 2; }
            if (args[0] == "q1" && args.Length >= 2) return Q1(args[1]);
            if (args[0] == "q2" && args.Length >= 7) return Q2(args[1], args[2], args[3], args[4], args[5], args[6]);
            if (args[0] == "q3target" && args.Length >= 4) return Q3Target(args[1], args[2], int.Parse(args[3]));
            if (args[0] == "q3feed" && args.Length >= 4) return Q3Feed(args[1], args[2], args[3]);
            if (args[0] == "q2ctl" && args.Length >= 2) return Q2Ctl(args[1]);
            return 3;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(args[args.Length - 1] + ".fatal.txt", ex.ToString()); } catch { }
            return 1;
        }
    }

    // ------------------------------------------------------------------ control
    // The control for Q2: the SAME two calls, from a process on the USER desktop, aimed at the
    // cursor's CURRENT position. A no-op move: it cannot move, click or disturb anything, and it
    // is what turns "denied on the private desktop" into a controlled comparison.
    static int Q2Ctl(string outFile)
    {
        var sb = new StringBuilder();
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        sb.AppendLine("desktop=" + ThreadDesktopName());
        POINT p0; bool got = GetCursorPos(out p0);
        sb.AppendLine("t" + Tick() + " GetCursorPos_ret=" + got + " cursor=" + p0.X + "," + p0.Y);
        bool s = SetCursorPos(p0.X, p0.Y);
        int es = Marshal.GetLastWin32Error();
        POINT p1; GetCursorPos(out p1);
        sb.AppendLine("t" + Tick() + " SetCursorPos(no-op, same point) ret=" + s + " err=" + es +
                      " cursor=" + p1.X + "," + p1.Y);
        var inp = new INPUT[1];
        inp[0].type = 0;
        inp[0].mi.dx = (int)Math.Round((double)p0.X * 65535.0 / (sw - 1));
        inp[0].mi.dy = (int)Math.Round((double)p0.Y * 65535.0 / (sh - 1));
        inp[0].mi.dwFlags = 0x0001 | 0x8000;   // MOVE | ABSOLUTE, never a button
        uint sent = SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        int e2 = Marshal.GetLastWin32Error();
        POINT p2; GetCursorPos(out p2);
        sb.AppendLine("t" + Tick() + " SendInput(MOVE to the same point) ret=" + sent + " err=" + e2 +
                      " cursor=" + p2.X + "," + p2.Y);
        sb.AppendLine("RESULT=on the input desktop SetCursorPos=" + s + " SendInput=" + sent +
                      " (1 = the event was accepted, 0 = refused)");
        File.WriteAllText(outFile, sb.ToString());
        return 0;
    }

    // ------------------------------------------------------------------ Q1
    static int Q1(string outFile)
    {
        var sb = new StringBuilder();
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        sb.AppendLine("desktop=" + ThreadDesktopName());
        sb.AppendLine("screen=" + sw + "x" + sh);
        POINT pc; bool gc = GetCursorPos(out pc);
        sb.AppendLine("t" + Tick() + " GetCursorPos_ret=" + gc + " readonly_cursor=" + pc.X + "," + pc.Y);
        sb.AppendLine("windows_before=" + EnumMine());

        // A magenta window is the marker: if the capture comes from THIS desktop it must be there.
        IntPtr brush = CreateSolidBrush(0x00FF00FF);
        _keep = new WndProc(PlainProc);
        var wc = new WNDCLASS();
        wc.lpfnWndProc = _keep;
        wc.hInstance = GetModuleHandleW(null);
        wc.hbrBackground = brush;
        wc.lpszClassName = "DshPdProbeMagentaWnd";
        RegisterClassW(ref wc);
        IntPtr h = CreateWindowExW(0x00000008, "DshPdProbeMagentaWnd", "probe",
            0x80000000 | 0x10000000, 0, 0, 400, 300, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        sb.AppendLine("magenta_hwnd=0x" + h.ToInt64().ToString("X"));
        ShowWindow(h, 5);
        UpdateWindow(h);
        Pump(900);
        sb.AppendLine("t" + Tick() + " after_pump windows=" + EnumMine());

        CaptureAttempts(sb, outFile, h, sw, sh);

        DestroyWindow(h);
        File.WriteAllText(outFile, sb.ToString());
        return 0;
    }

    // ------------------------------------------------------------------ Q2
    static int Q2(string t1x, string t1y, string t2x, string t2y, string goFile, string outFile)
    {
        var sb = new StringBuilder();
        int tx1 = int.Parse(t1x), ty1 = int.Parse(t1y), tx2 = int.Parse(t2x), ty2 = int.Parse(t2y);
        sb.AppendLine("desktop=" + ThreadDesktopName());
        sb.AppendLine("screen=" + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
        POINT p;
        GetCursorPos(out p);
        sb.AppendLine("t" + Tick() + " readonly_cursor=" + p.X + "," + p.Y);
        sb.AppendLine("target_setcursorpos=" + tx1 + "," + ty1);
        sb.AppendLine("target_sendinput=" + tx2 + "," + ty2);
        File.WriteAllText(outFile + ".ready", "ready");

        long dl = Tick() + 20000;
        while (!File.Exists(goFile) && Tick() < dl) Thread.Sleep(40);
        if (!File.Exists(goFile))
        {
            sb.AppendLine("RESULT=NO_GO");
            File.WriteAllText(outFile, sb.ToString());
            return 1;
        }
        sb.AppendLine("t" + Tick() + " go_seen");
        Thread.Sleep(1500);

        GetCursorPos(out p);
        sb.AppendLine("t" + Tick() + " p_before=" + p.X + "," + p.Y);
        bool ok1 = SetCursorPos(tx1, ty1);
        int e1 = Marshal.GetLastWin32Error();
        GetCursorPos(out p);
        sb.AppendLine("t" + Tick() + " after_SetCursorPos ret=" + ok1 + " err=" + e1 + " cursor=" + p.X + "," + p.Y);
        Thread.Sleep(400);
        GetCursorPos(out p);
        sb.AppendLine("t" + Tick() + " p_after_settle=" + p.X + "," + p.Y);

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        var inp = new INPUT[1];
        inp[0].type = 0;
        inp[0].mi.dx = (int)Math.Round((double)tx2 * 65535.0 / (sw - 1));
        inp[0].mi.dy = (int)Math.Round((double)ty2 * 65535.0 / (sh - 1));
        inp[0].mi.dwFlags = 0x0001 | 0x8000;   // MOVE | ABSOLUTE
        uint sent = SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        sb.AppendLine("t" + Tick() + " SendInput_move ret=" + sent + " err=" + Marshal.GetLastWin32Error());
        Thread.Sleep(400);
        GetCursorPos(out p);
        sb.AppendLine("t" + Tick() + " cursor_after_sendinput=" + p.X + "," + p.Y);

        SetCursorPos(0 + (tx1 < sw / 2 ? 1 : 0), 0);   // park it, no button ever
        GetCursorPos(out p);
        sb.AppendLine("t" + Tick() + " cursor_after_park=" + p.X + "," + p.Y);
        File.WriteAllText(outFile, sb.ToString());
        return 0;
    }

    // ------------------------------------------------------------------ Q3
    static int Q3Target(string logFile, string outFile, int seconds)
    {
        _dropLog = logFile;
        if (!File.Exists(logFile)) File.WriteAllText(logFile, "drop log " + ThreadDesktopName() + "\r\n");
        _keep = new WndProc(DropProc);
        var wc = new WNDCLASS();
        wc.lpfnWndProc = _keep;
        wc.hInstance = GetModuleHandleW(null);
        wc.hbrBackground = CreateSolidBrush(0x0088CC44);
        wc.lpszClassName = "DshPdProbeDropWnd";
        RegisterClassW(ref wc);
        _dropWnd = CreateWindowExW(0x00000008, "DshPdProbeDropWnd", "pd drop target",
            0x80000000 | 0x10000000, 300, 300, 500, 400, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        ShowWindow(_dropWnd, 5);
        UpdateWindow(_dropWnd);
        DragAcceptFiles(_dropWnd, true);
        var sb = new StringBuilder();
        sb.AppendLine("desktop=" + ThreadDesktopName());
        sb.AppendLine("drop_hwnd=0x" + _dropWnd.ToInt64().ToString("X"));
        sb.AppendLine("window_class=DshPdProbeDropWnd rect=300,300 500x400");
        sb.AppendLine("windows_on_this_desktop=" + EnumMine());
        File.WriteAllText(outFile, sb.ToString());
        Pump(seconds * 1000);
        DestroyWindow(_dropWnd);
        File.AppendAllText(logFile, "t" + Tick() + " target window closed after " + seconds + " s\r\n");
        return 0;
    }

    static int Q3Feed(string targetClass, string fileToDrop, string outFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("desktop=" + ThreadDesktopName());
        IntPtr h = IntPtr.Zero;
        long dl = Tick() + 10000;
        while (h == IntPtr.Zero && Tick() < dl) { h = FindWindowW(targetClass, null); if (h == IntPtr.Zero) Thread.Sleep(100); }
        if (h == IntPtr.Zero) { sb.AppendLine("RESULT=TARGET_NOT_FOUND"); File.WriteAllText(outFile, sb.ToString()); return 1; }
        sb.AppendLine("found hwnd=0x" + h.ToInt64().ToString("X"));

        byte[] path = Encoding.Unicode.GetBytes(fileToDrop + "\0\0");
        int hdr = 20, total = hdr + path.Length;
        IntPtr mem = GlobalAlloc(0x0042, (IntPtr)total);
        if (mem == IntPtr.Zero) { sb.AppendLine("RESULT=ALLOC_FAILED"); File.WriteAllText(outFile, sb.ToString()); return 1; }
        IntPtr ptr = GlobalLock(mem);
        byte[] buf = new byte[total];
        BitConverter.GetBytes(hdr).CopyTo(buf, 0);   // pFiles offset 0
        BitConverter.GetBytes(0).CopyTo(buf, 12);   // fNC
        BitConverter.GetBytes(1).CopyTo(buf, 16);   // fWide = TRUE
        path.CopyTo(buf, hdr);
        Marshal.Copy(buf, 0, ptr, total);
        GlobalUnlock(mem);

        bool ok = PostMessageW(h, WM_DROPFILES, mem, IntPtr.Zero);
        sb.AppendLine("t" + Tick() + " PostMessage(WM_DROPFILES) ret=" + ok + " err=" + Marshal.GetLastWin32Error() + " hdrop=0x" + mem.ToInt64().ToString("X"));
        sb.AppendLine("payload=" + fileToDrop);
        File.WriteAllText(outFile, sb.ToString());
        return ok ? 0 : 1;
    }
}
'@

# ---------------------------------------------------------------------------
# Probe-side interop (runs on the USER desktop).
# ---------------------------------------------------------------------------
$nativeSrc = @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class PdNative
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct STARTUPINFO {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION {
        public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId;
    }
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopW(string name, string dev, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDesktopWindows(IntPtr d, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformationW(IntPtr h, int i, StringBuilder b, int n, out uint need);

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint f, IntPtr b);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);

    // A real HDROP delivered by WM_DROPFILES. Used to prove the drop path of a window that
    // lives on the private desktop WITHOUT moving the mouse.
    public static string PostDrop(long hwnd, string file)
    {
        byte[] path = Encoding.Unicode.GetBytes(file + "\0\0");
        int hdr = 20, total = hdr + path.Length;
        IntPtr mem = GlobalAlloc(0x0042, (IntPtr)total);
        if (mem == IntPtr.Zero) return "GlobalAlloc failed";
        IntPtr ptr = GlobalLock(mem);
        byte[] buf = new byte[total];
        BitConverter.GetBytes(hdr).CopyTo(buf, 0);
        BitConverter.GetBytes(0).CopyTo(buf, 12);
        BitConverter.GetBytes(1).CopyTo(buf, 16);
        path.CopyTo(buf, hdr);
        Marshal.Copy(buf, 0, ptr, total);
        GlobalUnlock(mem);
        bool ok = PostMessageW((IntPtr)hwnd, 0x0233, mem, IntPtr.Zero);
        return ok ? ("posted ok hdrop=0x" + mem.ToInt64().ToString("X"))
                  : ("PostMessage failed err=" + Marshal.GetLastWin32Error());
    }

    public static string MyDesktopName()
    {
        var sb = new StringBuilder(256); uint need;
        if (GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, sb, 512, out need)) return sb.ToString();
        return "unknown";
    }
    public static string Cursor()
    {
        POINT p; if (!GetCursorPos(out p)) return "fail";
        return p.X + "," + p.Y;
    }
    public static string Pixel(int x, int y)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return "nodc";
        uint c = GetPixel(dc, x, y);
        ReleaseDC(IntPtr.Zero, dc);
        if (c == 0xFFFFFFFF) return "CLR_INVALID";
        return "0x" + c.ToString("X6");
    }
    public static string[] DesktopWindows(IntPtr desk)
    {
        var list = new System.Collections.Generic.List<string>();
        EnumProc cb = delegate(IntPtr h, IntPtr x) {
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            uint pid; GetWindowThreadProcessId(h, out pid);
            list.Add(c.ToString() + "|pid=" + pid + "|vis=" + IsWindowVisible(h));
            return true;
        };
        EnumDesktopWindows(desk, cb, IntPtr.Zero); GC.KeepAlive(cb);
        return list.ToArray();
    }
}
'@

$t0 = Get-Date
Rule 'dsh probe: private desktop feasibility  (ASCII probe, no repo changes)'
Say ("work dir   : " + $WorkRoot)
Say ("desktop    : " + $DeskName)
Say ("powerShell : " + $PSVersionTable.PSVersion.ToString())
Say ("started    : " + $t0.ToString('HH:mm:ss.fff'))

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

try {
  # ---- compile the helper -------------------------------------------------
  $csPath = Join-Path $WorkRoot 'pd-helper.cs'
  Set-Content -LiteralPath $csPath -Value $helperSrc -Encoding ASCII
  $exePath = Join-Path $WorkRoot 'pd-helper.exe'
  $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
  if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
  if (Test-Path -LiteralPath $csc) {
    $out = & $csc /nologo /target:exe /optimize+ "/out:$exePath" /r:System.dll /r:System.Drawing.dll $csPath 2>&1
    Say ("compiler   : csc " + $csc)
  } else {
    Add-Type -TypeDefinition $helperSrc -OutputAssembly $exePath -OutputType ConsoleApplication `
      -ReferencedAssemblies @('System.dll', 'System.Drawing.dll') -ErrorAction Stop
    Say "compiler   : Add-Type -OutputAssembly (csc.exe not found)"
  }
  if (-not (Test-Path -LiteralPath $exePath)) {
    Say "COMPILE FAILED:"; $out | ForEach-Object { Say ("  " + $_) }
    throw "helper did not compile"
  }
  $script:HelperExe = $exePath
  Say ("helper exe : " + $exePath + "  (" + (Get-Item -LiteralPath $exePath).Length + " bytes)")

  # ---- probe-side interop -------------------------------------------------
  Add-Type -TypeDefinition $nativeSrc -ErrorAction Stop
  Say ("user desktop: " + [PdNative]::MyDesktopName() + "  screen=" +
       ('' + [PdNative]::GetSystemMetrics(0) + 'x' + [PdNative]::GetSystemMetrics(1)))
  Say ("user cursor : " + [PdNative]::Cursor())
  $userPix1 = [PdNative]::Pixel(200, 150)
  $userPix2 = [PdNative]::Pixel(100, 75)
  $userPix3 = [PdNative]::Pixel(300, 225)
  $userPix4 = [PdNative]::Pixel(390, 290)
  Say ("user desktop pixels @200,150 / @100,75 / @300,225 / @390,290 = $userPix1 / $userPix2 / $userPix3 / $userPix4")

  # ---- create the private desktop ----------------------------------------
  # NOTE: PowerShell binds $null to an EMPTY STRING for a [string] parameter, and
  # CreateDesktop(..., "") fails with 87 / ERROR_INVALID_PARAMETER. [NullString]::Value
  # is the only spelling that really passes NULL.
  $hDesk = [PdNative]::CreateDesktopW($DeskName, [NullString]::Value, [IntPtr]::Zero, 0, [uint32]269419007, [IntPtr]::Zero)
  if ($hDesk -eq [IntPtr]::Zero) {
    Say ("CreateDesktop FAILED err=" + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    throw "CreateDesktop failed"
  }
  $script:HDesk = $hDesk
  Say ("CreateDesktop OK  handle=0x" + $hDesk.ToInt64().ToString('X'))
  Say ("windows on the fresh private desktop: " + (([PdNative]::DesktopWindows($hDesk)) -join ' , '))

  function Start-OnPrivateDesktop([string]$Exe, [string[]]$Argv) {
    return Start-Process-WithDesktop -Exe $Exe -Argv $Argv -Desktop $DeskName
  }
  function Start-OnUserDesktop([string]$Exe, [string[]]$Argv) {
    return Start-Process-WithDesktop -Exe $Exe -Argv $Argv -Desktop ''
  }
  function Start-Process-WithDesktop([string]$Exe, [string[]]$Argv, [string]$Desktop) {
    $si = New-Object PdNative+STARTUPINFO
    $si.cb = [Runtime.InteropServices.Marshal]::SizeOf([type]('PdNative+STARTUPINFO'))
    if ($Desktop -eq '') { $si.lpDesktop = [NullString]::Value } else { $si.lpDesktop = $Desktop }
    $si.dwFlags = 0x1
    $si.wShowWindow = 0
    $pi = New-Object PdNative+PROCESS_INFORMATION
    $cmd = '"' + $Exe + '"'
    foreach ($a in $Argv) { $cmd += ' "' + $a + '"' }
    # CREATE_NO_WINDOW keeps a console from ever flashing on the user's desktop.
    $ok = [PdNative]::CreateProcessW($Exe, $cmd, [IntPtr]::Zero, [IntPtr]::Zero, $false,
            0x08000000 -bor 0x00000400, [IntPtr]::Zero, $WorkRoot, [ref]$si, [ref]$pi)
    if (-not $ok) {
      throw ("CreateProcess failed err=" + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + " for " + $cmd)
    }
    [void]$script:Launched.Add($pi.dwProcessId)
    [PdNative]::CloseHandle($pi.hThread) | Out-Null
    return @{ Pid = $pi.dwProcessId; Handle = $pi.hProcess }
  }
  function Wait-Proc($h, [int]$ms) { [void][PdNative]::WaitForSingleObject($h.Handle, [uint32]$ms) }
  function Read-Out([string]$p) {
    if (Test-Path -LiteralPath $p) { return (Get-Content -LiteralPath $p -Encoding ASCII) -join "`n" }
    return "(missing: $p)"
  }

  # ======================================================================== Q1
  Rule 'Q1  can a process on the private desktop capture THAT desktop?'
  $q1Out = Join-Path $WorkRoot 'q1.txt'
  $p1 = Start-OnPrivateDesktop $exePath @('q1', $q1Out)
  Wait-Proc $p1 30000
  $q1 = Read-Out $q1Out
  $q1 -split "`n" | ForEach-Object { Say ('  ' + $_) }
  Say ''
  Say ("  USER desktop at the same coordinates: 200,150=$userPix1  100,75=$userPix2  300,225=$userPix3  390,290=$userPix4")
  Say ''
  Say '  --- control: the SAME product call, executed from the USER desktop ---'
  Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
  $bmpU = New-Object System.Drawing.Bitmap 40, 30
  $gU = [System.Drawing.Graphics]::FromImage($bmpU)
  $uGrab = 'threw'
  try {
    $gU.CopyFromScreen(200, 150, 0, 0, (New-Object System.Drawing.Size 40, 30))
    $cu = $bmpU.GetPixel(0, 0)
    $uGrab = ('0x{0:X2}{1:X2}{2:X2}' -f $cu.B, $cu.G, $cu.R)
  } catch { $uGrab = 'threw ' + $_.Exception.Message }
  $gU.Dispose(); $bmpU.Dispose()
  Say ("  user desktop: GetPixel(200,150)=$userPix1   CopyFromScreen(200,150)=$uGrab   agree=" + ($uGrab -eq $userPix1))
  Say '  (the identical CopyFromScreen call therefore DOES return real pixels when the desktop is the input desktop)'

  $aVal = 'n/a'; if ($q1 -match 'A_BitBlt\(src=screen DC of this desktop\) ok=(\w+)') { $aVal = $Matches[1] }
  $bVal = 'n/a'; if ($q1 -match 'B_BitBlt\(src=DISPLAY DC\) ok=(\w+)') { $bVal = $Matches[1] }
  $cThrew = ($q1 -match 'C_CopyFromScreen threw=YES')
  $dVal = 'n/a'; if ($q1 -match 'D_PrintWindow\(PW_RENDERFULLCONTENT\) ok=(\w+)') { $dVal = $Matches[1] }
  $dMagenta = ($q1 -match 'D_png=.*pixel_100_75=0x00FF00FF')
  $q1Verdict = 'UNDETERMINED'
  if ($aVal -eq 'False' -and $cThrew -and $dVal -eq 'True' -and $dMagenta) {
    $q1Verdict = 'DESKTOP_CAPTURE_FAILS__PER_WINDOW_PRINTWINDOW_WORKS'
  } elseif ($aVal -eq 'True' -and -not $cThrew) {
    $q1Verdict = 'DESKTOP_CAPTURE_WORKS'
  } elseif ($aVal -eq 'False' -or $cThrew) {
    $q1Verdict = 'DESKTOP_CAPTURE_FAILS' + $(if ($dVal -eq 'True') { '__PER_WINDOW_PRINTWINDOW_WORKS' } else { '__NO_ALTERNATIVE_MEASURED' })
  }
  Say ("  Q1 VERDICT: $q1Verdict")
  Say ("    A GetDC(NULL)+BitBlt ok=$aVal | B CreateDC(DISPLAY)+BitBlt ok=$bVal | C CopyFromScreen threw=$cThrew | D PrintWindow ok=$dVal magenta=$dMagenta")

  # ======================================================================== Q2
  Rule 'Q2  does input issued on the private desktop reach the USER cursor?'
  $ub = [PdNative]::Cursor()
  $q2Out = Join-Path $WorkRoot 'q2.txt'
  $goFile = Join-Path $WorkRoot 'q2.go'
  $sw = [PdNative]::GetSystemMetrics(0); $sh = [PdNative]::GetSystemMetrics(1)
  $ux = [int]($ub -split ',')[0]; $uy = [int]($ub -split ',')[1]
  $t1x = if ($ux -lt $sw / 2) { 40 } else { $sw - 40 }
  $t1y = if ($uy -lt $sh / 2) { 40 } else { $sh - 40 }
  $t2x = if ($ux -lt $sw / 2) { $sw - 40 } else { 40 }
  $t2y = if ($uy -lt $sh / 2) { $sh - 40 } else { 40 }

  # Baseline: is the user's hand on the mouse right now?
  $base = New-Object System.Collections.ArrayList
  for ($i = 0; $i -lt 10; $i++) { [void]$base.Add([PdNative]::Cursor()); [Threading.Thread]::Sleep(100) }
  $baseSpan = (@($base | Sort-Object -Unique)).Count
  Say ("  baseline: user cursor samples over 1 s = " + (($base | Select-Object -Unique) -join ' '))
  Say ("  baseline: distinct positions = $baseSpan  (1 = the hand is off the mouse)")

  Say '  --- control: the same two calls run from the USER desktop, aimed at the current cursor point (a no-op move) ---'
  $ctlOut = Join-Path $WorkRoot 'q2ctl.txt'
  $p0c = Start-OnUserDesktop $exePath @('q2ctl', $ctlOut)
  Wait-Proc $p0c 20000
  $ctl = Read-Out $ctlOut
  $ctl -split "`n" | ForEach-Object { Say ('  ' + $_) }
  Say ("  user cursor right after the control: " + [PdNative]::Cursor() + "  (a no-op move cannot change it)")

  $privQ1 = 'not measured'
  if ($q1 -match 'GetCursorPos_ret=(\w+) readonly_cursor=([\d,]+)') { $privQ1 = "ret=$($Matches[1]) pos=$($Matches[2])" }
  Say ("  READ-ONLY evidence: the private desktop reports its own cursor $privQ1 while the user desktop reports $ub at the same time")

  $idle = ($baseSpan -le 1)
  if (-not $SkipInjection -and -not $idle) {
    Say '  gate: the cursor moved during the 1 s baseline -> waiting up to 45 s for a still hand'
    $waitUntil = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $waitUntil -and -not $idle) {
      $probe = @()
      for ($i = 0; $i -lt 8; $i++) { $probe += [PdNative]::Cursor(); [Threading.Thread]::Sleep(100) }
      $uniq = @($probe | Sort-Object -Unique)
      if ($uniq.Count -eq 1) { $idle = $true; Say ("  gate: still hand detected at " + $uniq[0]) }
    }
  }
  $inject = (-not $SkipInjection) -and $idle
  if (-not $inject) {
    Say '  GATE: no still-hand window -> the injection stage is SKIPPED; only the programmatic evidence above is reported.'
  }
  if ($inject) {
    Say ("  targets: SetCursorPos -> $t1x,$t1y   SendInput MOVE -> $t2x,$t2y   (no button event is ever sent)")
    $p2 = Start-OnPrivateDesktop $exePath @('q2', "$t1x", "$t1y", "$t2x", "$t2y", $goFile, $q2Out)
    $ready = $q2Out + '.ready'
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-Path -LiteralPath $ready) -and (Get-Date) -lt $deadline) { [Threading.Thread]::Sleep(50) }
    $samples = New-Object System.Collections.ArrayList
    $last = ''
    Set-Content -LiteralPath $goFile -Value 'go' -Encoding ASCII
    $tEnd = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $tEnd) {
      $now = [PdNative]::Cursor()
      if ($now -ne $last) {
        [void]$samples.Add(([PdNative]::GetTickCount64()).ToString() + ':' + $now)
        $last = $now
      }
      [Threading.Thread]::Sleep(5)
    }
    Wait-Proc $p2 20000
    $q2 = Read-Out $q2Out
    Say '  --- helper log (running on the private desktop) ---'
    $q2 -split "`n" | ForEach-Object { Say ('  ' + $_) }
    Say '  --- user-desktop cursor samples (tick:position, only on change) ---'
    Say ('  ' + (($samples | Select-Object -First 40) -join '  '))
    $hit1 = @($samples | Where-Object { $_ -like "*:$t1x,$t1y" }).Count
    $hit2 = @($samples | Where-Object { $_ -like "*:$t2x,$t2y" }).Count
    $ua = [PdNative]::Cursor()
    Say ''
    Say ("  user cursor before=$ub  after=$ua  distinct positions during the window=" + (@($samples | ForEach-Object { ($_ -split ':')[1] } | Sort-Object -Unique).Count))
    Say ("  samples that landed EXACTLY on the private-desktop targets: SetCursorPos target=$hit1  SendInput target=$hit2")
    $privMoved = ($q2 -match 'after_SetCursorPos ret=True') -and ($q2 -match ("after_SetCursorPos ret=True err=\d+ cursor=$t1x,$t1y"))
    $siMoved = ($q2 -match ("cursor_after_sendinput=$t2x,$t2y"))
    Say ("  private-desktop cursor reached the SetCursorPos target: $privMoved ; reached the SendInput target: $siMoved")
    if (-not $privMoved -and -not $siMoved) {
      Say '  NOTE: the private desktop did not take the injected position at all -> the desktop has no own cursor state; see the report.'
    }
    $q2Verdict = if ($hit1 -eq 0 -and $hit2 -eq 0) { 'USER_CURSOR_UNAFFECTED' } else { 'LEAKED_TO_USER_DESKTOP' }
  } else {
    $q2Verdict = 'SKIPPED_BY_GATE'
  }
  Say ("  Q2 VERDICT: $q2Verdict")

  # ======================================================================== Q3
  Rule 'Q3  can a shell / drop target live on the private desktop?'
  $before = @([PdNative]::DesktopWindows([IntPtr]::Zero))
  Say ("  user-desktop windows before: " + $before.Count)
  $shell = @()
  foreach ($w in ([PdNative]::DesktopWindows($hDesk))) {
    if ($w -match '^(Shell_TrayWnd|Progman|WorkerW|CabinetWClass|Shell_SecondaryTrayWnd|TaskListThumbnailWnd|Progman)\|') { $shell += $w }
  }
  Say ("  shell classes present on the private desktop BEFORE anything: " + $(if ($shell.Count -eq 0) { '(none)' } else { ($shell -join ' , ') }))

  $dropLog = Join-Path $WorkRoot 'drop.log'
  $q3Out = Join-Path $WorkRoot 'q3target.txt'
  $p3 = Start-OnPrivateDesktop $exePath @('q3target', $dropLog, $q3Out, "$DropTargetSeconds")
  $deadline = (Get-Date).AddSeconds(10)
  while (-not (Test-Path -LiteralPath $q3Out) -and (Get-Date) -lt $deadline) { [Threading.Thread]::Sleep(100) }
  $q3 = Read-Out $q3Out
  $q3 -split "`n" | ForEach-Object { Say ('  ' + $_) }
  $deskWins = @([PdNative]::DesktopWindows($hDesk))
  Say ("  windows the PROBE sees on the private desktop handle: " + (($deskWins) -join ' , '))

  # a drop target can act only if a drop can reach it; deliver one from inside the desktop
  $sampleFile = Join-Path $WorkRoot 'drop-sample.txt'
  Set-Content -LiteralPath $sampleFile -Value ('private-desktop drop probe ' + [guid]::NewGuid().ToString()) -Encoding ASCII
  $q3fOut = Join-Path $WorkRoot 'q3feed.txt'
  $p4 = Start-OnPrivateDesktop $exePath @('q3feed', 'DshPdProbeDropWnd', $sampleFile, $q3fOut)
  Wait-Proc $p4 15000
  $q3f = Read-Out $q3fOut
  $q3f -split "`n" | ForEach-Object { Say ('  feed: ' + $_) }

  # Second delivery path: the PROBE itself (living on the user's desktop) posts the drop.
  # If this works, a future harness can drive a private-desktop check without ever touching
  # the mouse and without leaving its own desktop.
  $hwnd = 0L
  if ($q3 -match 'drop_hwnd=0x([0-9A-F]+)') { $hwnd = [Convert]::ToInt64($Matches[1], 16) }
  if ($hwnd -ne 0) {
    $r = [PdNative]::PostDrop($hwnd, $sampleFile)
    Say ("  cross-desktop post from the probe (user desktop -> window on the private desktop): " + $r)
    [Threading.Thread]::Sleep(1200)
  }
  Say '  --- drop target log ---'
  [Threading.Thread]::Sleep(600)
  $dropLogText = Read-Out $dropLog
  $dropLogText -split "`n" | ForEach-Object { Say ('  ' + $_) }
  $dropCount = @([regex]::Matches($dropLogText, 'WM_DROPFILES count=1')).Count
  Say ("  drops received by the private-desktop window: " + $dropCount + " of 2 attempted (same-desktop feeder + cross-desktop post)")

  Stop-Process -Id $p3.Pid -Force -ErrorAction SilentlyContinue
  [void][PdNative]::WaitForSingleObject($p3.Handle, 5000)

  # can a FOREIGN Microsoft GUI binary get a window up there?  (explorer.exe is deliberately NOT
  # launched: a live shell on the user's desktop is exactly what this probe must not disturb.)
  $foreign = @('C:\Windows\System32\charmap.exe', 'C:\Windows\System32\osk.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
  if ($foreign) {
    Say ("  foreign GUI test binary: " + $foreign)
    $pf = Start-OnPrivateDesktop $foreign @()
    [Threading.Thread]::Sleep(2500)
    $w2 = @([PdNative]::DesktopWindows($hDesk))
    Say ("  private desktop windows while it runs: " + ($w2 -join ' , '))
    $after = @([PdNative]::DesktopWindows([IntPtr]::Zero))
    $newUser = @($after | Where-Object { $before -notcontains $_ })
    Say ("  NEW windows on the USER desktop: " + $(if ($newUser.Count -eq 0) { '(none)' } else { ($newUser -join ' , ') }))
    Stop-Process -Id $pf.Pid -Force -ErrorAction SilentlyContinue
    [void][PdNative]::WaitForSingleObject($pf.Handle, 5000)
  } else {
    Say '  foreign GUI test: no candidate binary found, skipped'
  }
  $after2 = @([PdNative]::DesktopWindows([IntPtr]::Zero))
  $newUser2 = @($after2 | Where-Object { $before -notcontains $_ })
  Say ("  user-desktop windows after the whole Q3 phase: " + $after2.Count + " (new vs before: " + $newUser2.Count + ")")
  if ($newUser2.Count -gt 0) { Say ("    new on the user desktop: " + ($newUser2 -join ' , ')) }

  $q3Verdict = if ($q3 -match 'drop_hwnd=0x[0-9A-F]+') { 'DROP_TARGET_WINDOW_CREATED' } else { 'NO_WINDOW_ON_PRIVATE_DESKTOP' }
  Say ("  Q3 VERDICT: $q3Verdict ; shell present on the private desktop: " + $(if ($shell.Count -eq 0) { 'NO' } else { 'YES' }))
}
catch {
  $script:Failure = $_.Exception.Message
  Say ''
  Say ("PROBE ERROR: " + $script:Failure)
}
finally {
  # ---- cleanup: processes, desktop, temp files ---------------------------
  Rule 'cleanup'
  foreach ($procId in $script:Launched) {
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($p) {
      if ($p.ProcessName -eq 'pd-helper') {
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        Say ("  killed helper pid " + $procId)
      } else {
        Say ("  NOT mine, left alone: pid " + $procId + " (" + $p.ProcessName + ")")
      }
    }
  }
  if ($script:HDesk -ne [IntPtr]::Zero -and ('PdNative' -as [type])) {
    $r = [PdNative]::CloseDesktop($script:HDesk)
    Say ("  CloseDesktop -> " + $r)
  }
  if ($KeepArtifacts) {
    Say ("  artifacts kept in " + $WorkRoot)
  } else {
    for ($i = 0; $i -lt 10; $i++) {
      Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
      if (-not (Test-Path -LiteralPath $WorkRoot)) { break }
      Start-Sleep -Milliseconds 200
    }
    Say ("  removed " + $WorkRoot + " -> still exists=" + (Test-Path -LiteralPath $WorkRoot))
  }
  $left = @(Get-Process pd-helper -ErrorAction SilentlyContinue).Count
  Say ("  pd-helper processes left: " + $left)
  Say ("  elapsed: " + [int]((Get-Date) - $t0).TotalSeconds + " s")
}
