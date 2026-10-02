using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// 進度顯示（藥丸／展開後的失敗清單）用的**容器**介面（待辦 5 · D1）。
///
/// 為什麼要有這個介面：這一版把藥丸從共用的視覺層搬到自己的一個可點視窗上，而
/// <see cref="ProgressPresenter"/> 不該因此知道「容器是視覺層還是自己的視窗」——
/// 它只用到六個成員（畫、藏、可見、frame 數、啟動週期回呼、TickAlive），那就只給它這六個。
///
/// <see cref="VisualLayer"/> **原封不動**（輪盤依賴它，而且它的穿透／z-order 契約是凍結的），
/// 只是多實作這個介面；<see cref="PillWindow"/> 則是一個新的實作。
/// 兩個實作都走同一條 <c>UpdateLayeredWindow</c> 繪製路徑，所以外觀不可能分歧。
/// </summary>
internal interface ILayeredSurface : IDisposable
{
    /// <summary>把 <paramref name="bitmap"/> 以左上角 (x,y) 貼到螢幕上；**尺寸即視窗尺寸**。</summary>
    void Show(int x, int y, Bitmap bitmap);

    void Hide();

    /// <summary>
    /// **實作必須問作業系統**（<c>IsWindowVisible</c>），不得回傳自己記錄的旗標。
    /// 收層斷言、淡出斷言、<see cref="ReleaseGuard"/> 的決定都讀這一個屬性；自記旗標
    /// 等於讓「覆蓋層卡在使用者畫面上」這件事在最需要被看見的地方說謊（2026-10-02 P0-2）。
    /// </summary>
    bool IsVisible { get; }

    /// <summary>已經畫過幾個 frame（診斷用：證明是原地更新而不是一直開新視窗）。</summary>
    int Frames { get; }

    /// <summary>啟動（或停止，<paramref name="intervalMs"/> = 0）週期回呼。</summary>
    void StartTick(int intervalMs);

    /// <summary>週期回呼。由實作決定怎麼產生（兩者都是執行緒計時器 ＋ 張貼訊息）。</summary>
    event Action? TickRequested;

    /// <summary>診斷用：週期回呼的計時器是否還活著。</summary>
    bool TickAlive { get; }
}

/// <summary>
/// 藥丸與失敗清單的**專屬可點視窗**（待辦 5 · D1 定案：方案 A）。
///
/// 為什麼需要它：規格 §7.3 的「清單可點擊展開」需要那一下點擊**進得來**。共用的
/// <see cref="VisualLayer"/> 必須有 <c>WS_EX_TRANSPARENT</c>（拖放要穿透它落到命中層），
/// 而那個位元的意思正是「滑鼠事件穿過去」——所以藥丸在視覺層上**永遠點不到**。
/// 這個類別把容器換掉，繪製程式碼一行都沒動。
///
/// 與 <see cref="VisualLayer"/> 的差別只有三件事（其餘刻意相同）：
///   1. **沒有** <c>WS_EX_TRANSPARENT</c>（建立後會實際檢查它沒被設起來）；
///   2. 多收三則滑鼠訊息：<c>WM_LBUTTONUP</c> → 回呼、<c>WM_MOUSEACTIVATE</c> → <c>MA_NOACTIVATE</c>、
///      <c>WM_MOUSEMOVE</c> ＋ <c>TrackMouseEvent(TME_LEAVE)</c> → 進入／離開（D2 唯一的實作方式）；
///   3. 它**會吃掉**落在那塊矩形上的點擊（＝不會順便點到桌面上的圖示）。
///
/// 幾何：**仍然只有一份**。視窗的位置與大小就等於呼叫端給的那張點陣圖的尺寸，
/// 這個類別不自己算任何幾何，也不知道藥丸與面板的關係。
///
/// 執行緒契約：與 <see cref="VisualLayer"/> 一樣，視窗屬於建立它的那條執行緒，
/// 而它本來就在主（訊息迴圈）執行緒上建立。
/// </summary>
internal sealed class PillWindow : ILayeredSurface
{
    /// <summary>視窗類別名（規格指定）。產品名稱的唯一擁有者是 <see cref="ProductInfo"/>。</summary>
    public const string ClassName = ProductInfo.Name + "PillWindow";

    private static readonly Dictionary<IntPtr, PillWindow> Instances = new();
    private static Native.WndProcDelegate? _proc;      // 保留委派，避免被 GC 回收（同 HitLayer／VisualLayer／TrayIcon）

    private readonly Evidence _evidence;
    private readonly IntPtr _hwnd;
    private System.Threading.Timer? _tickTimer;
    private int _frames;
    private bool _trackingLeave;      // TrackMouseEvent 是「一次性」的，離開之後必須重新要求
    private bool _hover;
    private bool _disposed;

    /// <summary>規格 §7.3：藥丸（或面板）被點一下。真實點擊與自測走的是同一條路徑。</summary>
    public event Action? Clicked;

    /// <summary>游標進入／離開這個視窗。</summary>
    public event Action<bool>? HoverChanged;

    public event Action? TickRequested;

    public IntPtr Handle => _hwnd;

    /// <summary>
    /// 與 <see cref="VisualLayer.IsVisible"/> 同一條契約（2026-10-02 可證偽性稽核 P0-2）：
    /// 「畫面上看不看得見」問作業系統，不回傳自己記錄的 bool。
    ///
    /// 藥丸比輪盤更需要這一條：它的失效模式是「一個永遠不淡出的藥丸 / 一塊卡住的錯誤清單」，
    /// 而舊版的自記旗標會讓 m1h 那一整族淡出斷言在視窗其實還在的時候照樣綠。
    /// 反過來說，`Show` 途中丟例外時舊版會留下一個「旗標說沒開、視窗其實開著」的覆蓋層。
    /// </summary>
    public bool IsVisible => Native.IsWindowVisible(_hwnd);

    public int Frames => _frames;
    public bool TickAlive => _tickTimer != null;

    /// <summary>游標現在是否停在這個視窗上（D2 的狀態）。</summary>
    public bool IsHovered => _hover;

    /// <summary>已處理過幾次點擊（診斷用：證明點擊真的進到 WndProc，不是推論）。</summary>
    public int Clicks { get; private set; }

    /// <summary>
    /// 建立後實際讀回來的擴充樣式。自測與驗收可以斷言「該有的有、不該有的沒有」——
    /// 而不是相信建立時傳進去的那個數字。
    /// </summary>
    public int ExStyle { get; }

    public PillWindow(Evidence evidence)
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

        // 頂層 WS_POPUP。WS_EX_TOPMOST 讓它蓋在命中層（也是 topmost）之上看得見。
        // **刻意沒有 WS_EX_TRANSPARENT**：加了就等於回到「點不到」的狀態。
        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            ClassName, ClassName, Native.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"pill window creation failed: {Marshal.GetLastWin32Error()}");

        Instances[_hwnd] = this;

        // 建立時就斷言樣式，與 VisualLayer 同樣的理由：這兩種失效都是靜默的
        //（少了 WS_EX_NOACTIVATE 會搶焦點；多了 WS_EX_TRANSPARENT 會讓點擊永遠進不來）。
        ExStyle = Native.GetWindowLongW(_hwnd, Native.GwlExStyle);
        bool noActivate = (ExStyle & Native.WS_EX_NOACTIVATE) != 0;
        bool layered = (ExStyle & Native.WS_EX_LAYERED) != 0;
        bool clickThrough = (ExStyle & Native.WS_EX_TRANSPARENT) != 0;
        _evidence.Line($"pill window created hwnd=0x{_hwnd.ToInt64():X} class={ClassName} exstyle=0x{ExStyle:X} " +
                       $"WS_EX_LAYERED={(layered ? "set(required)" : "MISSING(FATAL)")} " +
                       $"WS_EX_NOACTIVATE={(noActivate ? "set(required)" : "MISSING(FATAL)")} " +
                       $"WS_EX_TRANSPARENT={(clickThrough ? "SET(FATAL: clicks would pass through)" : "clear(required)")}");
        if (!layered)
            throw new InvalidOperationException("pill window is missing WS_EX_LAYERED");
        if (!noActivate)
            throw new InvalidOperationException("pill window is missing WS_EX_NOACTIVATE — it could steal focus");
        if (clickThrough)
            throw new InvalidOperationException(
                "pill window has WS_EX_TRANSPARENT — the click would go through it, which is the whole bug D1 fixes");
    }

    /// <summary>
    /// 把 <paramref name="bitmap"/> 以左上角 (x,y) 貼到螢幕上；**尺寸即視窗尺寸**。
    ///
    /// 這一段與 <see cref="VisualLayer.Show"/> 是同一條 <c>UpdateLayeredWindow</c> 路徑
    /// （同樣的 DIB、同樣的逐列複製、同樣的 per-pixel alpha）。刻意重複而不是共用一個
    /// helper：VisualLayer 的契約是凍結的，而重構它會讓這一版多出一個與功能無關的風險面。
    /// </summary>
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
                    throw new InvalidOperationException($"pill window: CreateDIBSection failed: {Marshal.GetLastWin32Error()}");

                int rowBytes = bitmap.Width * 4;
                if (bd.Stride != rowBytes)
                    _evidence.Line($"pill window: stride={bd.Stride} != rowBytes={rowBytes}; copying row by row");
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
                throw new InvalidOperationException($"pill window: UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) Native.SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        _frames++;
    }

    public void Hide()
    {
        // OS 狀態而不是自記旗標（同 VisualLayer.Hide 的理由）。
        if (!IsVisible) return;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        // 藏起來之後「游標停在上面」不再是事實。留著它會讓下一次顯示帶著一個過期的 hover——
        // 而 hover 決定「要不要淡出」，過期的 hover 就是一個永遠不淡出的藥丸。
        SetHover(false, "hidden");
    }

    /// <summary>與 <see cref="VisualLayer"/> 同一條路徑：執行緒計時器 ＋ 張貼訊息。</summary>
    public void StartTick(int intervalMs)
    {
        if (intervalMs <= 0)
        {
            _tickTimer?.Dispose();
            _tickTimer = null;
            return;
        }
        _tickTimer?.Dispose();
        IntPtr hwnd = _hwnd;
        _tickTimer = new System.Threading.Timer(
            _ => Native.PostMessageW(hwnd, Native.WM_APP_TICK, IntPtr.Zero, IntPtr.Zero),
            null, intervalMs, intervalMs);
    }

    /// <summary>
    /// 進入／離開的狀態轉移**只有這一處**：進入與離開都會寫一行日誌（`… hover=True/False …`），
    /// 因為「游標停在藥丸上卻淡出了」與「游標根本沒到過藥丸」在使用者眼裡一模一樣。
    /// </summary>
    private void SetHover(bool hover, string why)
    {
        if (_hover == hover) return;
        _hover = hover;
        _evidence.Line($"pill window: hover={hover} ({why}) hwnd=0x{_hwnd.ToInt64():X}");
        HoverChanged?.Invoke(hover);
    }

    /// <summary>
    /// 要求系統在游標離開時通知我們。**每次進入都要重新要求一次**（這是 one-shot API），
    /// 漏掉重設的症狀是「第一次離開之後就再也收不到離開事件」——藥丸會卡在畫面上不淡出。
    /// </summary>
    private void RequestLeaveTracking()
    {
        if (_trackingLeave) return;
        var tme = new Native.TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
            dwFlags = Native.TME_LEAVE,
            hwndTrack = _hwnd,
            dwHoverTime = 0
        };
        _trackingLeave = Native.TrackMouseEvent(ref tme);
        if (!_trackingLeave)
            _evidence.Line($"pill window: TrackMouseEvent(TME_LEAVE) failed (lastError={Marshal.GetLastWin32Error()}) " +
                           "— the cursor leaving the pill will not be reported, so it may not fade out");
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hWnd, out PillWindow? pill) && pill._hwnd == hWnd)
        {
            switch (msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    // 不搶焦點（規格 §7.3 的實作前提）。
                    //
                    // ⚠ 誠實的現狀：**這一行在可量測的範圍內是走不到的**。視窗帶著
                    //   `WS_EX_NOACTIVATE`，而建構子在那個樣式缺席時直接丟例外，所以系統從不
                    //   為了這個視窗送 `WM_MOUSEACTIVATE` 過來。實測過的兩次手術：
                    //     只把這一行拿掉（其餘不動）→ 62/62 全綠（沒有紅）；
                    //     只把 `WS_EX_NOACTIVATE` 拿掉（這一行留著）→ 行程在建立視窗時就死掉，
                    //     什麼都量不到。
                    //   留著它是因為它把「不搶焦點」講成訊息層的意圖，代價是一行；
                    //   但**不要**把它當成被驗證過的防線——沒有任何一條檢查證明過它。
                    return new IntPtr(Native.MA_NOACTIVATE);

                case Native.WM_MOUSEMOVE:
                    // 游標在視窗上＝進入；同時（重新）要求離開通知。
                    pill._trackingLeave = false;
                    pill.SetHover(true, "WM_MOUSEMOVE");
                    pill.RequestLeaveTracking();
                    return IntPtr.Zero;

                case Native.WM_MOUSELEAVE:
                    pill.SetHover(false, "WM_MOUSELEAVE");
                    pill._trackingLeave = false;
                    return IntPtr.Zero;

                case Native.WM_LBUTTONDOWN:
                    // 捕捉滑鼠：按下之後手指飄出藥丸才放開，放開事件仍然回到這裡。
                    Native.SetCapture(hWnd);
                    return IntPtr.Zero;

                case Native.WM_LBUTTONUP:
                    Native.ReleaseCapture();
                    int upX = SignedLoWord(lParam), upY = SignedHiWord(lParam);
                    // lParam 是**工作區座標**（見 ClientRect）。用工作區矩形判定，
                    // 不要拿它去比螢幕矩形——那兩組座標只會在原點附近偶然重合。
                    if (!pill.HitTestClientPoint(upX, upY))
                    {
                        pill._evidence.Line($"pill window: WM_LBUTTONUP at client ({upX},{upY}) is outside " +
                                            $"the client area {pill.ClientRectText()} -> ignored");
                        return IntPtr.Zero;
                    }
                    pill.Clicks++;
                    pill._evidence.Line($"pill window: WM_LBUTTONUP at client ({upX},{upY}) clicks={pill.Clicks}");
                    pill.Clicked?.Invoke();
                    return IntPtr.Zero;

                case Native.WM_APP_TICK:
                    pill.TickRequested?.Invoke();
                    return IntPtr.Zero;

                case Native.WM_APP_FORCE_HIDE:
                    // 與視覺層同一條契約：**決定權不在這裡**（guard 決定，這裡只執行）。
                    Native.ShowWindow(hWnd, Native.SW_HIDE);
                    pill.SetHover(false, "force hide");
                    pill._evidence.Line($"pill window HIDDEN ({Native.HideReasonText(wParam.ToInt32())})");
                    return IntPtr.Zero;
            }
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>滑鼠訊息的 lParam 是兩個 signed 16-bit：不轉型的話負座標（多螢幕左側）會變成 6 萬多。</summary>
    internal static int SignedLoWord(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    internal static int SignedHiWord(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    /// <summary>
    /// 這個視窗的**工作區矩形**（左上角為 0,0、大小與視窗的螢幕矩形相同）。
    ///
    /// ⚠ 這是命中測試用的座標系，而它與視窗的螢幕矩形**不是同一個**：
    /// 滑鼠訊息的 lParam 帶的是**工作區座標**（相對於視窗左上角），不是螢幕座標。
    /// 第一版拿 lParam 去比 <c>GetWindowRect</c>，於是每個點擊都被判成「在視窗外面」——
    /// 而自測之所以還是綠的，只因為那個視窗剛好落在螢幕原點附近（兩組座標偶然接近）。
    /// 座標系混用是這一類功能的典型錯誤：它在大部分的螢幕位置上都會壞掉，
    /// 而且是**靜默**地壞掉（點擊被丟掉，沒有任何錯誤）。
    /// </summary>
    public Rectangle ClientRect
    {
        get
        {
            if (!Native.GetWindowRect(_hwnd, out Native.RECT r)) return Rectangle.Empty;
            return new Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    /// <summary>
    /// 滑鼠訊息的座標是否落在這個視窗上。**參數是工作區座標**（見 <see cref="ClientRect"/>），
    /// 不是螢幕座標。
    ///
    /// 為什麼要做這一件事，既然使用者的滑鼠本來就點不到外面：訊息是可以被**送進來的**
    /// （自測、診斷、別的行程）。沒有這一行的話，「點在藥丸外面不會展開」這條規格
    /// 就只對真實滑鼠成立——一個送錯座標的訊息照樣會展開清單。
    /// </summary>
    public bool HitTestClientPoint(int clientX, int clientY) =>
        ClientRect.Contains(clientX, clientY);

    /// <summary>螢幕座標是否落在這個視窗上（給外部工具與截圖用，與上面的工作區版本不同）。</summary>
    public bool HitTest(int screenX, int screenY)
    {
        if (!Native.GetWindowRect(_hwnd, out Native.RECT r)) return false;
        return screenX >= r.Left && screenX < r.Right && screenY >= r.Top && screenY < r.Bottom;
    }


    private string ClientRectText()
    {
        Rectangle r = ClientRect;
        return r.IsEmpty ? "(unknown)" : $"{r.Width}x{r.Height}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tickTimer?.Dispose();
        _tickTimer = null;
        Instances.Remove(_hwnd);
        Native.DestroyWindow(_hwnd);
        _evidence.Line($"pill window disposed; frames={_frames} clicks={Clicks}");
    }
}
