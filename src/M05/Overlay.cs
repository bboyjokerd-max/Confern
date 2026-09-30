using System.Runtime.InteropServices;
using System.Text;

namespace M05;

/// <summary>
/// 全螢幕接收層。單一職責：視窗生命週期 + 拖放接收。
/// 只暴露 Show / Hide / Handle，不含任何狀態機邏輯（那是 DragDetector 的事）。
/// </summary>
internal sealed class Overlay : IDisposable
{
    private const string ClassName = "M05OverlayWindow";

    private static Overlay? _self;                   // 供靜態 WndProc 轉發
    private static Native.WndProcDelegate? _proc;    // 保留委派，避免被 GC 回收

    private IntPtr _hwnd = IntPtr.Zero;
    private readonly Evidence _evidence;
    private readonly byte _alpha;
    private int _showCount;

    public event Action<string[]>? FilesDropped;

    /// <param name="alpha">
    /// 1 = 生產值（1/255，肉眼不可見但仍可被拖放命中）。
    /// 較大值 = 診斷用「看得見的模式」，讓人工測試能當場確認接收層是否被叫起來。
    /// 兩者都 ≥ 1，因此對命中行為是等價的。
    /// </param>
    public Overlay(Evidence evidence, byte alpha = Native.OverlayAlpha)
    {
        _evidence = evidence;
        _alpha = alpha;
    }

    public bool IsVisible { get; private set; }
    public IntPtr Handle => _hwnd;

    public void EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero) return;

        _self = this;
        _proc = StaticWndProc;
        IntPtr hInst = Native.GetModuleHandleW(null);
        _evidence.Line($"step: hInst=0x{hInst.ToInt64():X} structSize={Marshal.SizeOf<Native.WNDCLASSEX>()}");

        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst,
            // COLOR_HIGHLIGHT 系統筆刷：診斷模式下才看得見那片覆蓋層
            hbrBackground = new IntPtr(13),
            lpszClassName = ClassName
        };
        ushort atom = Native.RegisterClassExW(ref wc);
        int regErr = Marshal.GetLastWin32Error();
        _evidence.Line($"step: RegisterClassExW atom={atom} lastError={regErr}");

        int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            ClassName, "M05 overlay", Native.WS_POPUP,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            _evidence.Line($"FATAL: CreateWindowExW failed lastError={err}");
            throw new InvalidOperationException("CreateWindowExW failed: " + err);
        }

        Native.DragAcceptFiles(_hwnd, true);
        Native.SetLayeredWindowAttributes(_hwnd, 0, _alpha, Native.LWA_ALPHA);

        long ex = Native.GetWindowLongW(_hwnd, Native.GwlExStyle);
        bool transparentBitSet = (ex & Native.WsExTransparentBit) != 0;
        _evidence.Line($"overlay created hwnd=0x{_hwnd.ToInt64():X} exstyle=0x{ex:X} " +
                       $"WS_EX_TRANSPARENT={(transparentBitSet ? "SET(FATAL)" : "not set(OK)")} " +
                       $"rect=({x},{y},{w}x{h}) alpha={_alpha}");

        if (transparentBitSet)
            _evidence.Line("FATAL: WS_EX_TRANSPARENT is set — drop hit-test WILL be skipped.");
    }

    public void Show()
    {
        EnsureCreated();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        IsVisible = true;
        _showCount++;
        _evidence.Line($"overlay SHOWN (count={_showCount})");
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        if (IsVisible) _evidence.Line("overlay HIDDEN");
        IsVisible = false;
    }

    /// <summary>由任何執行緒要求收起（走視窗訊息，避免跨執行緒呼叫 ShowWindow）。</summary>
    public void RequestHide()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.PostMessageW(_hwnd, Native.WM_APP_FORCE_HIDE, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _self;
        if (self is not null)
        {
            if (msg == Native.WM_DROPFILES)
            {
                self.OnDropFiles(wParam);
                return IntPtr.Zero;
            }
            if (msg == Native.WM_APP_FORCE_HIDE)
            {
                self.Hide();
                return IntPtr.Zero;
            }
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnDropFiles(IntPtr hDrop)
    {
        try
        {
            uint count = Native.DragQueryFileW(hDrop, Native.DRAGQUERY_COUNT, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint len = Native.DragQueryFileW(hDrop, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                Native.DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
                paths.Add(sb.ToString());
            }
            _evidence.Line($"*** DROP RECEIVED: {count} file(s) ***");
            foreach (var p in paths) _evidence.Line($"    {p}");
            FilesDropped?.Invoke(paths.ToArray());
        }
        catch (Exception ex)
        {
            _evidence.Line("drop handler error: " + ex.Message);
        }
        finally
        {
            Native.DragFinish(hDrop);
            Hide();   // 硬保險：drop 已解析完成，立即收層
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _self = null;
    }
}
