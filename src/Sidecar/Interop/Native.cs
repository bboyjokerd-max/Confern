using System.Runtime.InteropServices;
using System.Text;

namespace Sidecar;

/// <summary>
/// P/Invoke 與常數的**唯一來源**（產品契約：其他檔案不得自行 DllImport）。
/// T1 只帶入骨架所需的訊息迴圈與主控台橋接；T2 補上 M05 移植與螢幕 API，
/// T3 補上視窗樣式與拖放 API。
/// </summary>
internal static class Native
{
    // ---- 訊息迴圈 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public POINT pt;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    // ---- 主控台橋接 ----
    // Sidecar 是 WinExe（不留主控台視窗：那會搶走前景焦點並破壞「前景行程＝explorer」判定）。
    // 診斷模式（--version／--dump-monitors）需要把輸出送回呼叫端的終端機，
    // 因此在那裡才接上父行程的主控台；一般常駐模式永遠不接。
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    /// <summary>接上父行程的主控台並把 stdout/stderr 重新指向它；沒有父主控台時為 no-op。</summary>
    public static bool AttachParentConsole()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return false;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
        return true;
    }

    // ---- 螢幕幾何 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;

    // ---- 視窗樣式 ----
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    // 刻意不使用 WS_EX_TRANSPARENT (0x20)：加上它會讓 WindowFromPoint 跳過命中視窗，
    // 整個機制立即失效（M0.5 已驗證）。建立後會實際檢查該位元沒被設起來，設了就直接丟例外。
    public const long WsExTransparentBit = 0x00000020L;
    public const int GwlExStyle = -20;

    public const int LWA_ALPHA = 0x2;
    /// <summary>1/255：視覺不可見、仍可被拖放命中（M0.5 的生產值）。</summary>
    public const byte OverlayAlpha = 1;

    // ---- 視窗訊息 ----
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_DROPFILES = 0x0233;
    /// <summary>自訂訊息：要求接收層收起（WM_APP + 1）。跨執行緒一律走這條，不直接呼叫 ShowWindow。</summary>
    public const uint WM_APP_FORCE_HIDE = 0x8001;

    // ---- 視窗位置 ----
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0;

    public const uint DRAGQUERY_COUNT = 0xFFFFFFFF;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    // 刻意不引入 FindWindowW：實測它對 WS_EX_TOOLWINDOW + 隱藏的視窗一律回 0（T3 驗證），
    // 拿它驗殘留會變成永遠通過的假檢查；需要列舉時用 EnumWindows + GetClassNameW。

    // DragAcceptFiles 在 shell32.dll（不是 user32.dll）——M0.5 的 EntryPointNotFoundException 就是這個。
    [DllImport("shell32.dll")] public static extern bool DragAcceptFiles(IntPtr hWnd, bool fAccept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);
    [DllImport("shell32.dll")] public static extern void DragFinish(IntPtr hDrop);
}
