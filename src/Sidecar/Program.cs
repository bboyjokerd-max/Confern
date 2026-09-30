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

        // M1c：編解碼能力探測同樣不需要視窗，而且必須能在「單一實例守衛」之前跑
        // （否則已有一個 Sidecar 在跑時，診斷會被守衛擋掉）。
        if (opt.Codecs)
        {
            Native.AttachParentConsole();
            using var probeEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunCodecProbe(probeEvidence, opt.CodecProbeFiles);
        }

        // M1e：worker 行程（規格 §9.1）。它不建視窗、不裝勾子、不碰 UI，只轉檔。
        if (!string.IsNullOrEmpty(opt.WorkerPipe))
        {
            using var workerEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return WorkerServer.Run(opt.WorkerPipe, workerEvidence);
        }

        if (!string.IsNullOrEmpty(opt.ProgressDump))
        {
            Native.AttachParentConsole();
            using var dumpEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunProgressDump(opt.ProgressDump, dumpEvidence);
        }

        // M1f：設定檔的範圍驗證（§8）——超範圍要夾回並說出來，壞檔要退回預設而不是讓程式起不來。
        if (!string.IsNullOrEmpty(opt.SettingsProbe))
        {
            Native.AttachParentConsole();
            using var settingsEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            Settings loaded = SettingsStore.Load(opt.SettingsProbe, settingsEvidence.Line);
            settingsEvidence.Line($"settings probe result: jpgQuality={loaded.JpgQuality} " +
                                  $"webpQuality={loaded.WebpQuality} capture={loaded.CaptureTimeoutSeconds} " +
                                  $"trigger={loaded.TriggerKey} collision={loaded.NamingCollision} " +
                                  $"hotkey={loaded.EmergencyHotkey} diagnostics={loaded.Diagnostics}");
            settingsEvidence.Line("settings probe done: failed=0");
            return 0;
        }

        // M1d：無需滑鼠的整條轉檔管線（解碼 → 編碼 → 原子寫入）。驗證靠它，不靠人工拖曳。
        if (!string.IsNullOrEmpty(opt.ConvertPayload))
        {
            Native.AttachParentConsole();
            using var convertEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunConvert(opt, convertEvidence);
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
          --wheel-hover-selftest
                                    feed each sector's own mid-angle point into the hover mapping and exit
          --codecs                  probe which formats this machine can really encode/decode, then exit
          --codec-probe <path>      also decode this real file (repeatable) and report the outcome
          --convert <p1;p2>         convert these files (headless, no mouse needed) and report the outcome
          --target <FORMAT>         target format for --convert (default PNG)
          --quality <60-100>        JPG/WebP quality for --convert (default 85)
          --worker <pipeName>       run as the conversion worker and connect back to this pipe (spec 9.1)
          --progress-dump <dir>     render the progress line to PNGs (verifies glyphs without a screen)
          --wheel-simulate-cycles <N>
                                    run N non-invasive capture/show/select/escape cycles and exit
          --wheel-payload <p1;p2;...>
                                    inject a payload into the post-DragEnter path (diagnostic) and exit
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
    public bool WheelHoverSelfTest;
    public bool Codecs;
    /// <summary>--codec-probe 指定的真實檔案（可重複），用來量測解碼能力。</summary>
    public List<string> CodecProbeFiles = new();
    /// <summary>--convert 的來源（以 `;` 分隔）；不需要滑鼠就能驗證整條轉檔管線。</summary>
    public string ConvertPayload = "";
    public string ConvertTarget = "PNG";
    public int ConvertQuality = 85;
    /// <summary>--worker <pipeName>：以 worker 行程啟動，連回 UI 建立的具名管道（規格 §9.1）。</summary>
    public string WorkerPipe = "";
    /// <summary>--progress-dump <dir>：把進度那一行畫成 PNG，用來驗證字形與版面（不必有畫面）。</summary>
    public string ProgressDump = "";
    /// <summary>--settings-probe <path>：載入指定的設定檔並印出夾制後的結果（驗證 §8 的範圍規則）。</summary>
    public string SettingsProbe = "";
    public int WheelSimulateCycles;

    /// <summary>
    /// 診斷用：把假的酬載直接餵進「真實 DragEnter 之後」的處理路徑（分號分隔的路徑清單）。
    /// 它**不**驗證 OLE 管線本身（那需要真實拖曳），只驗證拿到酬載之後的行為。
    /// </summary>
    public string? WheelPayload;

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
                case "--wheel-hover-selftest": o.WheelHoverSelfTest = true; break;
                case "--codecs": o.Codecs = true; break;
                case "--codec-probe":
                    if (i + 1 < args.Length) { o.CodecProbeFiles.Add(args[i + 1]); i++; }
                    break;
                case "--convert":
                    if (i + 1 < args.Length) { o.ConvertPayload = args[i + 1]; i++; }
                    break;
                case "--target":
                    if (i + 1 < args.Length) { o.ConvertTarget = args[i + 1]; i++; }
                    break;
                case "--quality":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int q)) { o.ConvertQuality = q; i++; }
                    break;
                case "--worker":
                    if (i + 1 < args.Length) { o.WorkerPipe = args[i + 1]; i++; }
                    break;
                case "--progress-dump":
                    if (i + 1 < args.Length) { o.ProgressDump = args[i + 1]; i++; }
                    break;
                case "--settings-probe":
                    if (i + 1 < args.Length) { o.SettingsProbe = args[i + 1]; i++; }
                    break;
                case "--wheel-payload":
                    if (i + 1 < args.Length) { o.WheelPayload = args[i + 1]; i++; }
                    break;
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

        // OLE 拖放必須先初始化，否則 RegisterDragDrop 會回 CO_E_NOTINITIALIZED。
        // 主執行緒有 [STAThread]，所以不會撞 RPC_E_CHANGED_MODE。結果一律寫進證據——失敗時
        // 輪盤會退回「原格式未知」的佔位符，而不是靜靜地少一半功能。
        int hrOle = OleInterop.OleInitialize(IntPtr.Zero);
        evidence.Line($"OleInitialize -> {OleInterop.HResultText(hrOle)}");
        if (hrOle != 0 && hrOle != 1)
            evidence.Line("WARNING: OLE 未初始化 → 拖曳途中的酬載拿不到，輪盤只會顯示佔位符");

        hit.FilesDropped += (index, paths) =>
        {
            evidence.Status($"DROP: {paths.Length} file(s) on hit#{index} — M1a records only, no file operation");
            guard.OnDrop();                                    // drop 已解析 → 立即收層
        };

        detector.Captured += guard.OnCaptured;                 // 升起 + 武裝保險 #3
        // 放手的處置掛在後面（需要 presenter／runner）；見「release(...)」那段

        hit.EnsureCreated();      // 預先建立、保持隱藏（規格 §9.3：升起 < 30 ms）

        if (opt.CycleTest > 0) return CycleTest(hit, evidence, opt.CycleTest);

        if (opt.WheelDemoX.HasValue)
            return WheelDemo(hit, guard, evidence, opt);

        // 視覺層同樣**預先建立**：規格 §9.3 要求輪盤首次顯示 < 100 ms，不可在拖曳中才建視窗。
        using var visual = new VisualLayer(evidence);
        // 進度層（規格 §4.4）同樣預先建立：轉檔開始時不可以再多花一次建視窗的成本，
        // 而且它必須與輪盤**分開**——輪盤收起後那一行進度還要留在原位置。
        using var progressLayer = new VisualLayer(evidence);
        using var progress = new ProgressPresenter(evidence, progressLayer);
        // runner 一定要釋放：它擁有 worker 行程與管道（Dispose 會送 shutdown 並確保 worker 收掉）
        using var runner = new ConversionRunner(Environment.ProcessPath ?? "Sidecar.exe", evidence.Line, progress);
        // 格式能力探測也要**預先做**：第一次使用 WPF/WIC 會載入 PresentationCore，
        // 若等到酬載進來才做，那筆成本會直接落在「酬載 → 顯示」的 100 ms 預算上。
        var catalog = BuildCatalog(evidence);
        var presenter = new WheelPresenter(evidence, visual);

        // M1f：設定（規格 §8）——預設值即規格值，檔案壞掉／超範圍都會退回並說明，不靜默。
        _settings = SettingsStore.Load(null, evidence.Line);

        // 規格 §7.1：上次崩潰／斷電留下的 `.tmp-*` 必須在啟動時清掉，並且**說出來**。
        int recoveredTemps = new TempJournal(TempJournal.DefaultPath()).RecoverStale();
        evidence.Line($"startup temp recovery: removed={recoveredTemps} stale .tmp-* file(s) " +
                      $"(spec 7.1: no leftovers after a crash)");

        // 兩層由**同一個權威**廣播收起。先前是「命中層先收、視覺層跟著收（hit.Hidden）」，
        // 那個順序在命中層沒升起時永遠不會發生 —— 輪盤就會卡在畫面上（最壞的失效模式）。
        guard.AddOverlay(() => visual.IsVisible, visual.RequestHide);

        // 拖曳途中拿到酬載（規格 §4.3）→ 這才是輪盤顯示真實原格式的路徑。
        hit.PayloadObserved += (_, paths) => ShowWheelFromPayload(presenter, catalog, evidence, paths);

        // 規格 §4.3「選好放手就轉檔」：**只有滑鼠放手**才執行轉檔。
        // 觸發鍵放開是「收起輪盤」（§4.2.2 保險 #2），**不是**執行——這兩件事語意不同，
        // 混在一起會變成「使用者只是放開空白鍵，檔案卻被轉了」。
        // （低階 hook 會在 shell 的拖放迴圈之前看到 mouse-up，所以這條是主要路徑；
        //   OLE drop 那條是交叉確認，且收層是幂等的，不會重複轉檔。）
        detector.Released += reason =>
        {
            bool mouseRelease = reason.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0;
            if (mouseRelease && presenter.IsVisible && presenter.CanCommit)
            {
                evidence.Line($"release({reason}): wheel visible with a valid selection -> commit (spec 4.3)");
                CommitWheel(presenter, guard, evidence, runner);
                return;
            }
            guard.OnReleaseRequested(reason);      // 保險 #1／#2（含 250 ms 寬限）
        };

        // OLE 路徑確認的放手：與 WM_DROPFILES 交叉確認。收層是幂等的，不會出現兩次收層。
        // 規格 §4.3 的「選好放手就轉檔」在這裡執行——這是**最自然的使用者動作**，
        // 少了它，使用者拖完、選完、放手卻什麼都沒發生（M1b 時期只記錄意圖，M1d 起真的執行）。
        hit.OleDropObserved += _ =>
        {
            if (presenter.IsVisible && presenter.CanCommit)
                CommitWheel(presenter, guard, evidence, runner);
            guard.OnDrop();
        };

        // 規格 §4.3：扇區可直接點擊 → 拖曳途中游標移到哪個扇區就選哪個。
        // 拖放迴圈會吃掉滑鼠訊息，所以座標只有 DragOver 拿得到。
        hit.DragOverObserved += (_, x, y) => presenter.SelectAt(x, y);

        // 啟發式（觸發鍵按著 + 移動超過門檻）只在輪盤尚未由酬載顯示時，才用佔位符把它叫出來。
        detector.Captured += () => { if (!presenter.IsVisible) ShowWheel(presenter, catalog, evidence); };

        if (opt.WheelHoverSelfTest)
            return WheelHoverSelfTest(presenter, catalog, evidence, opt);

        if (opt.WheelSimulateCycles > 0)
            return WheelSimulateCycle(hit, guard, evidence, presenter, catalog, opt.WheelSimulateCycles);

        if (opt.WheelPayload != null)
        {
            string[] injected = opt.WheelPayload.Split(';', StringSplitOptions.RemoveEmptyEntries);
            evidence.Line($"*** INJECTED PAYLOAD (diagnostic; NOT a real DragEnter): {injected.Length} path(s) ***");
            // 依生產順序模擬：先捕捉（升起接收層），酬載才可能到達。
            guard.OnCaptured();
            ShowWheelFromPayload(presenter, catalog, evidence, injected);

            // 拒絕狀態（混合選取／不支援）下 Enter 必須不執行，而且輪盤要留在畫面上等 Esc。
            if (opt.WheelDump != null) presenter.DumpTo(opt.WheelDump);
            bool committed = CommitWheel(presenter, guard, evidence, runner);
            if (committed) PumpUntil(() => !presenter.IsVisible, 500);
            evidence.Line($"payload enter attempt: committed={committed} wheelVisibleAfter={presenter.IsVisible}");

            // 規格 §4.3：選中項下方顯示輸出檔名 → 換扇區時預覽必須跟著換。
            for (int step = 1; step <= 2; step++)
            {
                int idx = presenter.SelectNext(1);
                evidence.Line($"payload select#{step}: index={idx} target={presenter.SelectedTarget} " +
                              $"preview={presenter.CurrentPreview ?? "-"}");
            }

            // M1e：這條注入路徑**真的會轉檔**（透過 worker 行程），所以它就是 UI 端最完整的自動驗證：
            // 進度那一行、淡出、部分失敗摘要都能在這裡被斷言，不需要滑鼠。
            if (committed)
            {
                ConversionSnapshot final = runner.Wait(60000);
                evidence.Line($"conversion finished: finished={final.Finished} ok={final.Ok} failed={final.Failed} " +
                              $"error={final.Error ?? "-"} line='{final.Line}'");
                evidence.Line($"progress instrument: updates={progress.Updates} frames={progress.Frames} " +
                              $"visible={progress.IsVisible} (spec 4.4: one line, updated in place)");
                bool faded = PumpUntil(() => !progress.IsVisible, 12000);
                evidence.Line($"progress faded out on its own: {faded} visible={progress.IsVisible}");

                // M1d：**放手即轉檔**（規格 §4.3）是使用者最自然的動作，但它需要真的滑鼠手勢，
                // 自動測試碰不到。這裡用 detector.ForceRelease("mouse up") 走**同一條** Released
                // 事件，至少證明「滑鼠放手 → 執行轉檔」這段接線是通的。
                // 真實手勢仍必須人工確認（這條只保證接線，不保證 Windows 真的把 mouse-up 送給我們）。
                guard.OnCaptured();                       // 接收層再次升起（＝第二次拖曳）
                ShowWheelFromPayload(presenter, catalog, evidence, injected);
                int sel = presenter.SelectNext(1);        // 換一個扇區，輸出才不會撞名
                evidence.Line($"release-path: selected index={sel} target={presenter.SelectedTarget}");
                detector.ForceRelease("mouse up");        // 走真正的 Released 事件，不是替代品
                ConversionSnapshot after = runner.Wait(60000);
                evidence.Line($"release commit finished: finished={after.Finished} ok={after.Ok} " +
                              $"failed={after.Failed} line='{after.Line}'");
                PumpUntil(() => !progress.IsVisible, 12000);
            }
            return 0;
        }

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
                if (isDown && presenter.IsVisible && HandleWheelKey(vk, presenter, guard, evidence, runner))
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
        var catalog = BuildCatalog(evidence);
        // 示範用的目標清單同樣來自**實測能力**（而且原格式自動排除）——
        // 示範畫面一樣不可以承諾做不到的事。
        string[] targets = opt.WheelTargets.Length > 0
            ? opt.WheelTargets
            : catalog.TargetsFor(opt.WheelSource).ToArray();

        var sw = Stopwatch.StartNew();
        presenter.Show(x, y, opt.WheelSource, targets, opt.WheelSelected,
                       PreviewFactory(targets, catalog, new[] { $"holiday.{opt.WheelSource.ToLowerInvariant()}" },
                                      opt.WheelPreviews));
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

    /// <summary>
    /// 建立格式目錄，並把**實測能力與探測成本**寫進證據檔。
    /// 成本必須看得見：它若悄悄變大，就會吃掉規格 §9.3 的「首次顯示 &lt; 100 ms」預算，
    /// 而那種退化在事後只看延遲數字是查不出原因的。
    /// </summary>
    private static FormatCatalog BuildCatalog(Evidence evidence)
    {
        var sw = Stopwatch.StartNew();
        var catalog = new FormatCatalog(new CodecRegistry());
        sw.Stop();
        evidence.Line($"codec catalog ready in {sw.Elapsed.TotalMilliseconds:F1} ms; " +
                      $"encodable=[{string.Join(",", catalog.Codecs.EncodableFormats)}] " +
                      $"heifExtension={CodecRegistry.HeifExtensionState()}");
        return catalog;
    }

    /// <summary>
    /// 檔名預覽的產生器（以選中索引為輸入，所以切扇區時輸出檔名會跟著更新）。
    ///
    /// M1b 的命名只是示範：真正的命名規則（`name (1).png` 衝突處理）屬 M1d 的 `NamingPolicy`。
    /// </summary>
    private static Func<int, string?> PreviewFactory(IReadOnlyList<string> targets, IFormatCatalog catalog,
                                                     string[] paths, int forcedCount)
    {
        if (forcedCount > 1) return _ => $"{forcedCount} 個檔案";
        if (paths.Length > 1) return _ => $"{paths.Length} 個檔案";
        if (paths.Length == 0) return _ => null;

        string stem = Path.GetFileNameWithoutExtension(paths[0]);
        if (string.IsNullOrEmpty(stem)) return _ => null;
        return i => targets.Count == 0 ? stem : $"{stem}.{catalog.ExtensionOf(targets[i])}";
    }

    /// <summary>
    /// 拖曳**途中**拿到酬載 → 叫出輪盤（規格 §4.3：第一個 `DragEnter` 後 ≤100 ms）。
    /// 這是輪盤唯一能顯示真實 `原格式` 的路徑；先前只有 DragDetector 的 Capturing 啟發式。
    /// </summary>
    private static void ShowWheelFromPayload(WheelPresenter presenter, IFormatCatalog catalog,
                                             Evidence evidence, string[] paths)
    {
        var sw = Stopwatch.StartNew();
        Native.GetCursorPos(out Native.POINT p);

        PayloadVerdict verdict = catalog.Classify(paths);
        PendingPaths = paths;      // Enter 要用這一批（規格 §4.4：同一類別就全部轉）
        // 真實拖曳的檔案清單必須留證據（M1d 的批次輸出要用它；也讓「讀到什麼」可回溯）。
        // 格式與 WM_DROPFILES 路徑一致，S8 才能斷言「這一批就是被拖的那個檔」。
        foreach (string path in paths) evidence.Line($"    payload file: {path}");
        string? notice = verdict.Status switch
        {
            PayloadStatus.Mixed => "混合選取，請選同類別",       // 規格 §4.3 的原文
            PayloadStatus.Unsupported => "不支援的格式",
            _ => null
        };

        IReadOnlyList<string> targets = notice == null
            ? catalog.TargetsFor(verdict.SourceFormat)
            : Array.Empty<string>();
        if (notice == null && targets.Count == 0) notice = "沒有可轉換的目標格式";

        presenter.Show(p.X, p.Y, verdict.SourceFormat ?? "?", targets, 0,
                       PreviewFactory(targets, catalog, paths, 1), notice);
        sw.Stop();
        evidence.Line($"wheel from OLE payload: files={paths.Length} status={verdict.Status} " +
                      $"source={verdict.SourceFormat ?? "UNKNOWN"} targets={targets.Count} " +
                      $"notice={(notice ?? "-")} latency={sw.Elapsed.TotalMilliseconds:F1} ms (budget 100 ms)");
    }

    /// <summary>
    /// Enter：只有「有選項且不在拒絕狀態」才執行（規格 §4.3：混合選取要拒絕、只允許 Esc）。
    /// M1d 起這裡是**真的會轉檔**：把輪盤的選擇交給 worker 行程，進度顯示在輪盤收起處（§4.4）。
    /// </summary>
    private static bool CommitWheel(WheelPresenter presenter, ReleaseGuard guard, Evidence evidence,
                                    ConversionRunner? runner = null)
    {
        if (!presenter.CanCommit)
        {
            evidence.Line($"ENTER refused: notice={(presenter.Notice ?? "-")} " +
                          $"target={(presenter.SelectedTarget ?? "-")} (spec 4.3)");
            return false;
        }

        string target = presenter.SelectedTarget!;
        string[] paths = PendingPaths;
        var (centerX, centerY) = presenter.CenterOnScreen;
        evidence.Line($"ENTER: commit -> {target}, files={paths.Length}, " +
                      $"wheelCenter=({centerX},{centerY}) (real conversion via worker, spec 9.1)");

        guard.OnUserResolved("enter");      // 收起輪盤（收層是幂等的）

        if (runner == null)
        {
            evidence.Line("ENTER: no conversion runner in this mode (diagnostic path)");
            return true;
        }
        if (paths.Length == 0)
        {
            evidence.Line("ENTER: no payload paths recorded -> nothing to convert");
            return true;
        }

        Settings settings = _settings ?? new Settings();
        runner.Start(paths, target, settings.QualityFor(target), centerX, centerY);
        PendingPaths = Array.Empty<string>();
        return true;
    }

    /// <summary>最近一次拖曳／注入的檔案清單——Enter 要靠它才知道要轉哪些檔。</summary>
    private static string[] PendingPaths = Array.Empty<string>();

    /// <summary>啟動時載入的設定（規格 §8）；null 代表還沒載入。</summary>
    private static Settings? _settings;

    /// <summary>
    /// 把進度那一行畫成 PNG（不需要畫面）。✓／✗ 這類符號若字型缺字會變成方框，
    /// 只讀程式碼看不出來，所以要有辦法把它輸出成看得見的檔案。
    /// </summary>
    internal static int RunProgressDump(string directory, Evidence evidence)
    {
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex)
        {
            evidence.Line($"progress-dump: cannot create {directory}: {ex.Message}");
            return 10;
        }

        (string Name, string Text, int Done, int Total, bool Finished, int Failed, int Alpha)[] variants =
        {
            ("running",  "3/8",        3, 8, false, 0, 255),
            ("success",  "✓ 8",        8, 8, true,  0, 255),
            ("partial",  "✓ 8 / ✗ 2",  10, 10, true, 2, 255),
            ("error",    "轉檔逾時",    1, 3, true,  2, 255),
            ("faded",    "✓ 8",        8, 8, true,  0, 80),
        };

        int failed = 0;
        foreach (var v in variants)
        {
            string path = Path.Combine(directory, $"progress-{v.Name}.png");
            using Bitmap bitmap = ProgressPresenter.Compose(v.Text, v.Done, v.Total, v.Finished, v.Failed, v.Alpha);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            long size = new FileInfo(path).Length;
            bool ok = size > 500;      // 空白圖會小很多（背景透明且沒有文字）
            if (!ok) failed++;
            evidence.Line($"{(ok ? "PASS" : "FAIL")}  progress '{v.Text}' rendered {bitmap.Width}x{bitmap.Height} " +
                          $"-> {path} ({size}B)");
        }

        evidence.Line($"progress dump done: variants={variants.Length} failed={failed}");
        return failed == 0 ? 0 : 7;
    }

    /// <summary>
    /// M1d：不需要滑鼠就能跑完整條轉檔管線——**驗證必須靠這個，不是靠人工拖曳**。
    /// 同時也是使用者可用的「命令列轉檔」入口（明天測試時很好用）。
    /// </summary>
    internal static int RunConvert(Options opt, Evidence evidence)
    {
        var registry = new CodecRegistry();
        var journal = new TempJournal(TempJournal.DefaultPath());
        int recovered = journal.RecoverStale();
        evidence.Line($"temp journal: recovered {recovered} stale temp file(s) " +
                      $"(spec 7.1: crash/power loss must not leave .tmp-* behind)");

        var engine = new ConversionEngine(registry, evidence.Line);
        string[] paths = opt.ConvertPayload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string target = opt.ConvertTarget.ToUpperInvariant();

        evidence.Line($"convert job: files={paths.Length} target={target} quality={opt.ConvertQuality} " +
                      $"offered=[{string.Join(",", registry.OfferedOutputs)}]");

        var results = new List<ConversionResult>();
        foreach (string path in paths)
        {
            ConversionResult result = engine.Convert(path, target, opt.ConvertQuality, journal);
            results.Add(result);
            evidence.Line(result.Ok
                ? $"convert result: Ok source={result.SourcePath} output={result.OutputPath} detail={result.Detail}"
                : $"convert result: Fail error={result.Error} source={result.SourcePath} " +
                  $"message={result.UserMessage} detail={result.Detail}");
        }

        var summary = new ConversionSummary(results);
        evidence.Line($"convert summary: {summary.Line}  (ok={summary.Ok} failed={summary.Failed})");
        return summary.AllOk ? 0 : 9;
    }

    /// <summary>
    /// M1c C1：把「這台機器上到底能做什麼」量出來並印清楚。
    /// 每一項都要說明是怎麼量到的——探測本身出錯會悄悄少掉扇區，那比多一個扇區更危險。
    /// </summary>
    internal static int RunCodecProbe(Evidence evidence, IReadOnlyList<string> probeFiles)
    {
        int failed = 0;
        void Report(string text, bool ok)
        {
            if (!ok) failed++;
            evidence.Line($"{(ok ? "PASS" : "FAIL")}  {text}");
        }

        // 成本必須看得見：它若悄悄變大，就會吃掉規格 §9.3 的「首次顯示 < 100 ms」預算。
        var probeWatch = Stopwatch.StartNew();
        var registry = new CodecRegistry();
        probeWatch.Stop();
        evidence.Line($"codec probe cost: {probeWatch.Elapsed.TotalMilliseconds:F1} ms " +
                      "(builtin encoders exercised + capability table)");
        foreach (CodecCapability c in registry.Capabilities)
            evidence.Line($"codec {c.Format}: decode={c.Decode.ToString().ToLowerInvariant()} " +
                          $"encode={(c.CanEncode ? "yes" : "no")} :: {c.Evidence}");

        // 這五個是 Windows 內建的 WIC 編碼器：在這台機器上失敗就是真的失敗，不是儀器的問題
        string[] builtin = { "PNG", "JPG", "BMP", "TIF", "GIF" };
        foreach (string format in builtin)
        {
            CodecCapability? c = registry.Capability(format);
            Report($"codec {format} encode+decode round-trip",
                   c is { CanEncode: true, Decode: DecodeSupport.Yes });
        }

        // 實際提供的輸出＝宣告 ∩ 實測。三條不變式都要成立，而且各自都能失敗：
        //   ① offered ⊆ declared（多一個就是承諾了規格沒宣告的東西）
        //   ② offered ⊆ measured（多一個就是承諾了機器做不到的事）
        //   ③ declared = offered ∪ unavailable（宣告的每一個都必須有交代，不能默默消失）
        IReadOnlyList<string> encodable = registry.EncodableFormats;
        IReadOnlyList<string> offered = registry.OfferedOutputs;
        string[] declared = { "PNG", "JPG", "WEBP", "BMP", "TIF" };
        Report($"offered [{string.Join(",", offered)}] is a subset of the declared MVP matrix", offered.All(declared.Contains));
        Report($"offered [{string.Join(",", offered)}] is a subset of what was measured", offered.All(encodable.Contains));
        Report($"every declared output is either offered or reported unavailable " +
               $"(unavailable=[{string.Join(",", registry.DeclaredButUnavailable)}])",
               declared.All(f => offered.Contains(f) || registry.DeclaredButUnavailable.Contains(f)));

        // 能力上可行、但規格沒宣告的：列出來，不准默默變成扇區
        Report($"capable-but-not-declared outputs stay out of the wheel " +
               $"([{string.Join(",", registry.CapableButNotDeclared)}])",
               registry.CapableButNotDeclared.All(f => !offered.Contains(f)));

        // 儀器誠實性：探測必須能**回報失敗**。用一個保證不存在的路徑當反向對照——
        // 「永遠回報 Ok」的探測器比沒有探測器更危險（它會讓每個扇區都看起來可用）。
        string missing = Path.Combine(Path.GetTempPath(), $"sidecar-missing-{Guid.NewGuid():N}.png");
        DecodeOutcome missingOutcome = registry.ProbeDecode(missing);
        Report($"decode probe reports a missing file as Unreadable (got {missingOutcome.Status})",
               missingOutcome.Status == DecodeStatus.Unreadable);

        // 真實檔案的解碼探測（測量，不是斷言）：HEIC 能不能解、WebP 能不能解，只有量了才知道
        foreach (string file in probeFiles)
        {
            DecodeOutcome outcome = registry.ProbeDecode(file);
            evidence.Line($"decode probe: status={outcome.Status} format={outcome.Format} " +
                          $"file={file} :: {outcome.Detail}");
        }

        evidence.Line($"codec probe summary: decodable=[{string.Join(",", registry.DecodableFormats)}] " +
                      $"encodable=[{string.Join(",", encodable)}] " +
                      $"offered=[{string.Join(",", offered)}] " +
                      $"capable-not-declared=[{string.Join(",", registry.CapableButNotDeclared)}] " +
                      $"declared-unavailable=[{string.Join(",", registry.DeclaredButUnavailable)}] " +
                      $"inputs=[{string.Join(",", registry.InputFormats)}] " +
                      $"heifExtension={CodecRegistry.HeifExtensionState()}");
        evidence.Line($"codec probe done: failed={failed}");
        return failed == 0 ? 0 : 7;
    }

    /// <summary>
    /// T4（滑鼠半邊）的自我對照：由**幾何反推**每個扇區內部的一個點，餵進 <see cref="WheelPresenter.SelectAt"/>，
    /// 檢查回傳索引等於該扇區，並檢查兩條邊界規則（中心圓盤、輪盤外都不得改變選取）。
    /// 真實的滑鼠 hover 走 OLE `DragOver`（只有 S8 能驗），這裡驗的是「座標 → 扇區」這條映射本身。
    /// </summary>
    private static int WheelHoverSelfTest(WheelPresenter presenter, IFormatCatalog catalog,
                                          Evidence evidence, Options opt)
    {
        int failed = 0;
        void Report(string text, bool ok)
        {
            if (!ok) failed++;
            evidence.Line($"{(ok ? "PASS" : "FAIL")}  {text}");
        }

        int cx = opt.WheelDemoX ?? 900;
        int cy = opt.WheelDemoY ?? 400;
        IReadOnlyList<string> targets = catalog.TargetsFor("HEIC");
        presenter.Show(cx, cy, "HEIC", targets, 0);
        Report($"wheel shown with {targets.Count} sectors", presenter.IsVisible && targets.Count >= 2);

        var (scx, scy) = presenter.CenterOnScreen;
        double r = WheelGeometry.DiameterPx / 2.0 * 0.66;      // 與標籤同一個半徑

        for (int i = 0; i < targets.Count; i++)
        {
            double mid = WheelGeometry.SectorMidDeg(i, targets.Count) * Math.PI / 180.0;
            int px = (int)Math.Round(scx + Math.Cos(mid) * r);
            int py = (int)Math.Round(scy + Math.Sin(mid) * r);
            int got = presenter.SelectAt(px, py);
            Report($"hover sector {i} ({targets[i]}) at ({px},{py}) -> index {got}", got == i);
        }

        int before = presenter.Selected;
        int atCenter = presenter.SelectAt(scx, scy);
        Report($"centre disc does not move the selection (got {atCenter})",
               atCenter == -1 && presenter.Selected == before);

        int outside = presenter.SelectAt(scx + WheelGeometry.DiameterPx, scy + WheelGeometry.DiameterPx);
        Report($"outside the wheel does not move the selection (got {outside})",
               outside == -1 && presenter.Selected == before);

        double lastMid = WheelGeometry.SectorMidDeg(targets.Count - 1, targets.Count) * Math.PI / 180.0;
        int lastIdx = presenter.SelectAt((int)Math.Round(scx + Math.Cos(lastMid) * r),
                                         (int)Math.Round(scy + Math.Sin(lastMid) * r));
        int wrap = presenter.SelectNext(1);
        Report($"ring wraps after the last sector ({lastIdx} -> {wrap})",
               lastIdx == targets.Count - 1 && wrap == 0);

        evidence.Line($"wheel hover self-test done: failed={failed}");
        return failed == 0 ? 0 : 6;
    }

    /// <summary>
    /// 捕捉開始時在游標處叫出輪盤（規格 §4.3）。
    /// 若 OLE 酬載還沒到（或 OLE 未初始化），原格式是**未知佔位符**——一律寫進證據，不用假資料假裝知道。
    /// </summary>
    private static void ShowWheel(WheelPresenter presenter, IFormatCatalog catalog, Evidence evidence)
    {
        Native.GetCursorPos(out Native.POINT p);
        string? source = catalog.SourceFormat(Array.Empty<string>());
        if (source == null)
            evidence.Line("wheel: source format UNKNOWN (no OLE payload yet); placeholder center shown");
        presenter.Show(p.X, p.Y, source ?? "?", catalog.TargetsFor(source));
    }

    /// <summary>輪盤可見時接管方向鍵／Enter／Esc；回傳 true 表示這個鍵已屬於輪盤。</summary>
    private static bool HandleWheelKey(uint vk, WheelPresenter presenter, ReleaseGuard guard, Evidence evidence,
                                       ConversionRunner runner)
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
                // M1d 起：真的轉檔（交給 worker 行程）
                CommitWheel(presenter, guard, evidence, runner);
                return true;
            case Native.VK_ESCAPE:
                // 規格 §4.3：Esc 立即收起（已開始的轉檔不因此中斷：取消是另外一個語意）
                evidence.Line("ESC: cancel (wheel dismissed; a running job keeps going)");
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
