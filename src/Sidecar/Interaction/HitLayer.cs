using System.Runtime.InteropServices;
using System.Text;

namespace Sidecar;

/// <summary>
/// 命中層（規格 §4.1、§3.1）。**每個螢幕一個視窗**，啟動時預先建立並保持隱藏。
/// 單一職責：視窗生命週期 + 拖放接收。
///
/// 兩條產品契約（與 M05 原型的差別）：
/// 1. **本類別永不自行收層**——收層一律由 <see cref="ReleaseGuard"/> 決定。
///    M05 在 drop 之後自己呼叫 Hide()，那會讓保險出現第二個出入口。
/// 2. **不得對被拖曳的檔案做任何檔案操作**（M1a 不變式）：這裡只讀取路徑並往上報。
/// </summary>
internal sealed class HitLayer : IDisposable
{
    private const string ClassName = "SidecarHitLayer";

    /// hwnd → 所屬實例與螢幕索引（靜態 WndProc 需要分派到正確的視窗）
    private static readonly Dictionary<IntPtr, (HitLayer Owner, int Index)> Windows = new();
    private static Native.WndProcDelegate? _proc;     // 保留委派，避免被 GC 回收

    private readonly Evidence _evidence;
    private readonly MonitorEntry[] _monitors;
    private readonly byte _alpha;
    private IntPtr[] _handles = Array.Empty<IntPtr>();
    private int _showCount;

    public event Action<int, string[]>? FilesDropped;

    public HitLayer(Evidence evidence, MonitorEntry[] monitors, byte alpha = Native.OverlayAlpha)
    {
        _evidence = evidence;
        _monitors = monitors;
        _alpha = alpha;
    }

    /// <summary>以**作業系統的真實狀態**判斷，而非自記旗標——收層斷言必須問系統。</summary>
    public bool IsVisible
    {
        get
        {
            foreach (IntPtr h in _handles)
                if (h != IntPtr.Zero && Native.IsWindowVisible(h)) return true;
            return false;
        }
    }

    public IntPtr HandleOf(int index) =>
        index >= 0 && index < _handles.Length ? _handles[index] : IntPtr.Zero;

    /// <summary>預先建立所有螢幕的視窗並保持隱藏。隱藏視窗不被 WindowFromPoint 命中，因此不影響零侵入。</summary>
    public void EnsureCreated()
    {
        if (_handles.Length > 0) return;

        _proc = StaticWndProc;
        IntPtr hInst = Native.GetModuleHandleW(null);

        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst,
            hbrBackground = IntPtr.Zero,     // 不繪製任何東西
            lpszClassName = ClassName
        };
        ushort atom = Native.RegisterClassExW(ref wc);
        int regErr = Marshal.GetLastWin32Error();
        // ERROR_CLASS_ALREADY_EXISTS (1410) 代表別的實例已註冊過，可接受。
        _evidence.Line($"hit class registered atom={atom} lastError={regErr}");

        var handles = new IntPtr[_monitors.Length];
        foreach (MonitorEntry m in _monitors)
        {
            IntPtr hwnd = Native.CreateWindowExW(
                Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                ClassName, $"Sidecar hit #{m.Index}", Native.WS_POPUP,
                m.Bounds.Left, m.Bounds.Top, m.Width, m.Height,
                IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CreateWindowExW failed for monitor#{m.Index}: {err}");
            }

            Native.DragAcceptFiles(hwnd, true);
            Native.SetLayeredWindowAttributes(hwnd, 0, _alpha, Native.LWA_ALPHA);
            Native.ShowWindow(hwnd, Native.SW_HIDE);      // 預先建立但立即隱藏

            long ex = Native.GetWindowLongW(hwnd, Native.GwlExStyle);
            bool transparentBitSet = (ex & Native.WsExTransparentBit) != 0;

            _evidence.Line($"hit#{m.Index} hwnd=0x{hwnd.ToInt64():X} exstyle=0x{ex:X} " +
                           $"WS_EX_TRANSPARENT={(transparentBitSet ? "SET(FATAL)" : "not set(OK)")} " +
                           $"rect=({m.Bounds.Left},{m.Bounds.Top},{m.Width}x{m.Height}) " +
                           $"alpha={_alpha} visible={Native.IsWindowVisible(hwnd)}");

            if (transparentBitSet)
                throw new InvalidOperationException(
                    $"hit layer #{m.Index} has WS_EX_TRANSPARENT — drop hit-test would be skipped; mechanism is dead");

            Windows[hwnd] = (this, m.Index);
            handles[m.Index] = hwnd;
        }

        _handles = handles;
        _evidence.Line($"hit layer ready: {_handles.Length} window(s), allHidden={!IsVisible} (must be True)");
    }

    public void ShowAll()
    {
        EnsureCreated();
        foreach (IntPtr h in _handles)
        {
            if (h == IntPtr.Zero) continue;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
        _showCount++;
        _evidence.Line($"hit layer SHOWN (count={_showCount}, windows={_handles.Length})");
    }

    public void HideAll()
    {
        bool wasVisible = IsVisible;
        foreach (IntPtr h in _handles)
            if (h != IntPtr.Zero) Native.ShowWindow(h, Native.SW_HIDE);
        if (wasVisible) _evidence.Line("hit layer HIDDEN");
    }

    /// <summary>
    /// 通知：接收層已實際隱藏（帶原因碼）。**這是通知，不是決定權**——決定權仍然只在
    /// <see cref="ReleaseGuard"/>。視覺層（輪盤）據此跟著收起，兩層才會在同一瞬間消失；
    /// 由 UI 執行緒的 WndProc 觸發，所以在處理常式裡操作視窗是安全的。
    /// </summary>
    public event Action<int>? Hidden;

    /// <summary>
    /// 由 <see cref="ReleaseGuard"/> 要求收層。走視窗訊息（不跨執行緒直接呼叫 ShowWindow），
    /// 原因碼夾帶在 wParam，實際的隱藏與記錄都在 UI 執行緒的 WndProc 裡發生。
    /// </summary>
    public void RequestHideAll(int reasonCode)
    {
        foreach (IntPtr h in _handles)
            if (h != IntPtr.Zero) Native.PostMessageW(h, Native.WM_APP_FORCE_HIDE, new IntPtr(reasonCode), IntPtr.Zero);
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(hWnd, out var entry))
        {
            if (msg == Native.WM_DROPFILES)
            {
                entry.Owner.OnDropFiles(entry.Index, wParam);
                return IntPtr.Zero;
            }
            if (msg == Native.WM_APP_FORCE_HIDE)
            {
                Native.ShowWindow(hWnd, Native.SW_HIDE);
                entry.Owner._evidence.Line($"hit layer HIDDEN ({Native.HideReasonText(wParam.ToInt32())})");
                entry.Owner.Hidden?.Invoke(wParam.ToInt32());
                return IntPtr.Zero;
            }
            if (msg == Native.WM_DESTROY)
            {
                Windows.Remove(hWnd);
                return IntPtr.Zero;
            }
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>只讀取檔案清單並往上報。**這裡不做任何檔案操作，也不收層。**</summary>
    private void OnDropFiles(int monitorIndex, IntPtr hDrop)
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

            _evidence.Line($"*** DROP RECEIVED on hit#{monitorIndex}: {count} file(s) ***");
            foreach (string p in paths) _evidence.Line($"    {p}");

            FilesDropped?.Invoke(monitorIndex, paths.ToArray());
        }
        catch (Exception ex)
        {
            _evidence.Line("drop handler error: " + ex.Message);
        }
        finally
        {
            Native.DragFinish(hDrop);
        }
    }

    public void Dispose()
    {
        IntPtr[] owned = _handles;
        foreach (IntPtr h in owned)
        {
            if (h == IntPtr.Zero) continue;
            Windows.Remove(h);
            Native.DestroyWindow(h);
        }
        _handles = Array.Empty<IntPtr>();

        // 用 IsWindow 對「自己擁有的 handle」直接驗，而非 FindWindowW：
        // 實測 FindWindowW 對這類 WS_EX_TOOLWINDOW + 隱藏視窗一律回 0（對照組 M05OverlayWindow 亦然），
        // 用它來驗殘留會變成永遠通過的假檢查。
        int leftover = owned.Count(h => h != IntPtr.Zero && Native.IsWindow(h));
        _evidence.Line($"hit layer disposed; leftoverWindows={leftover} (must be 0)");
    }
}
