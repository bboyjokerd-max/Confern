using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// 托盤選單的三個命令。**這是選單、快捷雙擊與自測共用的同一組代碼**——
/// 自測若能走一條「只有它自己會走」的路，它驗到的就不是使用者會走的那條。
/// </summary>
internal enum TrayCommand
{
    OpenSettings = Native.TrayCommandOpenSettings,
    TogglePause = Native.TrayCommandTogglePause,
    Exit = Native.TrayCommandExit
}

/// <summary>
/// 通知區域（托盤）圖示，規格 §8 的托盤入口（待辦 4）。
///
/// 為什麼是原始 Win32 而不是 <c>System.Windows.Forms.NotifyIcon</c>：
///   - 本專案**不使用任何 WinForms 型別**（csproj 的註解寫明了，UseWindowsForms 只是為了離線解析）；
///   - 托盤圖示要能被自測逐項斷言（有沒有真的註冊、選單有幾項、提示字串是什麼、關掉之後
///     shell 還留不留著它），而 <c>NotifyIcon</c> 把這些都包在裡面，沒有一項問得出來；
///   - 這個檔案與 <see cref="HitLayer"/>／<see cref="VisualLayer"/> 同一個風格：明確的 P/Invoke、
///     <c>IntPtr</c>、成對的建立／釋放、失敗一律大聲記錄。
///
/// 圖示本身**不使用任何二進位資產檔**：用 GDI+ 以 <see cref="WheelDesign"/> 的色票畫出
/// 一個小輪盤（16×16 給工具列、32×32 給高 DPI），再 <c>Bitmap.GetHicon()</c> 換成 HICON。
/// 沒有資產檔就沒有「檔案不見了所以圖示變空白」這種失效模式。
///
/// 執行緒契約：視窗、選單、圖示全部屬於**建立它的那條執行緒**（＝主訊息迴圈執行緒）。
/// 選單點擊與 <see cref="InvokeCommand"/> 都排到那條執行緒上執行，所以回呼永遠在主執行緒上跑。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>視窗類別名。產品名稱的唯一擁有者是 <see cref="ProductInfo"/>（改名時必須跟著改）。</summary>
    private const string ClassName = ProductInfo.Name + "Tray";

    /// <summary>托盤圖示的識別碼（同一個 hwnd 下可以有多個圖示，這裡只有一個）。</summary>
    private const uint IconId = 1;

    // ================= 選單文字（唯一權威） =================
    //
    // 為什麼放在這裡而不是散在 AppendMenuW 的呼叫處：自測要斷言的是**這三行字**，
    // 若斷言自己另寫一份字面值，改了一邊而另一邊沒改就會「通過」——那正是驗收最不該有的性質。

    public static string MenuOpenSettings => UiStrings.Get("tray.openSettings");
    public static string MenuPause => UiStrings.Get("tray.pause");
    public static string MenuResume => UiStrings.Get("tray.resume");
    public static string MenuExit => UiStrings.Get("tray.exit");

    /// <summary>選單有幾項。自測與驗收腳本都斷言這個數字（少一項是很容易發生、又不容易看見的退化）。</summary>
    public const int MenuItemCount = 3;

    /// <summary>提示文字的兩個狀態（ASCII，因為驗收腳本必須是純 ASCII 才比對得到）。</summary>
    private const string StateRunning = "running";
    private const string StatePaused = "paused";

    private static readonly Dictionary<IntPtr, TrayIcon> Instances = new();
    private static Native.WndProcDelegate? _proc;      // 保留委派，避免被 GC 回收（同 HitLayer／VisualLayer）
    private static uint _taskbarCreatedMessage;        // RegisterWindowMessageW("TaskbarCreated") 的結果

    private readonly Evidence _evidence;
    private readonly Action _onOpenSettings;
    private readonly Action _onTogglePause;
    private readonly Action _onExit;

    /// <summary>
    /// 「捕捉現在是停用的嗎」——由呼叫端提供；圖示顯示的狀態一律以它為準。
    ///
    /// 為什麼不自己記（第一版就是 `ApplyPaused(!_paused)`）：那記的是**使用者剛按下的意圖**，
    /// 不是結果。緊急停用（保險 #5）之後要求「繼續轉換」會被拒絕（規格 §4.2.2：直到重啟），
    /// 屆時圖示若照意圖翻成 running，就會顯示「可以轉」而捕捉其實是停用的——
    /// 一個會說謊的狀態顯示，比沒有這個顯示更糟。
    /// </summary>
    private readonly Func<bool> _isCaptureDisabled;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _menu;
    private readonly IntPtr _icon16;
    private readonly IntPtr _icon32;

    private bool _paused;
    private bool _disposed;
    private int _shellReAdds;

    /// <summary>圖示是否真的在 shell 上。任何一條失敗路徑都會讓它維持 false（不假裝成功）。</summary>
    public bool IsCreated { get; private set; }

    /// <summary>提示文字。自測與日誌共用同一個來源，不另寫一份字面值。</summary>
    public string Tooltip { get; private set; }

    /// <summary>
    /// 托盤自己的視窗（0＝建立失敗）。公開出來是為了讓「從別的執行緒要求關機」這條路走
    /// <see cref="Native.WM_APP_TRAY_COMMAND"/> 的張貼，而不是跨執行緒直接呼叫回呼。
    /// </summary>
    public IntPtr WindowHandle => _hwnd;

    /// <summary>explorer 重啟後重新加入圖示的次數（正常執行時應為 0）。</summary>
    public int ShellReAdds => _shellReAdds;

    public bool IsPaused => _paused;

    public TrayIcon(Evidence evidence, Func<bool> isCaptureDisabled,
                    Action onOpenSettings, Action onTogglePause, Action onExit)
    {
        _evidence = evidence;
        _isCaptureDisabled = isCaptureDisabled;
        _onOpenSettings = onOpenSettings;
        _onTogglePause = onTogglePause;
        _onExit = onExit;
        Tooltip = BuildTooltip(paused: false);

        try
        {
            _proc ??= StaticWndProc;
            _taskbarCreatedMessage = Native.RegisterWindowMessageW("TaskbarCreated");

            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = ClassName
            };
            Native.RegisterClassExW(ref wc);

            // **刻意不是 HWND_MESSAGE 的訊息專用視窗**：訊息專用視窗收不到 TaskbarCreated
            // 這種廣播（廣播只送給頂層視窗），用了它就等於放棄 explorer 重啟後的重新加入。
            // 這裡的替代品是一個 0×0、WS_POPUP、不顯示的**頂層**視窗：
            //   - WS_EX_TOOLWINDOW → 不出現在 Alt+Tab
            //   - WS_EX_NOACTIVATE → 建立與顯示都不搶前景（S15.7 會量這件事）
            _hwnd = Native.CreateWindowExW(
                Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                ClassName, ClassName, Native.WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Fail($"window creation failed (lastError={Marshal.GetLastWin32Error()})");
                return;
            }
            Instances[_hwnd] = this;

            _icon16 = CreateWheelIcon(16);
            _icon32 = CreateWheelIcon(32);
            if (_icon16 == IntPtr.Zero || _icon32 == IntPtr.Zero)
            {
                Fail($"icon drawing failed (hIcon16=0x{_icon16.ToInt64():X} hIcon32=0x{_icon32.ToInt64():X})");
                return;
            }

            _menu = BuildMenu();
            if (_menu == IntPtr.Zero)
            {
                Fail($"CreatePopupMenu failed (lastError={Marshal.GetLastWin32Error()})");
                return;
            }

            _evidence.Line($"tray: class={ClassName} hwnd=0x{_hwnd.ToInt64():X} " +
                           $"taskbarCreatedMessage=0x{_taskbarCreatedMessage:X} " +
                           $"icons=16px+32px (GDI+ drawn, no asset files) menu items={MenuItemCount}");
            _evidence.Line($"tray: menu items={MenuItemCount} item0='{LabelFor(TrayCommand.OpenSettings)}' " +
                           $"item1='{LabelFor(TrayCommand.TogglePause)}' item2='{LabelFor(TrayCommand.Exit)}' " +
                           $"labels_ok={LabelsMatchSpec()}");
            LogTooltip();

            if (!ShellAdd())
            {
                Fail($"Shell_NotifyIconW(NIM_ADD) failed (lastError={Marshal.GetLastWin32Error()})");
                return;
            }

            IsCreated = true;
            _evidence.Line("tray: icon registered");
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildTooltip(bool paused) =>
        $"{ProductInfo.Name} - {(paused ? StatePaused : StateRunning)}";

    /// <summary>
    /// 提示文字是**使用者唯一看得到的狀態顯示**，而它同時是驗收腳本唯一能斷言的東西
    /// （腳本必須純 ASCII），所以兩種寫法一起寫進日誌：人看引號裡的字，腳本比對 state=。
    /// </summary>
    private void LogTooltip() =>
        _evidence.Line($"tray: tooltip '{Tooltip}' state={(_paused ? StatePaused : StateRunning)}");

    private string LabelFor(TrayCommand command) => command switch
    {
        TrayCommand.OpenSettings => MenuOpenSettings,
        TrayCommand.TogglePause => _paused ? MenuResume : MenuPause,
        _ => MenuExit
    };

    /// <summary>自測用：選單的三行字是不是規格說的那三行（含暫停／繼續的切換）。</summary>
    public bool LabelsMatchSpec() =>
        LabelFor(TrayCommand.OpenSettings) == MenuOpenSettings &&
        LabelFor(TrayCommand.TogglePause) == (_paused ? MenuResume : MenuPause) &&
        LabelFor(TrayCommand.Exit) == MenuExit;

    // ================= 圖示（GDI+ 畫，沒有資產檔） =================

    /// <summary>
    /// 畫一個迷你輪盤：深底圓 ＋ 兩個扇形 ＋ 琥珀色的選中扇形 ＋ 薄荷外緣線。
    /// 顏色**全部**來自 <see cref="WheelDesign"/>，不新增第二份色票權威；
    /// 深底是 <c>Veil</c> 的顏色（暗罩本身是 .80 的半透明，托盤圖示必須不透明）。
    /// </summary>
    public static Bitmap DrawWheelIcon(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float c = size / 2f;
        float r = size / 2f - Math.Max(1f, size / 16f);       // 留一點邊，縮到 16px 才不會被切角

        using (var body = new SolidBrush(Color.FromArgb(255, WheelDesign.Veil.R, WheelDesign.Veil.G, WheelDesign.Veil.B)))
            g.FillEllipse(body, c - r, c - r, r * 2, r * 2);

        // 兩個扇形：未選中的交替填色（與輪盤同一組色票）
        using (var fillA = new SolidBrush(Color.FromArgb(255, WheelDesign.FillA.R, WheelDesign.FillA.G, WheelDesign.FillA.B)))
        using (var fillB = new SolidBrush(Color.FromArgb(255, WheelDesign.FillB.R, WheelDesign.FillB.G, WheelDesign.FillB.B)))
        {
            g.FillPie(fillA, c - r, c - r, r * 2, r * 2, -92f, 88f);
            g.FillPie(fillB, c - r, c - r, r * 2, r * 2, 92f, 88f);
        }

        // 選中的扇形：輪盤上「選中」＝不透明填色 ＋ 琥珀描邊（唯一亮度手段，同 WheelDesign 的裁定）
        using (var sel = new SolidBrush(WheelDesign.SelFill))
        using (var selPen = new Pen(WheelDesign.SelStroke, Math.Max(1f, size / 16f)))
        {
            g.FillPie(sel, c - r, c - r, r * 2, r * 2, -4f, 88f);
            g.DrawPie(selPen, c - r, c - r, r * 2, r * 2, -4f, 88f);
        }

        // 外緣髮絲線：輪盤的辨識特徵，縮到 16px 也還看得出來
        using (var rim = new Pen(WheelDesign.Rim, Math.Max(1f, size / 16f)))
            g.DrawEllipse(rim, c - r, c - r, r * 2, r * 2);

        // 中心孔：輪盤中心是鏤空的（來源縮圖的位置），留一個深色小圓當作辨識點
        float inner = r * WheelDesign.CenterRatio * 0.55f;
        using (var hole = new SolidBrush(Color.FromArgb(255, 10, 15, 20)))
            g.FillEllipse(hole, c - inner, c - inner, inner * 2, inner * 2);

        return bmp;
    }

    /// <summary>
    /// 畫好之後轉 HICON。**必須 <c>Dispose</c> 點陣圖**：<c>GetHicon</c> 會複製出一個獨立的
    /// GDI 圖示物件，原點陣圖之後就沒有用了（不釋放會每次啟動漏一張點陣圖）。
    /// </summary>
    private static IntPtr CreateWheelIcon(int size)
    {
        try
        {
            using Bitmap bmp = DrawWheelIcon(size);
            return bmp.GetHicon();
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    // ================= shell 註冊 =================

    private Native.NOTIFYICONDATAW BuildData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
        uCallbackMessage = Native.WM_APP_TRAY,
        hIcon = _icon16,
        szTip = Tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private bool ShellAdd()
    {
        Native.NOTIFYICONDATAW data = BuildData();
        return Native.Shell_NotifyIconW(Native.NIM_ADD, ref data);
    }

    /// <summary>換圖示或換提示文字之後重新送一次（<c>NIM_MODIFY</c>）。</summary>
    private bool ShellModify()
    {
        Native.NOTIFYICONDATAW data = BuildData();
        return Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// 暫停／繼續：換提示文字與選單第二項的字（選單上的字與圖示的提示是同一個狀態的兩種說法，
    /// 只改其中一個會讓使用者看到自相矛盾的畫面）。可在任何執行緒呼叫，實際動作排到擁有它的執行緒。
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (_disposed) return;
        if (_hwnd == IntPtr.Zero || _menu == IntPtr.Zero) return;

        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            // 視窗與選單都屬於建立它們的執行緒：跨執行緒直接碰會變成隨機損壞，一律排隊。
            Native.PostMessageW(_hwnd, Native.WM_APP_TRAY_COMMAND, new IntPtr(paused ? PauseCommandCode : ResumeCommandCode), IntPtr.Zero);
            return;
        }
        ApplyPaused(paused);
    }

    /// <summary>內部用的兩個額外代碼（<see cref="TrayCommand"/> 是給使用者命令的，暫停狀態是內部狀態）。</summary>
    private const int PauseCommandCode = 101;
    private const int ResumeCommandCode = 102;

    private void ApplyPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        Tooltip = BuildTooltip(paused);

        if (_menu != IntPtr.Zero)
        {
            // 第二項（位置 1）的字要跟著換：選單上的「暫停轉換」與「繼續轉換」是同一個開關的兩面。
            Native.ModifyMenuW(_menu, 1, Native.MF_STRING, new IntPtr(Native.TrayCommandTogglePause),
                               paused ? MenuResume : MenuPause);
        }

        bool ok = IsCreated ? ShellModify() : false;
        _evidence.Line($"tray: {(_paused ? "paused" : "resumed")} -> tooltip '{Tooltip}' " +
                       $"menu1='{LabelFor(TrayCommand.TogglePause)}' shellUpdated={ok}");
        if (IsCreated && !ok)
            _evidence.Line($"WARNING tray: tooltip/icon update failed (lastError={Marshal.GetLastWin32Error()}); " +
                           "the shell may still show the previous state");
    }

    // ================= 選單 =================

    private IntPtr BuildMenu()
    {
        IntPtr menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return IntPtr.Zero;
        Native.AppendMenuW(menu, Native.MF_STRING, new IntPtr(Native.TrayCommandOpenSettings), MenuOpenSettings);
        Native.AppendMenuW(menu, Native.MF_STRING, new IntPtr(Native.TrayCommandTogglePause), MenuPause);
        Native.AppendMenuW(menu, Native.MF_STRING, new IntPtr(Native.TrayCommandExit), MenuExit);
        return menu;
    }

    /// <summary>
    /// 真的把選單跳出來給使用者（右鍵）。
    ///
    /// MSDN 明文要求的兩步：跳選單**前** <c>SetForegroundWindow</c>、選完**後**送一個空訊息——
    /// 少了任何一步，選單會卡在畫面上（點其他地方不會關掉）。<c>TPM_RETURNCMD</c> 讓
    /// <c>TrackPopupMenu</c> 直接回傳被選的命令代碼，於是「選單 → 命令」不必繞 <c>WM_COMMAND</c>。
    /// </summary>
    private void ShowContextMenu()
    {
        if (_menu == IntPtr.Zero || _hwnd == IntPtr.Zero) return;
        Native.GetCursorPos(out Native.POINT pt);

        IntPtr previousForeground = Native.GetForegroundWindow();
        Native.SetForegroundWindow(_hwnd);
        int selected = Native.TrackPopupMenu(
            _menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Native.PostMessageW(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (selected != 0)
        {
            _evidence.Line($"tray: menu selection={selected} label='{LabelFor((TrayCommand)selected)}'");
            InvokeCommand((TrayCommand)selected);
        }

        // 選單關掉之後把前景還回去。少了這一步，使用者只是右鍵點一下托盤，焦點就留在
        // 一個 0×0 的隱藏視窗上（切到別的城市視窗都會怪怪的）。
        if (previousForeground != IntPtr.Zero && previousForeground != _hwnd &&
            Native.GetForegroundWindow() == _hwnd)
            Native.SetForegroundWindow(previousForeground);
    }

    /// <summary>
    /// 執行一個托盤命令。**真正的選單點擊與自測走的是這一個方法**——
    /// 自測若能繞過它，驗到的就只是自測自己。
    /// 可在任何執行緒呼叫：命令一律排到擁有視窗的那條執行緒上執行。
    /// </summary>
    public void InvokeCommand(TrayCommand command)
    {
        if (command is TrayCommand.OpenSettings or TrayCommand.TogglePause or TrayCommand.Exit)
        {
            Dispatch(command);
            return;
        }
        _evidence.Line($"tray: unknown command {command} ignored (no callback exists for it)");
    }

    private void Dispatch(TrayCommand command) => Dispatch((int)command);

    private void Dispatch(int code)
    {
        switch (code)
        {
            case Native.TrayCommandOpenSettings:
                _onOpenSettings();
                break;
            case Native.TrayCommandTogglePause:
                // 順序刻意是「先請呼叫端做事，再問真正的結果」：停用／復原捕捉是另一個擁有者的事
                // （ReleaseGuard ＋ DragDetector），而「繼續轉換」在緊急停用之後會被拒絕，
                // 所以圖示只有在回呼跑完之後才問得到真相。
                // try/finally：回呼若丟例外，仍然同步一次，不留一個過期的說法在圖示上。
                try { _onTogglePause(); }
                finally { ApplyPaused(_isCaptureDisabled()); }
                break;
            case Native.TrayCommandExit:
                _onExit();
                break;
            case PauseCommandCode:
                ApplyPaused(true);
                break;
            case ResumeCommandCode:
                ApplyPaused(false);
                break;
            default:
                _evidence.Line($"tray: unknown command {code} ignored (no callback exists for it)");
                break;
        }
    }

    // ================= shell 狀態 =================

    /// <summary>
    /// shell 手上還留著這個圖示嗎？
    ///
    /// 作法：對同一筆資料送一次 <c>NIM_MODIFY</c>。**自測專用**——正式路徑不會呼叫它，
    /// 因為它在「圖示還在」時會多送一次無害的更新，而它的價值完全在於「關掉之後還剩不剩」。
    /// 為什麼用 <c>NIM_MODIFY</c> 而不是「等幾毫秒看圖示有沒有消失」：前者是一個可回傳、
    /// 可斷言的布林，後者只能靠人眼看，而且在自動化裡一定會變成不穩定的 sleep。
    /// </summary>
    public bool ProbeShellStillHasIcon()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return false;
        Native.NOTIFYICONDATAW data = BuildData();
        return Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
    }

    // ================= 訊息分派 =================

    /// <summary>
    /// 這個訊息是我們自己的行程送來的嗎？
    ///
    /// <c>WM_APP</c> 家族是自訂訊息，任何行程都可以對這個視窗 <c>PostMessage</c> 一個「結束」
    /// 過來——不檢查來源的話，一個陌生行程就能關掉這個程式（或讓它把接收層停用）。
    /// 這不是理論問題：訊息編號是可以猜的，而托盤視窗的 hwnd 用 <c>FindWindow</c> 就找得到。
    /// </summary>
    private static bool FromThisProcess(IntPtr hWnd)
    {
        Native.GetWindowThreadProcessId(hWnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hWnd, out TrayIcon? tray) && tray._hwnd == hWnd)
        {
            if ((msg == Native.WM_APP_TRAY || msg == Native.WM_APP_TRAY_COMMAND) && !FromThisProcess(hWnd)) return IntPtr.Zero;

            // explorer 重啟：shell 廣播 TaskbarCreated，所有托盤都必須重新加入圖示。
            // 忘了這件事的症狀是「explorer 當掉重開之後圖示就不見了」，而且是靜默的。
            if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                tray._shellReAdds++;
                bool reAdded = tray.ShellAdd();
                tray.IsCreated = reAdded;
                tray._evidence.Line($"tray: TaskbarCreated broadcast received (explorer restarted) -> " +
                                    $"re-add attempt #{tray._shellReAdds} ok={reAdded}" +
                                    (reAdded ? string.Empty : $" (lastError={Marshal.GetLastWin32Error()})"));
                return IntPtr.Zero;
            }

            if (msg == Native.WM_APP_TRAY)
            {
                int callback = lParam.ToInt32();
                if (callback == Native.TrayCallbackRightUp) tray.ShowContextMenu();
                else if (callback == Native.TrayCallbackLeftDoubleClick) tray.InvokeCommand(TrayCommand.OpenSettings);
                return IntPtr.Zero;
            }

            if (msg == Native.WM_APP_TRAY_COMMAND)
            {
                tray.Dispatch(wParam.ToInt32());
                return IntPtr.Zero;
            }

            if (msg == Native.WM_DESTROY)
            {
                Instances.Remove(hWnd);
                return IntPtr.Zero;
            }
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    /// <summary>
    /// 任何一條失敗路徑都必須經過這裡：**大聲記錄，而且 <see cref="IsCreated"/> 維持 false**。
    /// 托盤圖示默默不出現是最糟的結果——使用者只會覺得「這個程式好像沒在跑」。
    /// </summary>
    private void Fail(string reason)
    {
        IsCreated = false;
        _evidence.Line($"FAIL tray: icon NOT created: {reason}");
        Native.AttachParentConsole();
        Console.Error.WriteLine($"tray: icon NOT created: {reason}");
    }

    /// <summary>
    /// 把建立過的東西**恰好釋放一次**：HICON ×2、HMENU ×1、視窗 ×1。
    /// 圖示與選單是 GDI／使用者物件，行程結束不會自動回收成「乾淨」的狀態（漏掉會累積），
    /// 而視窗若不銷毀，訊息會繼續進來、實例表也會留著一個已經沒有人記得的項目。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsCreated && _hwnd != IntPtr.Zero)
        {
            Native.NOTIFYICONDATAW data = BuildData();
            bool removed = Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
            IsCreated = false;
            _evidence.Line($"tray: NIM_DELETE removed={removed}");   // false 也要說，不能靜默
        }

        if (_menu != IntPtr.Zero) Native.DestroyMenu(_menu);
        if (_icon16 != IntPtr.Zero) Native.DestroyIcon(_icon16);
        if (_icon32 != IntPtr.Zero) Native.DestroyIcon(_icon32);

        if (_hwnd != IntPtr.Zero)
        {
            Instances.Remove(_hwnd);
            Native.DestroyWindow(_hwnd);
        }

        _evidence.Line($"tray: disposed (icon16+icon32+menu destroyed; shellReAdds={_shellReAdds})");
    }
}
