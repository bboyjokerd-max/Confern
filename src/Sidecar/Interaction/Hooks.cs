using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// 低階全域 hook。移植自 M05（行為不變）。
/// 注意：WH_MOUSE_LL / WH_KEYBOARD_LL **不需要管理員權限**，
/// 且回呼在安裝它們的執行緒上被呼叫 → 事件都在 UI 執行緒上派發。
/// </summary>
internal sealed class Hooks : IDisposable
{
    private IntPtr _mouseHook, _keyboardHook;
    private Native.HookProc? _mouseProc, _keyProc;   // 保留委派避免被 GC

    public event Action<uint, Native.POINT>? MouseEvent;   // msg, 游標
    public event Action<uint, bool>? KeyEvent;             // vkCode, isDown

    /// <summary>
    /// 鍵盤鉤子是否真的裝上了（handle != 0）。**診斷探針要用它**：
    /// 「呼叫過 Install」與「鉤子存在」是兩件事，而探針若在沒有鉤子的情況下說自己驗過了，
    /// 那個結論就是假的（HOOK FAIL 只寫在日誌裡，不會擋下任何事）。
    /// </summary>
    public bool KeyboardInstalled => _keyboardHook != IntPtr.Zero;

    /// <summary>
    /// 鉤子回呼**實際被呼叫**的次數（keydown／keyup）。給診斷探針用：
    /// 「模式換了」單獨看不能證明那個鍵走過鉤子（同一個行程內誰都可能直接呼叫切層），
    /// 但「模式換了 ∧ 這個計數增加了」就把來源釘死在 WH_KEYBOARD_LL 回呼上。
    /// 產品路徑不讀它，也沒有任何一行行為依賴它。
    /// </summary>
    public int KeyCallbacks { get; private set; }

    public void Install(Evidence evidence)
    {
        IntPtr hInst = Native.GetModuleHandleW(null);

        _mouseProc = MouseCallback;
        _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseProc, hInst, 0);
        if (_mouseHook == IntPtr.Zero)
            evidence.Line("HOOK FAIL mouse: " + Marshal.GetLastWin32Error());

        _keyProc = KeyboardCallback;
        _keyboardHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _keyProc, hInst, 0);
        if (_keyboardHook == IntPtr.Zero)
            evidence.Line("HOOK FAIL keyboard: " + Marshal.GetLastWin32Error());

        evidence.Line($"hooks installed mouse=0x{_mouseHook.ToInt64():X} keyboard=0x{_keyboardHook.ToInt64():X}");
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            MouseEvent?.Invoke((uint)wParam.ToInt64(), info.pt);
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            uint m = (uint)wParam.ToInt64();
            bool isDown = m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN;
            bool isUp = m == Native.WM_KEYUP || m == Native.WM_SYSKEYUP;
            if (isDown || isUp) { KeyCallbacks++; KeyEvent?.Invoke(info.vkCode, isDown); }
        }
        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
    }
}
