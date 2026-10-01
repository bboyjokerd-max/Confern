using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace GlassSpike;

/// <summary>
/// 純 Win32 P/Invoke。刻意**不引用 src/Sidecar 的 Native.cs**：spike 必須與正式程式碼解耦，
/// 否則量到的東西會被正式專案的改動影響，而且會違反「不動 src/Sidecar」的硬限制。
/// </summary>
internal static class Native
{
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public const int SRCCOPY = 0x00CC0020;
    public const int BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;   // 只用 1 個 DWORD 的 padding 就夠（24/32bpp 不使用調色盤）
    }

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy,
                                                              IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
                                                 out IntPtr ppvBits, IntPtr hSection, uint offset);
}

/// <summary>
/// 螢幕擷取：一個長命的記憶體 DC + 32bpp top-down DIB section（= 一條可直接讀寫的 BGRA 緩衝）。
///
/// 為什麼用 CreateDIBSection 而不是 <c>Graphics.CopyFromScreen</c>：
///   - 正式輪盤要的是「BitBlt 進一塊能直接當貼圖用的記憶體」，DIB section 就是最短的那條路
///   - <c>CopyFromScreen</c> 內部還有額外的 GDI+ 包裝與一次拷貝，量它等於量錯東西
///   - 我們要能自己控制 stride 與 alpha（見下）
///
/// **alpha 陷阱（實測踩到）**：螢幕 DC 的像素是 32bpp 但沒有 alpha 通道的概念，
/// BitBlt 之後 DIB 裡的 alpha byte **不會被寫入**（內容未定義）。所以 Grab() 之後必須自己把 alpha 全部補成 255，
/// 否則後面任何 premultiplied 合成都會拿到垃圾 alpha。
/// </summary>
internal sealed class ScreenCapture : IDisposable
{
    private readonly int _size;
    private readonly int _x;
    private readonly int _y;

    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldObj;
    private IntPtr _bits;
    private int _stride;

    private readonly byte[] _raw;      // 未模糊的原始抓圖（BGRA, top-down）

    public ScreenCapture(int x, int y, int size)
    {
        _size = size;
        _x = x;
        _y = y;
        _raw = new byte[size * size * 4];
        _scratch = new byte[size * size * 4];

        _screenDc = Native.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC(NULL) failed");

        _memDc = Native.CreateCompatibleDC(_screenDc);
        if (_memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");

        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = size;
        bmi.bmiHeader.biHeight = -size;          // 負值 = top-down，row 0 就是最上面
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = Native.BI_RGB;
        bmi.bmiHeader.biSizeImage = (uint)(size * size * 4);

        _dib = Native.CreateDIBSection(_screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
        _oldObj = Native.SelectObject(_memDc, _dib);
        _stride = size * 4;   // 32bpp 且寬度 = size，stride 必定是 4 的倍數，不會有 padding
    }

    public int Size => _size;

    /// <summary>
    /// (1) 抓螢幕：GetDC(NULL) + BitBlt。**只做 BitBlt**，不重建任何資源。
    ///
    /// 回傳 BitBlt 自己的毫秒數（<paramref name="msFixup"/> 是 BitBlt 之後補 alpha
    /// 與留一份原始副本的時間）。為什麼要拆開量：實測 BitBlt 是整個管線最貴的一步，
    /// 若不拆開就會把「我自己寫的 alpha 補值迴圈」誤算成 BitBlt 的成本，
    /// 之後任何優化都會下錯藥。
    /// </summary>
    public unsafe void Grab(out double msBlit, out double msFixup)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (!Native.BitBlt(_memDc, 0, 0, _size, _size, _screenDc, _x, _y, Native.SRCCOPY))
            throw new InvalidOperationException(
                $"BitBlt failed (Win32 error {Marshal.GetLastWin32Error()}) at ({_x},{_y}) {_size}x{_size}");
        long t1 = Stopwatch.GetTimestamp();

        // 補 alpha（理由見類別註解）
        byte* p = (byte*)_bits;
        int n = _size * _size;
        for (int i = 0; i < n; i++) p[i * 4 + 3] = 255;

        // 留一份未模糊的原始資料（對照組要用**同一張**來源）
        var span = new ReadOnlySpan<byte>(p, _size * _size * 4);
        span.CopyTo(_raw);
        long t2 = Stopwatch.GetTimestamp();

        msBlit = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
        msFixup = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
    }

    /// <summary>方便呼叫端：不關心拆分時用這個。</summary>
    public unsafe void Grab() => Grab(out _, out _);

    /// <summary>診斷用：把記憶體 DC 的內容讀一遍（強制任何延後的 GDI 工作真的做完）。</summary>
    public unsafe long ReadBackChecksum()
    {
        byte* p = (byte*)_bits;
        long sum = 0;
        int n = _size * _size * 4;
        for (int i = 0; i < n; i++) sum += p[i];
        return sum;
    }

    /// <summary>
    /// 診斷用：這張抓圖的亮度變異數。數值越大代表畫面內容越豐富（有圖示、文字、桌布圖案），
    /// 拿來挑「有東西可以透過玻璃看見」的對照區域。
    /// </summary>
    public unsafe double LumaVariance()
    {
        byte* p = (byte*)_bits;
        int n = _size * _size;
        double sum = 0, sum2 = 0;
        for (int i = 0; i < n; i++)
        {
            double luma = 0.114 * p[i * 4 + 0] + 0.587 * p[i * 4 + 1] + 0.299 * p[i * 4 + 2];
            sum += luma; sum2 += luma * luma;
        }
        double mean = sum / n;
        return sum2 / n - mean * mean;
    }

    /// <summary>診斷用：記憶體 DC 是否已建立。</summary>
    public bool HasMemDc => _memDc != IntPtr.Zero;

    /// <summary>
    /// 診斷用：確認抓到的**真的是螢幕內容**而不是失敗後留下的空白。
    /// 回傳像素統計（平均亮度、相異色數、四個角落的顏色）。
    /// 若 BitBlt 悄悄失敗，這裡會看到整張同色 / 全黑。
    /// </summary>
    public unsafe string DescribeCapture()
    {
        byte* p = (byte*)_bits;
        long sum = 0;
        int minB = 255, maxB = 0;
        var distinct = new HashSet<int>();
        int n = _size * _size;
        for (int i = 0; i < n; i++)
        {
            int b = p[i * 4 + 0], g = p[i * 4 + 1], r = p[i * 4 + 2];
            sum += b + g + r;
            if (b < minB) minB = b;
            if (b > maxB) maxB = b;
            if (distinct.Count < 4096) distinct.Add((r << 16) | (g << 8) | b);
        }
        double avg = sum / (double)(n * 3);
        int c0 = (p[0] << 16) | (p[1] << 8) | p[2];
        int c1 = (p[(n - 1) * 4 + 2] << 16) | (p[(n - 1) * 4 + 1] << 8) | p[(n - 1) * 4 + 0];
        return $"capture stats: avg-luma={avg:F1}/255  blue-range=[{minB},{maxB}]  " +
               $"distinct-colors={distinct.Count}{(distinct.Count >= 4096 ? "+" : "")}  " +
               $"px(0,0)=#{c0:X6}  px(corner)=#{c1:X6}";
    }

    /// <summary>診斷用：對記憶體 DC 做一次極小的 GDI 操作，用來觀察 BitBlt 是否只是「延後執行」。</summary>
    public void MemDcTouch() { /* 目前不需要；保留給後續實驗 */ }

    /// <summary>目前這張抓圖的原生副本（BGRA，未模糊）。</summary>
    public byte[] SnapshotRaw() => (byte[])_raw.Clone();

    /// <summary>
    /// 把一張**合成桌布**寫進 DIB（只給 <c>--backdrop=dark|light</c> 用）。
    ///
    /// 為什麼要寫 DIB 而不是別的緩衝：<see cref="Blur(byte[],int,int)"/> 的來源是 DIB
    /// （<c>_bits</c>）不是 <c>_raw</c>。寫這裡才能讓「模糊 → 合成」這條路原封不動地跑，
    /// 量到的還是同一份程式碼，只是輸入換成一張可重複的桌布。
    /// 量測路徑（無 --backdrop）永遠不會呼叫這個方法。
    /// </summary>
    public unsafe void InjectBackdrop(byte[] bgra)
    {
        if (bgra.Length != _size * _size * 4)
            throw new ArgumentException($"backdrop must be {_size * _size * 4} bytes, got {bgra.Length}");
        var dst = new Span<byte>((byte*)_bits, _size * _size * 4);
        bgra.AsSpan().CopyTo(dst);
    }

    /// <summary>同上，但寫進呼叫者提供的緩衝（量測路徑用，避免每次配置）。</summary>
    public void SnapshotRawInto(byte[] dest)
    {
        if (dest.Length < _raw.Length)
            throw new ArgumentException($"dest too small: {dest.Length} < {_raw.Length}", nameof(dest));
        Buffer.BlockCopy(_raw, 0, dest, 0, _raw.Length);
    }

    // ------------------------------------------------------------------
    // (2) 可分離 box blur（自己實作，不用任何外部程式庫）
    //
    // 分離 = 先水平掃一遍、再垂直掃一遍，複雜度 O(w*h) 而不是 O(w*h*r)。
    // 做 N 次 box blur ≈ 一次高斯模糊（中央極限定理）；3 次、半徑 10 的視覺結果夠像毛玻璃。
    // 全程整數滑動視窗（每像素加一個新的、減一個舊的），沒有任何浮點除法在內圈。
    // ------------------------------------------------------------------
    /// <summary>模糊用的暫存塊（**建構時配置一次**，之後每次模糊重複使用；見 Blur 的說明）。</summary>
    private readonly byte[] _scratch;

    /// <summary>
    /// 執行 blur，結果寫進呼叫者提供的 <paramref name="dest"/>（長度需為 size*size*4）。
    ///
    /// 為什麼要傳緩衝而不是回傳新陣列：**正式輪盤常駐期間不該每幀配置 435 KB**。
    /// 第一版每次呼叫都 <c>new byte[435600]</c>，50 次就製造 21 MB 垃圾，
    /// 實測把行程工作集從 32.89 MB 推到 39.66 MB（+6.77 MB），直接讓記憶體預算失敗。
    /// 改成重複使用同一塊緩衝後，這筆成本就消失了。
    ///
    /// 另外：暫存塊必須容納**整個影像**（不是單一列），因為水平通道結束時是把整列
    /// <c>Buffer.BlockCopy</c> 回對應的 row offset，會寫到 row*stride 的位置。
    /// </summary>
    public unsafe void Blur(byte[] dest, int radius, int passes)
    {
        byte[] scratch = _scratch;
        var span = new ReadOnlySpan<byte>((byte*)_bits, _size * _size * 4);
        span.CopyTo(dest);

        for (int p = 0; p < passes; p++)
        {
            BoxBlurHorizontal(dest, scratch, radius);
            BoxBlurVertical(dest, scratch, radius);
        }
    }

    /// <summary>方便呼叫端（除錯用；會配置新陣列）。量測路徑請用 <see cref="Blur(byte[],int,int)"/>。</summary>
    public byte[] Blur(int radius, int passes)
    {
        var buf = new byte[_size * _size * 4];
        Blur(buf, radius, passes);
        return buf;
    }

    private void BoxBlurHorizontal(byte[] buf, byte[] scratch, int radius)
    {
        int n = _size;
        int win = 2 * radius + 1;
        for (int y = 0; y < n; y++)
        {
            int baseOff = y * _stride;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            // 視窗初始化：左邊界外重複邊緣像素（clamp），避免圖邊變暗
            for (int k = -radius; k <= radius; k++)
            {
                int xx = Math.Clamp(k, 0, n - 1) * 4 + baseOff;
                sumB += buf[xx]; sumG += buf[xx + 1]; sumR += buf[xx + 2]; sumA += buf[xx + 3];
            }

            for (int x = 0; x < n; x++)
            {
                int o = x * 4;
                scratch[o] = (byte)(sumB / win);
                scratch[o + 1] = (byte)(sumG / win);
                scratch[o + 2] = (byte)(sumR / win);
                scratch[o + 3] = (byte)(sumA / win);

                int add = Math.Clamp(x + radius + 1, 0, n - 1) * 4 + baseOff;
                int sub = Math.Clamp(x - radius, 0, n - 1) * 4 + baseOff;
                sumB += buf[add] - buf[sub];
                sumG += buf[add + 1] - buf[sub + 1];
                sumR += buf[add + 2] - buf[sub + 2];
                sumA += buf[add + 3] - buf[sub + 3];
            }
            Buffer.BlockCopy(scratch, 0, buf, baseOff, _stride);
        }
    }

    private void BoxBlurVertical(byte[] buf, byte[] scratch, int radius)
    {
        int n = _size;
        int win = 2 * radius + 1;
        for (int x = 0; x < n; x++)
        {
            int colOff = x * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int yy = Math.Clamp(k, 0, n - 1) * _stride + colOff;
                sumB += buf[yy]; sumG += buf[yy + 1]; sumR += buf[yy + 2]; sumA += buf[yy + 3];
            }

            for (int y = 0; y < n; y++)
            {
                int o = y * _stride + colOff;
                scratch[o] = (byte)(sumB / win);
                scratch[o + 1] = (byte)(sumG / win);
                scratch[o + 2] = (byte)(sumR / win);
                scratch[o + 3] = (byte)(sumA / win);

                int add = Math.Clamp(y + radius + 1, 0, n - 1) * _stride + colOff;
                int sub = Math.Clamp(y - radius, 0, n - 1) * _stride + colOff;
                sumB += buf[add] - buf[sub];
                sumG += buf[add + 1] - buf[sub + 1];
                sumR += buf[add + 2] - buf[sub + 2];
                sumA += buf[add + 3] - buf[sub + 3];
            }
        }
        Buffer.BlockCopy(scratch, 0, buf, 0, buf.Length);
    }

    public void Dispose()
    {
        if (_oldObj != IntPtr.Zero && _memDc != IntPtr.Zero) Native.SelectObject(_memDc, _oldObj);
        if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) Native.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, _screenDc);
        _dib = _memDc = _screenDc = _oldObj = IntPtr.Zero;
    }
}
