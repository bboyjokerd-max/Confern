using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace Sidecar;

internal static class Program
{
    /// <summary>觸發鍵預設值 VK_SPACE（Q1：預設空白鍵、可設定）。此為唯一硬編碼處。</summary>
    public const uint DefaultTriggerKey = 0x20;

    /// <summary>
    /// 對外顯示的版本。用 <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>：
    /// <c>AssemblyVersion</c> 會被補成四段（<c>0.1.0</c> → <c>0.1.0.0</c>），不適合當顯示字串。
    /// </summary>
    public static string VersionText()
    {
        var asm = typeof(Program).Assembly;
        string? info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info)
            ? asm.GetName().Version?.ToString(3) ?? "0.0.0"
            : info;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Options opt = Options.Parse(args);

        if (opt.ShowUsage)
        {
            Native.AttachParentConsole();
            Console.WriteLine(Options.Usage);
            return 2;
        }

        if (opt.ShowVersion)
        {
            Native.AttachParentConsole();
            Console.WriteLine(VersionText());
            return 0;
        }

        // 純幾何自測不需要任何視窗；在建任何圖層之前就處理掉。
        // 注意：執行邏輯（含 CycleTest／WheelDemo）掛在 Options 上，這是 M1a 遺留的結構。
        if (opt.GeometrySelfTest)
        {
            Native.AttachParentConsole();
            return App.RunGeometrySelfTest(opt);
        }

        // 單一實例守衛。A13 要求「被強制結束後能立刻重啟」，
        // 因此這裡不留下任何需要清理的檔案鎖。
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Sidecar.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Native.AttachParentConsole();
            Console.Error.WriteLine("Sidecar is already running.");
            return 3;
        }

        return App.Run(opt);
    }
}

/// <summary>
/// 命令列介面。**先凍結**：驗收跑者（tools/verify-m1a.ps1）依賴這組旗標，
/// 後續任務只接線、不改名。
/// </summary>
internal sealed class Options
{
    public const string Usage =
        """
        Sidecar (M1a)
          --version                 print version and exit
          --dump-monitors           list monitors and exit (arrives in T2)
          --raise                   raise the hit layer without hooks (diagnostic)
          --watchdog-seconds <N>    override guard #3 visible timeout (default 30)
          --assume-explorer-foreground
                                    diagnostic: arm without the explorer-foreground
                                    check, so acceptance runs need not steal focus
          --log <path>              evidence log path
          --trigger-key <0xNN|NAME> trigger virtual-key (default 0x20 / VK_SPACE)
          --cycle-test <N>          show/hide the hit layer N times and exit
          --wheel-demo <X> <Y>      raise both layers and show the wheel at (X,Y) (diagnostic)
          --wheel-dump <path>       with --wheel-demo: write the painted frame as PNG and exit
          --wheel-source <FMT>      with --wheel-demo: source format label (default HEIC)
          --wheel-targets <a,b,c>   with --wheel-demo: target format list (default PNG,JPG,WEBP,BMP)
          --wheel-selected <N>      with --wheel-demo: initially selected sector (default 0)
          --wheel-previews <N>      with --wheel-demo: file count shown in the preview (default 1)
          --wheel-geometry-selftest check the wheel geometry against known angles and exit
          --wheel-simulate-cycles <N>
                                    run N non-invasive capture/show/select/escape cycles and exit
          --quiet                   do not echo events to the console
        """;

    public bool ShowUsage;
    public bool ShowVersion;
    public bool DumpMonitors;
    public bool ForceRaiseHitLayer;
    public string? LogPath;
    public uint TriggerKey = Program.DefaultTriggerKey;
    public int CycleTest;
    public bool Quiet;

    /// <summary>
    /// 保險 #3 的可視逾時（秒）。生產值為規格固定的 30 s；此旗標僅供診斷加速驗證，
    /// 每次都必須寫進證據，避免「以為驗的是 30 秒其實不是」。
    /// </summary>
    public int WatchdogSeconds = 30;

    /// <summary>
    /// 診斷專用：略過「左鍵按下時前景必須是 explorer」的判定。
    /// 存在的理由＝自動化驗收若必須先把 explorer 弄到前景，就得去最小化使用者的視窗，
    /// 讓測試本身的穩定性取決於桌面上剛好有什麼；代價是停用零侵入的第一道保證，
    /// 因此一律寫進證據、永不預設。
    /// </summary>
    public bool AssumeExplorerForeground;

    /// <summary>診斷用：把輪盤顯示在指定螢幕座標（驗兩層堆疊的穿透與輪盤外觀）。</summary>
    public int? WheelDemoX;
    public int? WheelDemoY;
    public string? WheelDump;
    public string WheelSource = "HEIC";
    public string[] WheelTargets = Array.Empty<string>();
    public int WheelSelected;
    public int WheelPreviews = 1;
    public bool GeometrySelfTest;
    public int WheelSimulateCycles;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--help" or "-h" or "/?": o.ShowUsage = true; break;
                case "--version" or "-v": o.ShowVersion = true; break;
                case "--dump-monitors": o.DumpMonitors = true; break;
                case "--raise": o.ForceRaiseHitLayer = true; break;
                case "--assume-explorer-foreground": o.AssumeExplorerForeground = true; break;
                case "--wheel-dump": if (i + 1 < args.Length) o.WheelDump = args[++i]; break;
                case "--wheel-source": if (i + 1 < args.Length) o.WheelSource = args[++i]; break;
                case "--wheel-targets":
                    if (i + 1 < args.Length)
                        o.WheelTargets = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "--wheel-selected":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int ws)) { o.WheelSelected = ws; i++; }
                    break;
                case "--wheel-previews":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int wp)) { o.WheelPreviews = wp; i++; }
                    break;
                case "--wheel-geometry-selftest": o.GeometrySelfTest = true; break;
                case "--wheel-simulate-cycles":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int sc)) { o.WheelSimulateCycles = sc; i++; }
                    break;
                case "--wheel-demo":
                    if (i + 2 < args.Length &&
                        int.TryParse(args[i + 1], out int wx) && int.TryParse(args[i + 2], out int wy))
                    {
                        o.WheelDemoX = wx; o.WheelDemoY = wy; i += 2;
                    }
                    break;
                case "--quiet": o.Quiet = true; break;
                case "--log":
                    if (++i < args.Length) o.LogPath = args[i];
                    break;
                case "--trigger-key":
                    if (++i < args.Length) o.TriggerKey = ParseVirtualKey(args[i]);
                    break;
                case "--cycle-test":
                    if (++i < args.Length && int.TryParse(args[i], out int n) && n > 0) o.CycleTest = n;
                    break;
                case "--watchdog-seconds":
                    if (++i < args.Length && int.TryParse(args[i], out int s) && s > 0) o.WatchdogSeconds = s;
                    break;
            }
        }
        return o;
    }

    /// <summary>接受 0xNN 十六進位或少量具名虛擬鍵；未知值一律退回預設並由呼叫端記錄。</summary>
    public static uint ParseVirtualKey(string text)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex))
            return hex;

        return t.ToUpperInvariant() switch
        {
            "VK_SPACE" => 0x20,
            "VK_TAB" => 0x09,
            "VK_CAPITAL" or "VK_CAPSLOCK" => 0x14,
            "VK_ESCAPE" => 0x1B,
            "VK_RMENU" => 0xA5,
            "VK_F8" => 0x77,
            _ => Program.DefaultTriggerKey
        };
    }
}

/// <summary>組裝與訊息迴圈。業務邏輯一律不放在這裡（收層由 ReleaseGuard 擁有）。</summary>
internal static class App
{
    public static int Run(Options opt)
    {
        using var evidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
        evidence.Line($"Sidecar {Program.VersionText()} starting: trigger=VK 0x{opt.TriggerKey:X2} " +
                      $"dumpMonitors={opt.DumpMonitors} raise={opt.ForceRaiseHitLayer} " +
                      $"cycleTest={opt.CycleTest} assumeExplorerForeground={opt.AssumeExplorerForeground} " +
                      $"log={evidence.Path}");

        try
        {
            return RunCore(opt, evidence);
        }
        catch (Exception ex)
        {
            // 大聲失敗：證據檔與 stderr 都要留痕，而且不得讓 WER 對話框卡住驗收跑者。
            evidence.Line("FATAL: " + ex.ToString().Replace("\r", string.Empty).Replace("\n", " | "));
            Native.AttachParentConsole();
            Console.Error.WriteLine("FATAL: " + ex.Message);
            return 9;
        }
    }

    private static int RunCore(Options opt, Evidence evidence)
    {
        MonitorEntry[] monitors = MonitorLayout.Enumerate();
        evidence.Line($"monitors: {monitors.Length}");

        if (opt.DumpMonitors)
        {
            Native.AttachParentConsole();
            foreach (MonitorEntry m in monitors)
            {
                string line =
                    $"monitor#{m.Index} {m.Device} bounds=({m.Bounds.Left},{m.Bounds.Top},{m.Width}x{m.Height}) " +
                    $"work=({m.Work.Left},{m.Work.Top},{m.Work.Right - m.Work.Left}x{m.Work.Bottom - m.Work.Top}) " +
                    $"primary={m.IsPrimary}";
                evidence.Line(line);
                Console.WriteLine(line);
            }
            return 0;
        }

        using var hit = new HitLayer(evidence, monitors, Native.OverlayAlpha);
        using var guard = new ReleaseGuard(evidence, hit, opt.WatchdogSeconds * 1000);
        var detector = new DragDetector(evidence, opt.TriggerKey, opt.AssumeExplorerForeground);

        hit.FilesDropped += (index, paths) =>
        {
            evidence.Status($"DROP: {paths.Length} file(s) on hit#{index} — M1a records only, no file operation");
            guard.OnDrop();                                    // drop 已解析 → 立即收層
        };

        detector.Captured += guard.OnCaptured;                 // 升起 + 武裝保險 #3
        detector.Released += guard.OnReleaseRequested;         // 保險 #1／#2（含 250 ms 寬限）

        hit.EnsureCreated();      // 預先建立、保持隱藏（規格 §9.3：升起 < 30 ms）

        if (opt.CycleTest > 0) return CycleTest(hit, evidence, opt.CycleTest);

        if (opt.WheelDemoX.HasValue)
            return WheelDemo(hit, guard, evidence, opt);

        // 視覺層同樣**預先建立**：規格 §9.3 要求輪盤首次顯示 < 100 ms，不可在拖曳中才建視窗。
        using var visual = new VisualLayer(evidence);
        var catalog = new StubFormatCatalog();
        var presenter = new WheelPresenter(evidence, visual);

        // 兩層在同一瞬間收起：guard 決定、接收層執行，視覺層只是跟著收（通知，不是決定權）。
        hit.Hidden += _ => presenter.Hide();
        detector.Captured += () => ShowWheel(presenter, catalog, evidence);

        if (opt.WheelSimulateCycles > 0)
            return WheelSimulateCycle(hit, guard, evidence, presenter, catalog, opt.WheelSimulateCycles);

        using var hooks = new Hooks();
        if (opt.ForceRaiseHitLayer)
        {
            evidence.Line("--raise: hooks NOT installed");
            guard.OnCaptured();       // 用真正的保險 #3 計時收層，不用替代品
        }
        else
        {
            hooks.MouseEvent += detector.OnMouse;
            hooks.KeyEvent += (vk, isDown) =>
            {
                if (isDown && vk == Native.VK_Q && IsCtrlAltShiftDown())
                {
                    guard.OnKillSwitch();     // 保險 #5
                    detector.Disable();
                    return;
                }
                // 輪盤可見時，方向鍵／Enter／Esc 屬於輪盤（規格 §4.3 鍵盤表）
                if (isDown && presenter.IsVisible && HandleWheelKey(vk, presenter, guard, evidence))
                    return;
                detector.OnKey(vk, isDown);
            };
            hooks.Install(evidence);
        }

        evidence.Line("message loop starting");
        return RunMessageLoop(evidence);
    }

    /// <summary>保險 #5 的組合鍵判定：Ctrl／Alt／Shift 三個都必須按著才算。</summary>
    private static bool IsCtrlAltShiftDown() =>
        (Native.GetAsyncKeyState((int)Native.VK_CONTROL) & 0x8000) != 0 &&
        (Native.GetAsyncKeyState((int)Native.VK_MENU) & 0x8000) != 0 &&
        (Native.GetAsyncKeyState((int)Native.VK_SHIFT) & 0x8000) != 0;

    /// <summary>
    /// 診斷用：同時升起命中層與視覺層。穿透只有在**兩層都可見**時才驗得出來
    /// （`WindowFromPoint` 只會回可見視窗，藏起來等於驗不出東西）。
    /// </summary>
    private static int WheelDemo(HitLayer hit, ReleaseGuard guard, Evidence evidence, Options opt)
    {
        int x = opt.WheelDemoX!.Value, y = opt.WheelDemoY!.Value;
        evidence.Line($"--wheel-demo: hooks NOT installed; wheel at ({x},{y})");
        guard.OnCaptured();                       // 升起命中層，並用真正的保險 #3 計時

        using var visual = new VisualLayer(evidence);
        var presenter = new WheelPresenter(evidence, visual);
        string[] targets = opt.WheelTargets.Length > 0 ? opt.WheelTargets : new[] { "PNG", "JPG", "WEBP", "BMP" };

        var sw = Stopwatch.StartNew();
        presenter.Show(x, y, opt.WheelSource, targets, opt.WheelSelected, PreviewText(targets, opt));
        sw.Stop();

        evidence.Line($"visual demo shown at ({x},{y}) " +
                      $"size={WheelGeometry.BitmapWidthPx}x{WheelGeometry.BitmapHeightPx} " +
                      $"in {sw.Elapsed.TotalMilliseconds:F1} ms (budget 100 ms)");

        if (opt.WheelDump != null)
        {
            presenter.DumpTo(opt.WheelDump);
            return 0;
        }
        return RunMessageLoop(evidence);
    }

    /// <summary>M1b 的預覽文字只是示範：真正的命名規則屬 M1d 的 <c>NamingPolicy</c>。</summary>
    private static string PreviewText(string[] targets, Options opt)
    {
        if (opt.WheelPreviews > 1) return $"{opt.WheelPreviews} 個檔案";
        int i = Math.Clamp(opt.WheelSelected, 0, targets.Length - 1);
        return $"holiday.{targets[i].ToLowerInvariant()}";
    }

    /// <summary>
    /// 捕捉開始時在游標處叫出輪盤（規格 §4.3）。
    /// 原格式目前一定是未知：拖曳**進行中**的酬載需要 OLE `IDropTarget::DragEnter`（T7），
    /// 在那之前中心只顯示佔位符——這一點一律寫進證據，不用假資料假裝知道。
    /// </summary>
    private static void ShowWheel(WheelPresenter presenter, IFormatCatalog catalog, Evidence evidence)
    {
        Native.GetCursorPos(out Native.POINT p);
        string? source = catalog.SourceFormat(Array.Empty<string>());
        if (source == null)
            evidence.Line("wheel: source format UNKNOWN (drag payload plumbing = T7); placeholder center shown");
        presenter.Show(p.X, p.Y, source ?? "?", catalog.TargetsFor(source));
    }

    /// <summary>輪盤可見時接管方向鍵／Enter／Esc；回傳 true 表示這個鍵已屬於輪盤。</summary>
    private static bool HandleWheelKey(uint vk, WheelPresenter presenter, ReleaseGuard guard, Evidence evidence)
    {
        switch (vk)
        {
            case Native.VK_LEFT or Native.VK_UP:
                presenter.SelectNext(-1);
                return true;
            case Native.VK_RIGHT or Native.VK_DOWN:
                presenter.SelectNext(+1);
                return true;
            case Native.VK_RETURN:
                // M1b 只記錄意圖，不轉檔；真正的執行在 M1d 之後
                evidence.Line($"ENTER: commit intent -> {presenter.SelectedTarget} (M1b records only)");
                guard.OnUserResolved("enter");
                return true;
            case Native.VK_ESCAPE:
                // 規格 §4.3：Esc 立即收起；§4.2.3 的保底語意在 M1d 實作
                evidence.Line("ESC: cancel (fallback semantics land in M1d)");
                guard.OnUserResolved("escape");
                return true;
            default:
                return false;
        }
    }

    /// <summary>把已投遞的訊息跑完直到條件成立（收層是訊息驅動的非同步動作，不跑就不會落地）。</summary>
    private static bool PumpUntil(Func<bool> done, int timeoutMs = 1000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            while (Native.PeekMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
            if (done()) return true;
            Thread.Sleep(5);
        }
        return done();
    }

    /// <summary>
    /// T4 的**非侵入**驗證：不注入任何輸入，直接走與真實拖曳相同的接線
    /// （guard.OnCaptured → ShowWheel → 方向鍵 → Esc → guard.OnUserResolved），
    /// 並以作業系統的真實狀態斷言兩層的顯示與隱藏。
    /// 真實放手（真的拖曳）仍要 S8，那需要滑鼠。
    /// </summary>
    private static int WheelSimulateCycle(HitLayer hit, ReleaseGuard guard, Evidence evidence,
                                          WheelPresenter presenter, IFormatCatalog catalog, int cycles)
    {
        int failed = 0;
        void Report(string text, bool ok)
        {
            if (!ok) failed++;
            evidence.Line($"{(ok ? "PASS" : "FAIL")}  {text}");
        }

        int count = catalog.TargetsFor(null).Count;
        for (int i = 0; i < cycles; i++)
        {
            guard.OnCaptured();
            ShowWheel(presenter, catalog, evidence);
            Report($"wheel sim {i}: wheel visible after capture", presenter.IsVisible);

            int a = presenter.SelectNext(1);
            int b = presenter.SelectNext(1);
            int c = presenter.SelectNext(-1);
            Report($"wheel sim {i}: ring selection (fwd,fwd,back) -> {a},{b},{c}, want 1,2,1", a == 1 && b == 2 && c == 1);

            int start = presenter.Selected;
            for (int k = 0; k < count; k++) presenter.SelectNext(1);
            Report($"wheel sim {i}: wrap-around after {count} steps returns to {start}", presenter.Selected == start);

            guard.OnUserResolved("simulated escape");
            bool hid = PumpUntil(() => !presenter.IsVisible, 1000);
            Report($"wheel sim {i}: wheel hidden by the guard (through hit.Hidden)", hid);
            Report($"wheel sim {i}: both layers hidden", !hit.IsVisible && !presenter.IsVisible);
        }

        evidence.Line($"wheel simulate cycle done: {cycles} cycles; failed={failed}");
        return failed == 0 ? 0 : 5;
    }

    /// <summary>
    /// T2 的自我對照（post-change regression，不是 strict RED）：以**已知角度**驗扇區映射，
    /// 並以已知座標驗翻邊。全數符合回 0，否則回 5 並列出不符項。
    /// </summary>
    internal static int RunGeometrySelfTest(Options opt)
    {
        var cases = new (int Count, double Dx, double Dy, int Expected, string Why)[]
        {
            (4, 0, -100, 0, "count=4 straight up -> 0"),
            (4, 100, -0.01, 0, "count=4 just before the 90deg boundary -> 0"),
            (4, 100, 0.01, 1, "count=4 just after the 90deg boundary -> 1"),
            (4, 100, 100, 1, "count=4 down-right 45deg -> 1"),
            (4, 0, 100, 2, "count=4 straight down -> 2"),
            (4, -100, 0, 3, "count=4 straight left -> 3"),
            (3, 0, -100, 0, "count=3 straight up -> 0"),
            (3, -100, 0, 2, "count=3 straight left -> 2"),
            (2, 0, -100, 0, "count=2 straight up -> 0"),
            (2, 0, 100, 1, "count=2 straight down -> 1"),
            (7, 0, 100, 3, "count=7 straight down -> 3"),
            (1, 500, 500, 0, "count=1 any direction -> 0")
        };

        int failed = 0;
        var out_ = new List<string>();

        void Report(string text, bool ok)
        {
            if (!ok) failed++;
            out_.Add($"{(ok ? "PASS" : "FAIL")}  {text}");
        }

        foreach (var c in cases)
        {
            int got = WheelGeometry.SectorAt(c.Dx, c.Dy, c.Count);
            Report($"{c.Why} (got {got}, want {c.Expected})", got == c.Expected);
        }

        var work = new Native.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1032 };
        var (fx, fy) = WheelGeometry.FlipEdge(2500, 1000, WheelGeometry.BoxPx, work);
        Report($"flip at bottom-right -> ({fx},{fy}), want (2234,734)", fx == 2234 && fy == 734);

        var (gx, gy) = WheelGeometry.FlipEdge(100, 100, WheelGeometry.BoxPx, work);
        Report($"no flip near the top-left -> ({gx},{gy}), want (118,118)", gx == 118 && gy == 118);

        var tiny = new Native.RECT { Left = 0, Top = 0, Right = 200, Bottom = 200 };
        var (hx, hy) = WheelGeometry.FlipEdge(100, 100, WheelGeometry.BoxPx, tiny);
        Report($"clamped when the work area is smaller than the wheel -> ({hx},{hy}), want (0,0)", hx == 0 && hy == 0);

        Report($"wheel box = {WheelGeometry.BoxPx} px (want 248)", WheelGeometry.BoxPx == 248);
        Report($"bitmap = {WheelGeometry.BitmapWidthPx}x{WheelGeometry.BitmapHeightPx} (want 304x330)",
               WheelGeometry.BitmapWidthPx == 304 && WheelGeometry.BitmapHeightPx == 330);

        foreach (var line in out_) Console.WriteLine(line);
        if (opt.LogPath is { Length: > 0 })
        {
            try { File.AppendAllLines(opt.LogPath, out_); } catch (IOException) { /* 記錄失敗不改變判定 */ }
        }
        Console.WriteLine($"geometry selftest: {out_.Count - failed}/{out_.Count} passed");
        return failed == 0 ? 0 : 5;
    }


    /// <summary>程式化收層循環：N 次升起／收起後以**作業系統狀態**斷言全部隱藏。</summary>
    private static int CycleTest(HitLayer hit, Evidence evidence, int cycles)
    {
        for (int i = 0; i < cycles; i++)
        {
            hit.ShowAll();
            Thread.Sleep(30);
            hit.HideAll();
            Thread.Sleep(30);
        }
        Thread.Sleep(150);
        bool hidden = !hit.IsVisible;
        evidence.Line($"cycle test done: {cycles} cycles; allHidden={hidden} (must be True)");
        return hidden ? 0 : 1;
    }

    private static int RunMessageLoop(Evidence evidence)
    {
        while (Native.GetMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        evidence.Line("message loop exited");
        return 0;
    }
}
