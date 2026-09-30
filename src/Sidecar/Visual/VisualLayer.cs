using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// 視覺層：per-pixel alpha 的唯一視窗（M1b）。
///
/// 契約（與命中層相反，兩者都不可交換）：
///   - **必須**有 <c>WS_EX_TRANSPARENT</c>：拖放才會穿透它、落到下面的命中層
///   - **必須**有 <c>WS_EX_NOACTIVATE</c>：絕不搶焦點（否則 <c>IsExplorerForeground</c> 判定會壞掉）
///   - 永遠 topmost，因為它要在命中層（也是 topmost）之上才看得見
///
/// 本類別**不含任何輪盤知識**：它只負責「把一張 ARGB 點陣圖貼到某個螢幕座標」，
/// 幾何與外觀屬 <c>WheelPresenter</c>（唯一繪製者）。
/// </summary>
internal sealed class VisualLayer : IDisposable
{
    private const string ClassName = "SidecarVisualLayer";
    private static Native.WndProcDelegate? _proc;   // 保留委派，避免被 GC 回收（同 HitLayer）

    private readonly Evidence _evidence;
    private readonly IntPtr _hwnd;

    /// <summary>hwnd → 實例（靜態 WndProc 分派用）。</summary>
    private static readonly Dictionary<IntPtr, VisualLayer> Instances = new();
    /// <summary>週期性回呼（進度顯示的淡出與重畫用）。只有需要時才啟動計時器。</summary>
    public event Action? TickRequested;
    private const int TickTimerId = 1;
    private bool _tickRunning;
    private bool _visible;
    private int _frames;

    public IntPtr Handle => _hwnd;
    public bool IsVisible => _visible;
    public int Frames => _frames;

    public VisualLayer(Evidence evidence)
    {
        _evidence = evidence;

        _proc ??= StaticWndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = ClassName
        };
        Native.RegisterClassExW(ref wc);

        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
            Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            ClassName, "SidecarVisualLayer", Native.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"visual layer creation failed: {Marshal.GetLastWin32Error()}");

        Instances[_hwnd] = this;      // 靜態 WndProc 需要由 hwnd 找回實例

        // 建立時就斷言兩個必要位元，避免「畫得出來但拖放全死」這種最糟的失敗模式
        int ex = Native.GetWindowLongW(_hwnd, Native.GwlExStyle);
        bool transparent = (ex & Native.WS_EX_TRANSPARENT) != 0;
        bool noActivate = (ex & Native.WS_EX_NOACTIVATE) != 0;
        _evidence.Line($"visual layer created hwnd=0x{_hwnd.ToInt64():X} exstyle=0x{ex:X} " +
                       $"WS_EX_TRANSPARENT={(transparent ? "set(required)" : "MISSING(FATAL)")} " +
                       $"WS_EX_NOACTIVATE={(noActivate ? "set(required)" : "MISSING(FATAL)")}");
        if (!transparent)
            throw new InvalidOperationException(
                "visual layer is missing WS_EX_TRANSPARENT — drops would land on the visual layer instead of the hit layer");
        if (!noActivate)
            throw new InvalidOperationException("visual layer is missing WS_EX_NOACTIVATE — it could steal focus");
    }

    /// <summary>把 <paramref name="bitmap"/> 以左上角 (x,y) 貼到螢幕上；尺寸即視窗尺寸。</summary>
    public void Show(int x, int y, Bitmap bitmap)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            BitmapData bd = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            IntPtr bits;
            try
            {
                var bmi = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = bitmap.Width,
                    biHeight = -bitmap.Height,     // 負值＝由上而下，與 GDI+ 的資料列順序一致
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Native.BI_RGB
                };
                hBitmap = Native.CreateDIBSection(memDc, ref bmi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero)
                    throw new InvalidOperationException($"CreateDIBSection failed: {Marshal.GetLastWin32Error()}");

                // GDI+ 的 Stride 可能含 padding，逐列複製。
                // 注意：Marshal.Copy 沒有 IntPtr→IntPtr 的多載，必須經由中繼緩衝區。
                int rowBytes = bitmap.Width * 4;
                if (bd.Stride != rowBytes)
                    _evidence.Line($"visual layer: stride={bd.Stride} != rowBytes={rowBytes}; copying row by row");
                var rowBuffer = new byte[rowBytes];
                for (int row = 0; row < bitmap.Height; row++)
                {
                    Marshal.Copy(bd.Scan0 + row * bd.Stride, rowBuffer, 0, rowBytes);
                    Marshal.Copy(rowBuffer, 0, bits + row * rowBytes, rowBytes);
                }
            }
            finally { bitmap.UnlockBits(bd); }

            oldBitmap = Native.SelectObject(memDc, hBitmap);

            var dst = new Native.POINT { X = x, Y = y };
            var size = new Native.SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var src = new Native.POINT { X = 0, Y = 0 };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA
            };

            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, bitmap.Width, bitmap.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            if (!Native.UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA))
                throw new InvalidOperationException($"UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) Native.SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        _visible = true;
        _frames++;
    }

    public void Hide()
    {
        if (!_visible) return;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        _visible = false;
    }

    /// <summary>
    /// 由 <see cref="ReleaseGuard"/> 要求收層。與命中層一樣走視窗訊息，讓實際的 ShowWindow
    /// 發生在 UI 執行緒（guard 的定時器是在執行緒池執行緒上觸發的）。
    /// </summary>
    public void RequestHide(int reason) =>
        Native.PostMessageW(_hwnd, Native.WM_APP_FORCE_HIDE, new IntPtr(reason), IntPtr.Zero);

    /// <summary>
    /// 視覺層只處理「收層」這一則自訂訊息，其餘一律交回系統。
    /// **收層的決定權不在這裡**：guard 決定，這裡只執行。
    /// </summary>
    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_APP_FORCE_HIDE && Instances.TryGetValue(hWnd, out VisualLayer? layer))
        {
            Native.ShowWindow(hWnd, Native.SW_HIDE);
            layer._visible = false;
            layer._evidence.Line($"visual layer HIDDEN ({Native.HideReasonText(wParam.ToInt32())})");
            return IntPtr.Zero;
        }
        if (msg == Native.WM_TIMER && wParam.ToInt64() == TickTimerId &&
            Instances.TryGetValue(hWnd, out VisualLayer? ticking))
        {
            ticking.TickRequested?.Invoke();
            return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 啟動（或停止）週期性回呼。<paramref name="intervalMs"/> = 0 表示停止。
    /// 這是進度顯示唯一的驅動來源——它讓 UI 不必為了「有沒有進展」而一直輪詢。
    /// </summary>
    public void StartTick(int intervalMs)
    {
        if (intervalMs <= 0)
        {
            if (_tickRunning)
            {
                Native.KillTimer(_hwnd, new IntPtr(TickTimerId));
                _tickRunning = false;
            }
            return;
        }
        Native.KillTimer(_hwnd, new IntPtr(TickTimerId));
        Native.SetTimer(_hwnd, new IntPtr(TickTimerId), (uint)intervalMs, IntPtr.Zero);
        _tickRunning = true;
    }

    public void Dispose()
    {
        Instances.Remove(_hwnd);
        Native.DestroyWindow(_hwnd);
        _evidence.Line($"visual layer disposed; frames={_frames}");
    }
}
