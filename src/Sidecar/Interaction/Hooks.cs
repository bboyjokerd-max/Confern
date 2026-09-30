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
            if (isDown || isUp) KeyEvent?.Invoke(info.vkCode, isDown);
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
