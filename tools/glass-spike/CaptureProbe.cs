using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassSpike;

/// <summary>
/// 擷取路徑對照量測。
///
/// 為什麼需要這個：主量測發現 BitBlt 的耗時跟**抓取面積無關**（16x16 與 512x512 都是 ~16.6 ms），
/// 而且 ~16.6 ms 剛好是 60 Hz 的一個 vblank。這代表它不是頻寬問題，而是**某種週期同步**。
/// 如果這是真的，那「用 BitBlt 抓螢幕做毛玻璃」的設計就會直接撞上這個固定的 16.7 ms，
/// 必須換路徑。這裡把幾個候選路徑放在同一個行程、同一個時間窗內量，避免跨行程比較失準。
/// </summary>
internal static class CaptureProbe
{
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public int ptScreenPosX;
        public int ptScreenPosY;
    }
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int cx, int cy,
                                                                IntPtr s, int sx, int sy, int rop);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public static void MeasureAll(int x, int y, int size)
    {
        const int N = 20;

        // --- A) GetDC(NULL) + BitBlt（目前實作） ---
        using (var cap = new ScreenCapture(x, y, size))
        {
            Bench("A. GetDC(NULL) + BitBlt", () =>
            {
                var sw = Stopwatch.StartNew();
                cap.Grab(out _, out _);
                cap.MemDcTouch();
                return sw.Elapsed.TotalMilliseconds;
            }, N);
        }

        // --- B) GetDC(NULL)，BitBlt 後把像素讀一遍（強制 flush，排除「延後才做」的可能） ---
        using (var cap = new ScreenCapture(x, y, size))
        {
            Bench("B. (A) + read back every pixel", () =>
            {
                var sw = Stopwatch.StartNew();
                cap.Grab(out _, out _);
                cap.MemDcTouch();
                cap.ReadBackChecksum();
                return sw.Elapsed.TotalMilliseconds;
            }, N);
        }

        // --- C) 桌面視窗 DC ---
        {
            IntPtr hwnd = GetDesktopWindow();
            IntPtr src = GetWindowDC(hwnd);
            IntPtr mem = Native.CreateCompatibleDC(src);
            var bmi = MakeBmi(size);
            IntPtr dib = Native.CreateDIBSection(src, ref bmi, 0, out _, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(mem, dib);
            Bench("C. GetWindowDC(desktop) + BitBlt", () =>
            {
                var sw = Stopwatch.StartNew();
                BitBlt(mem, 0, 0, size, size, src, x, y, SRCCOPY);
                return sw.Elapsed.TotalMilliseconds;
            }, N);
            Native.SelectObject(mem, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(mem);
            ReleaseDC(hwnd, src);
        }

        // --- D) GetDC(NULL) + BitBlt | CAPTUREBLT（含分層視窗） ---
        {
            IntPtr src = GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(src);
            var bmi = MakeBmi(size);
            IntPtr dib = Native.CreateDIBSection(src, ref bmi, 0, out _, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(mem, dib);
            Bench("D. BitBlt | CAPTUREBLT", () =>
            {
                var sw = Stopwatch.StartNew();
                BitBlt(mem, 0, 0, size, size, src, x, y, SRCCOPY | CAPTUREBLT);
                return sw.Elapsed.TotalMilliseconds;
            }, N);
            Native.SelectObject(mem, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, src);
        }

        // --- E) PrintWindow(PW_RENDERFULLCONTENT) 到記憶體 DC ---
        {
            IntPtr src = GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(src);
            var bmi = MakeBmi(size);
            IntPtr dib = Native.CreateDIBSection(src, ref bmi, 0, out _, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(mem, dib);
            IntPtr hwnd = GetDesktopWindow();
            Bench("E. PrintWindow(desktop)", () =>
            {
                var sw = Stopwatch.StartNew();
                PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT);
                return sw.Elapsed.TotalMilliseconds;
            }, N);
            Native.SelectObject(mem, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, src);
        }

        // --- F) GDI+ Graphics.CopyFromScreen ---
        using (var bmp = new System.Drawing.Bitmap(size, size,
                   System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using var g = System.Drawing.Graphics.FromImage(bmp);
            Bench("F. GDI+ CopyFromScreen", () =>
            {
                var sw = Stopwatch.StartNew();
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(size, size),
                                 System.Drawing.CopyPixelOperation.SourceCopy);
                return sw.Elapsed.TotalMilliseconds;
            }, N);
        }

        // --- G) DwmFlush 單獨計時（如果這條本身就是 ~16.7 ms，那就證明 16.7 ms 是合成器節奏，
        //        而不是 BitBlt 的資料搬運成本） ---
        Bench("G. DwmFlush (compositor wait only)", () =>
        {
            var sw = Stopwatch.StartNew();
            DwmFlush();
            return sw.Elapsed.TotalMilliseconds;
        }, N);

        // --- H) BitBlt 緊接在 DwmFlush 之後（若前面已經等過合成器，BitBlt 應該變便宜） ---
        using (var cap = new ScreenCapture(x, y, size))
        {
            Bench("H. DwmFlush then BitBlt", () =>
            {
                DwmFlush();
                var sw = Stopwatch.StartNew();
                cap.Grab(out _, out _);
                return sw.Elapsed.TotalMilliseconds;
            }, N);
        }

        // --- I) 硬體游標是否出現在擷取結果裡（分層視窗／游標的已知限制） ---
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            bool ok = GetCursorInfo(ref ci);
            Console.WriteLine($"  cursor info: available={ok} flags=0x{ci.flags:X} " +
                              $"pos=({ci.ptScreenPosX},{ci.ptScreenPosY}) hCursor=0x{ci.hCursor:X} " +
                              $"(flags&1 means CURSOR_SHOWING)");
        }
    }

    private static Native.BITMAPINFO MakeBmi(int size)
    {
        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = size;
        bmi.bmiHeader.biHeight = -size;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = Native.BI_RGB;
        bmi.bmiHeader.biSizeImage = (uint)(size * size * 4);
        return bmi;
    }

    private static void Bench(string label, Func<double> once, int n)
    {
        for (int i = 0; i < 3; i++) once();          // 暖機
        var t = new double[n];
        for (int i = 0; i < n; i++) t[i] = once();
        Console.WriteLine($"  {label,-34} avg {t.Average(),7:F3} ms   max {t.Max(),7:F3} ms   min {t.Min(),7:F3} ms");
    }
}
