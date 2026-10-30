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

    /// <summary>
    /// 自訂訊息：設定頁在**別的執行緒**改好設定，要求主執行緒套用（WM_APP + 3）。
    /// 為什麼一定要繞視窗訊息：設定頁跑在自己的 STA WPF 執行緒上，而證據日誌的 StreamWriter
    /// 與覆蓋層都不是執行緒安全的——直接呼叫不會當場報錯，只會變成隨機損壞，最難查的那種。
    /// </summary>
    public const uint WM_APP_SETTINGS_CHANGED = 0x8003;

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
    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 同步送一則訊息（等對方處理完才回來）。藥丸的自測用它把 <c>WM_LBUTTONUP</c>
    /// 送進**真實的 WndProc**：呼叫端不在訊息迴圈裡，用張貼的訊息會變成「送出去但還沒處理」，
    /// 而那種「還沒」在下一個斷言裡看起來就像失敗。真實使用者的點擊由系統送達，不受此影響。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 視窗在螢幕上的矩形（含不可見的部分）。它是「視窗實際多大」唯一的客觀來源，
    /// 所以「視窗矩形＝我們回報的矩形」這條斷言問的是作業系統，不是我們自己的變數。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // 刻意不引入 FindWindowW：實測它對 WS_EX_TOOLWINDOW + 隱藏的視窗一律回 0（T3 驗證），
    // 拿它驗殘留會變成永遠通過的假檢查；需要列舉時用 EnumWindows + GetClassNameW。

    // DragAcceptFiles 在 shell32.dll（不是 user32.dll）——M0.5 的 EntryPointNotFoundException 就是這個。
    [DllImport("shell32.dll")] public static extern bool DragAcceptFiles(IntPtr hWnd, bool fAccept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);
    [DllImport("shell32.dll")] public static extern void DragFinish(IntPtr hDrop);

    // ================= M1g：托盤圖示（規格 §8 的托盤入口） =================
    //
    // 托盤是**唯一**會長期活著、由 shell 主動回呼的視窗訊息來源。它與覆蓋層無關，
    // 所以訊息常數掛在 WM_APP 家族（0x8004 起），與設定頁的 0x8003 相接：

    /// <summary>托盤回呼（lParam ＝ 滑鼠訊息，見 <see cref="TrayCallback*"/>）。</summary>
    public const uint WM_APP_TRAY = 0x8004;

    /// <summary>要求托盤執行某個命令（wParam ＝ <see cref="TrayCommand*"/>）。</summary>
    public const uint WM_APP_TRAY_COMMAND = 0x8005;

    /// <summary>托盤選單命令的代碼。走訊息而不是直接呼叫，讓「選單點擊」與自測共用同一條分派。</summary>
    public const int TrayCommandOpenSettings = 1;
    public const int TrayCommandTogglePause = 2;
    public const int TrayCommandExit = 3;
    /// <summary>托盤「最近轉檔」子選單的父項（子選單本身不會回傳它，只是給人看的識別代碼）。</summary>
    public const int TrayCommandRecentMenu = 4;

    /// <summary>子選單裡的「清除紀錄」。</summary>
    public const int TrayCommandRecentClear = 1000;

    /// <summary>
    /// 最近轉檔的每一列：<c>1001 + index</c>。用保留範圍而不是逐項註冊，是因為重建子選單時
    /// **命令代碼必須跟著位置走**——TrackPopupMenu 只回傳一個 int，沒有別的東西可以對回去。
    /// </summary>
    public const int TrayCommandRecentBase = 1001;

    /// <summary>托盤回呼的滑鼠訊息（放在 <c>WM_APP_TRAY</c> 的 lParam）。</summary>
    public const int TrayCallbackLeftDoubleClick = 0x0203;   // WM_LBUTTONDBLCLK
    public const int TrayCallbackRightUp = 0x0205;           // WM_RBUTTONUP

    /// <summary>
    /// 訊息專用視窗的父視窗。**托盤刻意不用它**：它建立的視窗收不到
    /// <c>TaskbarCreated</c> 的廣播（那個廣播只送給頂層視窗），而 explorer 重啟後
    /// 重新加入圖示正是托盤最經典的 bug——用了它等於把那個 bug 寫進來。
    /// 常數仍然留在這裡，讓「為什麼不用」是一件查得到的事。
    /// </summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags;
        public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    /// <summary>
    /// explorer 重啟／當掉重開時，shell 會廣播這個訊息要求所有托盤重新加入圖示。
    /// 訊息代碼是**動態**的，每次開機都可能不同，所以只能在執行期註冊取得。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool ModifyMenuW(IntPtr hMenu, uint uPosition, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);
    /// <summary>按**位置**刪除一個選單項目（重建「最近轉檔」子選單時用）。</summary>
    [DllImport("user32.dll")]
    public static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    /// <summary>目前有幾項。子選單重建前要先問這個，刪不乾淨就會累積出重複的列。</summary>
    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);

    public const uint MF_STRING = 0x00000000;
    // ---- 選單旗標（托盤「最近轉檔」子選單用）----
    //
    // MF_POPUP：這一項不是命令而是一個子選單，此時 AppendMenuW 的 uIDNewItem 放的是**子選單的
    //           HMENU**，不是命令代碼。忘記這件事的症狀是「子選單點開是空的、父項也沒反應」。
    // MF_GRAYED：沒有東西可列時，子選單裡放一項灰字說明。刻意**不**把整個子選單變灰——
    //           灰掉的子選單在某些 Windows 版本連點都點不開，使用者看不到原因。
    // MF_BYPOSITION：DeleteMenu 按位置刪（命令代碼會隨重建而變，不能拿它定位）。
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_DISABLED = 0x00000002;
    public const uint MF_POPUP = 0x00000010;
    public const uint MF_BYPOSITION = 0x00000400;
    public const uint MF_SEPARATOR = 0x00000800;    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    /// <summary>
    /// 托盤選單前的 <c>SetForegroundWindow</c> 與選單後的「空訊息」是 MSDN 明文要求的兩步，
    /// 少任一步選單都會卡在畫面上（點別的地方不會關）。
    /// </summary>
    public const uint WM_NULL = 0x0000;

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
    /// <summary>游標離開追蹤中的視窗（<see cref="TrackMouseEvent"/> 的 <c>TME_LEAVE</c>）。</summary>
    public const uint WM_MOUSELEAVE = 0x02A3;
    /// <summary>點擊一個視窗時，系統先問「要不要啟用它」。藥丸回 <see cref="MA_NOACTIVATE"/>。</summary>
    public const uint WM_MOUSEACTIVATE = 0x0021;

    /// <summary>
    /// 「啟動我，但不要改變前景視窗」。藥丸視窗**必須**回這個：使用者的焦點不該因為
    /// 他想看清楚是哪幾個檔案失敗而跑到一個覆蓋層上（那會讓正在輸入的視窗失焦）。
    /// </summary>
    public const int MA_NOACTIVATE = 3;

    /// <summary><c>TrackMouseEvent</c> 的參數（D2 唯一的實作方式：進入／離開）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime;
    }

    public const uint TME_LEAVE = 0x00000002;

    /// <summary>
    /// 要求系統在游標離開 <c>hwndTrack</c> 時送一次 <see cref="WM_MOUSELEAVE"/>。
    ///
    /// 為什麼非它不可：`WM_MOUSEMOVE` 只在**游標移動**時到達，游標停住不動時什麼都不會來。
    /// 「游標停在藥丸上就不淡出」因此不能靠最後一次移動的時間戳推論（游標靜止時那個推論
    /// 會以為使用者離開了）——必須由系統在真正離開的那一刻告訴我們。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    /// <summary>
    /// 把滑鼠捕捉交給某個視窗。藥丸在按下滑鼠時捕捉，否則「按下之後手指飄出藥丸才放開」
    /// 會讓放開事件落在別的視窗上，使用者看到的是「我點了它卻沒反應」。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;

    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    /// <summary>托盤選單跳出來之前必須把焦點交給自己（MSDN 的要求，少了它選單會卡住）。</summary>
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    // ================= 合成輸入（**只給診斷探針**） =================
    // 產品路徑一個位元組都不碰這些：沒有任何一條使用者會走到的路徑會合成按鍵。
    //
    // ⚠ 呼叫端的唯一硬規則：**不可以從安裝低階鉤子的那個執行緒呼叫**。
    // WH_KEYBOARD_LL 的回呼是在安裝鉤子的執行緒上被呼叫的，而 keybd_event／SendInput 會擋到
    // 那個事件被處理完為止 —— 同一個執行緒送就等於「回呼永遠等不到那個執行緒去跑它」，
    // 系統會等 LowLevelHooksTimeout（預設 300 ms）之後直接放行（Win7 之後甚至把鉤子移除），
    // 結果是一個**假陰性**：看起來像「鉤子收不到合成鍵」。見 Program.WheelLayerProbe。
    [DllImport("user32.dll", SetLastError = true)]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    public const uint KEYEVENTF_KEYUP = 0x0002;

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
