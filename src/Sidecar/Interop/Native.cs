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

    /// <summary>
    /// 自訂訊息：週期回呼（進度顯示的淡出）。
    /// 由執行緒計時器 PostMessage 進來，**不經 SetTimer/WM_TIMER**：WM_TIMER 是低優先權的
    /// 合成訊息（佇列一忙就不被產生），而張貼的訊息是真實的佇列項目。
    /// 注意：這是**防禦性選擇，不是某個 bug 的修法** —— 2026-10-01 那次「進度卡住不淡出」
    /// 的真因是淡出時鐘（見 ProgressPresenter.Update），換掉計時器並沒有修好它。
    /// 留著它的理由是它讓回呼次數可被斷言（S12.10d2），真的被餓死時看得出來。
    /// </summary>
    public const uint WM_APP_TICK = 0x8002;

    /// <summary>保留作為對照：這個常數已不再是週期回呼的驅動來源。</summary>
    public const uint WM_TIMER = 0x0113;

    // ---- 計時器 ----
    // 進度顯示要週期性更新（淡出、重畫）。用 SetTimer 讓 GetMessageW 繼續阻塞——
    // 不改成 sleep 輪詢，因為那樣會讓 UI 行程一直在醒著，也失去「有訊息才動」的性質。
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    /// <summary>把目前排隊的訊息跑完（不阻塞）。等待非同步結果時用，避免訊息卡在佇列裡。</summary>
    public static void PumpMessages()
    {
        while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

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

    // ---- 低階 hook ----
    // WH_MOUSE_LL / WH_KEYBOARD_LL **不需要管理員權限**：它們在安裝它們的行程中執行，
    // 不像 WH_GETMESSAGE 需要注入目標行程。回呼在安裝執行緒（＝有訊息迴圈的 UI 執行緒）上被呼叫。
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // ---- 滑鼠／鍵盤訊息與系統度量 ----
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;

    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    // ---- 虛擬鍵 ----
    public const uint VK_CONTROL = 0x11;
    public const uint VK_SHIFT = 0x10;
    public const uint VK_MENU = 0x12;
    public const uint VK_Q = 0x51;

    // ---- 輪盤操作鍵（規格 §4.3 的鍵盤表）----
    public const uint VK_RETURN = 0x0D;
    public const uint VK_ESCAPE = 0x1B;
    public const uint VK_LEFT = 0x25;
    public const uint VK_UP = 0x26;
    public const uint VK_RIGHT = 0x27;
    public const uint VK_DOWN = 0x28;

    // ---- 收層原因碼（夾帶在 WM_APP_FORCE_HIDE 的 wParam）----
    // 定時器在執行緒池執行緒上觸發，不可跨執行緒直接呼叫 ShowWindow；
    // 因此「決定收層」與「實際收層」分離：guard 決定並投遞，UI 執行緒執行。
    public const int HideReasonDrop = 1;
    public const int HideReasonGrace = 2;
    public const int HideReasonWatchdog = 3;
    public const int HideReasonKillSwitch = 4;
    public const int HideReasonManual = 5;

    public static string HideReasonText(int code) => code switch
    {
        HideReasonDrop => "drop resolved",
        HideReasonGrace => $"{250} ms grace elapsed",
        HideReasonWatchdog => "guard #3 watchdog",
        HideReasonKillSwitch => "kill switch",
        HideReasonManual => "manual",
        _ => $"unknown({code})"
    };

    // ================= M1b：視覺層繪製（per-pixel alpha） =================
    // 命中層與視覺層用的是同一個位元（0x20），但契約**相反**：
    //   命中層  **絕不可**設（設了 WindowFromPoint 會跳過它，拖放全部失效）
    //   視覺層  **必須**設（設了拖放才會穿透它、落到下面的命中層）
    // 刻意保留兩個名稱：避免有人把其中一邊的斷言複製到另一邊。
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int SW_SHOWNOACTIVATE = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize; public int biWidth; public int biHeight;
        public short biPlanes; public short biBitCount;
        public int biCompression; public int biSizeImage;
        public int biXPelsPerMeter; public int biYPelsPerMeter;
        public int biClrUsed; public int biClrImportant;
    }

    public const uint ULW_ALPHA = 0x02;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;
    public const int BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    /// <summary>命中測試查詢：**會**跳過 WS_EX_TRANSPARENT 的視窗，因此正好可以證明穿透成立。</summary>
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);

    // ---- 訊息幫浦（驗證用）----
    // 收層是「決定」與「執行」分離的非同步動作（由 guard 投遞、UI 執行緒執行），
    // 驗證時若不多跑訊息，收層永遠不會落地，檢查就會變成假的。
    // MSG／TranslateMessage／DispatchMessageW 已存在（訊息迴圈在用），這裡只補 PeekMessageW。
    public const uint PM_REMOVE = 0x0001;
    [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    /// <summary>
    /// 讀出 HDROP 的檔案清單。CF_HDROP 的 <c>HGLOBAL</c> 本身就是 HDROP，兩種來源都要用同一套讀法。
    /// </summary>
    public static string[] ReadDropFileList(IntPtr hDrop)
    {
        uint count = DragQueryFileW(hDrop, DRAGQUERY_COUNT, null, 0);
        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint len = DragQueryFileW(hDrop, i, null, 0);
            var sb = new StringBuilder((int)len + 1);
            DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
            paths.Add(sb.ToString());
        }
        return paths.ToArray();
    }
}
