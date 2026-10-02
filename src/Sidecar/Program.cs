using System.Collections.Concurrent;
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

        // 規格 §7.3 的失敗清單 dump。放在 `--convert` **之前**：清單的列資料要能來自
        // 一次真的轉檔失敗，而那種轉檔不需要畫面、也不需要滑鼠——這條路徑本身就是驗收手段。
        //
        // ⚠ 但**帶 `--wheel-payload` 時不在這裡處理**：那一條要走完整條 UI 路徑
        //   （worker 行程、管道、快照），它的列資料才驗得到「跨管道之後還拆得對」。
        //   若在這裡先 dump，行程會直接結束，那條路徑永遠跑不到——而驗收還是會看到
        //   「有圖、有列」，只是那些列來自 `--convert` 而不是管道。那是靜默的假通過。
        if (!string.IsNullOrEmpty(opt.WheelPayload))
        {
            // fall through: the payload path dumps the panel itself (see DumpDetailPanels)
        }
        else if (!string.IsNullOrEmpty(opt.ProgressDumpDetail) || !string.IsNullOrEmpty(opt.ProgressDumpDetailRaw)
                 || !string.IsNullOrEmpty(opt.ProgressDumpDetailJson))
        {
            Native.AttachParentConsole();
            using var detailEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunProgressDumpDetail(opt, detailEvidence);
        }

        // 規格 §7.3 的行為自測（展開狀態與命中矩形）。它需要一個視覺層實例（藥丸畫在哪裡、
        // 矩形就是什麼），但不需要升起任何看得見的視窗。
        if (opt.ProgressDetailSelfTest)
        {
            Native.AttachParentConsole();
            using var detailEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunProgressDetailSelfTest(detailEvidence);
        }

        // M1f：改設定並存檔（規格 §8）。設定頁按「儲存」走的就是同一條路，
        // 所以這條路必須能被無頭驗證——否則設定頁的驗證只能靠人眼看。
        if (!string.IsNullOrEmpty(opt.SettingsSet))
        {
            Native.AttachParentConsole();
            using var setEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunSettingsSet(opt, setEvidence);
        }

        // M1f：把設定頁畫成一張 PNG（設計審查用；也讓「WPF 在這個行程裡真的畫得出來」有檔案可查）。
        if (!string.IsNullOrEmpty(opt.SettingsUiDump))
        {
            Native.AttachParentConsole();
            using var dumpEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunSettingsUiDump(opt, dumpEvidence);
        }

        // M1f：設定頁單獨開啟（規格 §8）——不啟動覆蓋層，關掉視窗就結束。
        if (opt.SettingsUi)
        {
            Native.AttachParentConsole();
            using var pageEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunSettingsUiStandalone(opt, pageEvidence);
        }

        // M1f：設定頁的生命週期自測（規格 §8）。設定頁跑在自己的 WPF 執行緒上，最現實的失效模式是
        // 「開得起來但關不掉／行程結束不掉」——人眼看不到，卻會讓程式無法退出。所以要有這條路。
        if (opt.SettingsUiSelfTest)
        {
            Native.AttachParentConsole();
            using var uiEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunSettingsUiSelfTest(opt, uiEvidence);
        }

        // M1f：設定檔的範圍驗證（§8）——超範圍要夾回並說出來，壞檔要退回預設而不是讓程式起不來。
        if (!string.IsNullOrEmpty(opt.SettingsProbe))
        {
            Native.AttachParentConsole();
            using var settingsEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            Settings loaded = SettingsStore.Load(opt.SettingsProbe, settingsEvidence.Line);
            settingsEvidence.Line($"settings probe result: {SettingsStore.Describe(loaded)}");
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

        // M1g：托盤圖示的自測（待辦 4）。與設定頁的自測一樣必須在單一實例守衛之前——
        // 否則「已經有一個 Confern 在跑」時，診斷會被守衛擋掉，而這正是使用者最有機會
        // 想跑它的時候。
        if (opt.TraySelfTest)
        {
            Native.AttachParentConsole();
            using var trayEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunTraySelfTest(opt, trayEvidence);
        }

        // M1g：開機自動啟動的探測（規格 §8 的 StartWithWindows）。**只碰指定的子機碼**，
        // 永不碰真正的 Run 機碼——理由見 Autostart 的類別註解。
        if (!string.IsNullOrEmpty(opt.AutostartProbe))
        {
            Native.AttachParentConsole();
            using var startupEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunAutostartProbe(opt.AutostartProbe, startupEvidence);
        }

        // 單一實例守衛。A13 要求「被強制結束後能立刻重啟」，
        // 因此這裡不留下任何需要清理的檔案鎖。
        using var mutex = new Mutex(initiallyOwned: true, $@"Local\{ProductInfo.Name}.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Native.AttachParentConsole();
            Console.Error.WriteLine($"{ProductInfo.Name} is already running.");
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
        Confern (M1a)
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
          --wheel-dump-raw <path>   with --wheel-demo: same frame but with alpha preserved
                                    (the only way to check the alpha contract: the glass body
                                    must be alpha 255, everything outside must be a pure layer)
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
          --progress-dump-detail <png>
                                    render the spec 7.3 failure list (filename + reason) to a PNG and exit;
                                    with --convert the rows come from a real failing conversion, and with
                                    --wheel-payload they come from the worker over the pipe (full UI path)
          --progress-dump-detail-raw <png>
                                    same panel with alpha preserved (the file whose pixels can be sampled)
          --progress-dump-detail-json <json>
                                    the EXACT laid-out row strings handed to GDI+ (plus the width cap),
                                    so acceptance can assert on what was drawn instead of on intent
          --progress-detail-selftest
                                    exercise PillRect/Contains/IsExpanded/ToggleDetail headlessly and exit
          --wheel-simulate-cycles <N>
                                    run N non-invasive capture/show/select/escape cycles and exit
          --wheel-payload <p1;p2;...>
                                    inject a payload into the post-DragEnter path (diagnostic) and exit
          --wheel-backdrop <screen|dark|light>
                                    glass backdrop source (default screen = the real desktop);
                                    dark/light use a synthetic wallpaper so visual checks are
                                    reproducible and never touch the user's screen
          --settings-file <path>    use this settings file (default %LOCALAPPDATA%\Confern\settings.json)
          --settings-set k=v;k=v    change settings and save them, then exit (spec 8); keys that are not
                                    accepted are reported and exit with code 4 instead of being ignored
          --settings-probe <path>   load that settings file, print the clamped result and exit
          --settings-ui             open the settings page only (no overlay); exits when the window closes
          --settings-ui-live        start the overlay AND open the settings page (saving applies at once)
          --settings-ui-dump <png>  render the settings page to a PNG and exit (visual evidence)
          --settings-ui-selftest    open the settings page, verify it opens/closes and exits (diagnostic)
          --tray-selftest           create the tray icon, verify registration/menu/tooltip, run each menu
                                    command through the shared dispatch, then prove the shell let it go
          --autostart-probe <subkey>
                                    exercise the start-with-Windows entry against that HKCU subkey
                                    (enable -> read back -> disable); never touches the real Run key
          --tray-exit-after <ms>    resident run: ask the tray to quit after N ms (acceptance runs must
                                    not inject input, and a resident app has to be asked to leave)
          --no-autostart            resident run: do NOT apply startWithWindows (spec 8). Verification
                                    runs use this so a regression run never changes the user's Run key
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
    /// <summary>同 <see cref="WheelDump"/>，但**保留 alpha**（驗 alpha 語意紅線用）。</summary>
    public string? WheelDumpRaw;
    /// <summary>
    /// 玻璃底材來源：`screen`（預設，抓真實桌面）／`dark`／`light`（合成桌布）。
    /// 合成桌布是為了**可重現的驗收**：自動化不能依賴「當時桌面上剛好有什麼」，
    /// 深淺兩端的可讀性也必須能重跑（見 WheelDesign 的光暈策略）。
    /// </summary>
    public string WheelBackdrop = "screen";
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
    /// <summary>
    /// --progress-dump-detail &lt;png&gt;：把規格 §7.3 的失敗清單畫成一張 PNG。
    ///
    /// 存在的理由與 <see cref="ProgressDump"/> 相同，而這裡更強：清單是**檔名 ＋ 中文原因**
    /// 兩欄，中文與 ✓／✗ 這類符號若字型缺字就是方框，而方框**也有墨水**——
    /// 「檔案存在」什麼都證明不了。搭配 <see cref="ProgressDumpDetailRaw"/> 才能取樣像素。
    ///
    /// 同時帶 `--convert <壞檔>` 時，列資料直接來自**真的轉檔失敗**（不是合成字串）。
    /// </summary>
    public string ProgressDumpDetail = "";
    /// <summary>同 <see cref="ProgressDumpDetail"/>，但**保留 alpha**（唯一能取樣文字墨水的檔案）。</summary>
    public string ProgressDumpDetailRaw = "";
    /// <summary>
    /// --progress-dump-detail-json &lt;json&gt;：把**真正交給 GDI+ 的每一列字串**寫成 JSON（D4 的證據）。
    ///
    /// 為什麼非有這個不可：驗收要斷言「畫出來的那一行還帶著完整原因、只有檔名被中略」。
    /// 只驗日誌的話，驗到的是「我打算這樣畫」——那叫**自我報告**；只驗 PNG 的話，
    /// 「字被截掉了」與「字比較短」在像素上分不出來。這個檔案是排版結果本身，
    /// 點陣圖的寬度則是同一件事的第二個角度（兩者必須一致）。
    /// </summary>
    public string ProgressDumpDetailJson = "";
    /// <summary>
    /// --progress-detail-selftest：不必畫面就驗規格 §7.3 的行為——
    /// `IsExpanded` 一開始必須是 false（「清單不自動彈出」）、`ToggleDetail` 要真的翻動它、
    /// 矩形要跟著展開長大、`Contains` 要同意那塊矩形。
    /// 這些都是「接上真實點擊之後」會直接影響使用者的性質，所以現在就要有自動斷言。
    /// </summary>
    public bool ProgressDetailSelfTest;
    /// <summary>--progress-trace：記錄每一次週期回呼的決策（診斷淡出卡住用）。</summary>
    public bool ProgressTrace = false;
    /// <summary>--settings-probe <path>：載入指定的設定檔並印出夾制後的結果（驗證 §8 的範圍規則）。</summary>
    public string SettingsProbe = "";
    /// <summary>
    /// --settings-file <path>：這次執行要用的設定檔（一般執行與 `--settings-set` 都用它）。
    /// 空＝`%LOCALAPPDATA%\Confern\settings.json`。存在的理由＝驗證必須能**隔離使用者的真實設定**，
    /// 否則一個改過設定的人會讓「預設值」的斷言隨環境浮動。
    /// </summary>
    public string SettingsFile = "";
    /// <summary>--settings-set k=v;k=v：改設定並存檔（設定頁按「儲存」走同一條路）。空＝不動作。</summary>
    public string SettingsSet = "";
    /// <summary>
    /// --settings-ui：只開設定頁（不啟動覆蓋層），視窗關掉就結束。這是托盤（待辦 4）之前
    /// 使用者唯一能自己打開設定頁的入口，所以它必須像一個正常的設定視窗，不留背景行程。
    /// </summary>
    public bool SettingsUi;
    /// <summary>
    /// --settings-ui-live：啟動完整程式（覆蓋層＋hook）並開啟設定頁，用來驗「儲存→當場套用」。
    /// 托盤做好之後走的是同一條路徑，這個旗標則留給自動化驗收。
    /// </summary>
    public bool SettingsUiLive;
    /// <summary>--settings-ui-dump &lt;png&gt;：把設定頁畫成一張 PNG（設計審查與視覺證據用）。</summary>
    public string SettingsUiDump = "";
    /// <summary>
    /// --settings-ui-selftest：不必人眼就能驗設定頁的生命週期（開得起來、第二次開啟被拒、關得掉、
    /// 而且**不會讓行程結束不掉**）。最後那一項人眼看不到，卻是設定頁最現實的失效模式。
    /// </summary>
    public bool SettingsUiSelfTest;
    /// <summary>
    /// --tray-selftest：托盤圖示的自測（待辦 4）。托盤最現實的失效模式是**靜默的**——
    /// 圖示根本沒出現在通知區域，而程式一切正常。所以每一項都必須能被斷言：
    /// 有沒有註冊、選單幾項、提示文字是什麼、三個命令各自有沒有走到、關掉之後 shell 還留不留著它。
    /// </summary>
    public bool TraySelfTest;
    /// <summary>
    /// --autostart-probe &lt;subkey&gt;：在指定的 HKCU 子機碼上完整跑一次
    /// 「不存在 → 啟用 → 讀回 → 停用 → 不存在」。
    /// **存在的理由就是「不要動使用者的真機碼」**：驗收若直接寫真正的 Run 機碼，
    /// 每一次回歸都會悄悄改掉使用者的開機啟動設定。
    /// </summary>
    public string AutostartProbe = "";
    /// <summary>
    /// --tray-exit-after &lt;ms&gt;：常駐執行 N 毫秒後，**透過托盤的「結束」命令**要求正常關機。
    ///
    /// 存在的理由：常駐程式不會自己結束，而驗收腳本不得注入任何合成輸入（使用者可能正在用電腦），
    /// 也不該用 Kill 假裝「正常結束」——那會跳過全部 Dispose，正好掩蓋掉這一項要驗的東西。
    /// 走的是與選單「結束」同一條路徑，所以它驗到的就是使用者按下去會發生的事。
    /// </summary>
    public int TrayExitAfterMs;
    /// <summary>
    /// --no-autostart：常駐模式**不套用** `StartWithWindows`（規格 §8）。
    ///
    /// 為什麼需要一個「明確說不要」的旗標：`StartWithWindows` 的預設是 on，所以任何一個用預設值
    /// 啟動常駐模式的行程都會寫使用者的 Run 機碼。驗收腳本啟動的正是這種行程——若不把它們
    /// 隔開，跑一次回歸就會在使用者的開機啟動清單裡留下一個指向 `bin\Release` 的項目，
    /// 而那是**使用者沒有要求過**的系統變更。
    ///
    /// 反過來說，這裡刻意不是「偵測到旗標就不要」的自動判斷：那種規則沒有人說得清楚，
    /// 而「這個行程會不會改我的系統設定」必須是一件查得出來的事。
    /// </summary>
    public bool NoAutostart;
    /// <summary>--trigger-key 是否由命令列明確指定（決定它與設定檔的優先順序）。</summary>
    public bool TriggerKeyFromCli;
    /// <summary>--watchdog-seconds 是否由命令列明確指定（同上）。</summary>
    public bool WatchdogFromCli;
    public int WheelSimulateCycles;

    /// <summary>
    /// 診斷用：把假的酬載直接餵進「真實 DragEnter 之後」的處理路徑（分號分隔的路徑清單）。
    /// 它**不**驗證 OLE 管線本身（那需要真實拖曳），只驗證拿到酬載之後的行為。
    /// </summary>
    public string? WheelPayload;

    /// <summary>
    /// 自測專用：同一組旗標但指向另一個設定檔。存在的理由只有一個——
    /// 自測會走**真正的**「開啟設定」路徑，而它絕不可以編輯使用者真正的設定檔。
    /// </summary>
    public Options ForSelfTest(string settingsFile)
    {
        var copy = (Options)MemberwiseClone();   // Options 不是 record，所以沒有 with
        copy.SettingsFile = settingsFile;
        return copy;
    }

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
                case "--wheel-dump-raw": if (i + 1 < args.Length) o.WheelDumpRaw = args[++i]; break;
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
                case "--progress-dump-detail":
                    if (i + 1 < args.Length) { o.ProgressDumpDetail = args[i + 1]; i++; }
                    break;
                case "--progress-dump-detail-raw":
                    if (i + 1 < args.Length) { o.ProgressDumpDetailRaw = args[i + 1]; i++; }
                    break;
                case "--progress-dump-detail-json":
                    if (i + 1 < args.Length) { o.ProgressDumpDetailJson = args[i + 1]; i++; }
                    break;
                case "--progress-detail-selftest":
                    o.ProgressDetailSelfTest = true;
                    break;
                case "--settings-probe":
                    if (i + 1 < args.Length) { o.SettingsProbe = args[i + 1]; i++; }
                    break;
                case "--progress-trace":
                    o.ProgressTrace = true;
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
                case "--wheel-backdrop":
                    if (++i < args.Length) o.WheelBackdrop = args[i].Trim().ToLowerInvariant();
                    break;
                case "--quiet": o.Quiet = true; break;
                case "--log":
                    if (++i < args.Length) o.LogPath = args[i];
                    break;
                case "--trigger-key":
                    if (++i < args.Length) { o.TriggerKey = ParseVirtualKey(args[i]); o.TriggerKeyFromCli = true; }
                    break;
                case "--cycle-test":
                    if (++i < args.Length && int.TryParse(args[i], out int n) && n > 0) o.CycleTest = n;
                    break;
                case "--watchdog-seconds":
                    if (++i < args.Length && int.TryParse(args[i], out int s) && s > 0) { o.WatchdogSeconds = s; o.WatchdogFromCli = true; }
                    break;
                case "--settings-file":
                    if (++i < args.Length) o.SettingsFile = args[i];
                    break;
                case "--settings-set":
                    if (++i < args.Length) o.SettingsSet = args[i];
                    break;
                case "--settings-ui": o.SettingsUi = true; break;
                case "--settings-ui-live": o.SettingsUiLive = true; break;
                case "--settings-ui-dump":
                    if (++i < args.Length) o.SettingsUiDump = args[i];
                    break;
                case "--settings-ui-selftest": o.SettingsUiSelfTest = true; break;
                case "--tray-selftest": o.TraySelfTest = true; break;
                case "--autostart-probe":
                    if (++i < args.Length) o.AutostartProbe = args[i];
                    break;
                case "--tray-exit-after":
                    if (++i < args.Length && int.TryParse(args[i], out int exitMs) && exitMs > 0) o.TrayExitAfterMs = exitMs;
                    break;
                case "--no-autostart": o.NoAutostart = true; break;
            }
        }
        return o;
    }

    /// <summary>
    /// 觸發鍵 → **鍵帽上顯示的名字**。鍵帽畫的是實體按鍵（`space`／`shift`），不是 VK 代碼；
    /// 認不得的鍵就照實印代碼，不假裝認得。
    /// </summary>
    public static string VirtualKeyLabel(uint vk) => vk switch
    {
        0x20 => "space",
        0x0D => "enter",
        0x09 => "tab",
        0x10 => "shift",
        0x11 => "ctrl",
        0x12 => "alt",
        0x1B => "esc",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString().ToLowerInvariant(),
        _ => $"0x{vk:X2}"
    };

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
        evidence.Line($"{ProductInfo.Name} {Program.VersionText()} starting: trigger=VK 0x{opt.TriggerKey:X2} " +
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

        // M1f：設定（規格 §8）必須在**建立偵測器之前**載入——觸發鍵與接收層逾時都來自它。
        // 優先順序：命令列（診斷用）> 設定檔 > 規格預設值；預設值即規格值，檔案壞掉／超範圍都會說明。
        _settings = SettingsStore.Load(string.IsNullOrEmpty(opt.SettingsFile) ? null : opt.SettingsFile, evidence.Line);
        // 「診斷記錄」只影響 detail 行的多寡，不影響必要事件——所以它可以在這裡就生效，
        // 而且必須在**其他東西開始寫日誌之前**生效，否則同一份日誌會有兩種規矩。
        evidence.SetDiagnostics(_settings.Diagnostics);
        // `--progress-trace` 選的是「要追什麼」，而「診斷記錄」決定「要不要寫 detail」。
        // 兩者衝突時以設定為準（那個語意是使用者裁定過的），但**必須大聲說**——
        // 一個默默無效的旗標比一個會抱怨的旗標糟糕得多。
        if (opt.ProgressTrace && !_settings.Diagnostics)
            evidence.Line("note: --progress-trace is ON but diagnostics is OFF -> per-tick detail lines are suppressed. " +
                          "Turn on the diagnostics setting (settings page, or --settings-set diagnostics=true) to see them.");
        uint triggerVk = opt.TriggerKeyFromCli ? opt.TriggerKey : SettingsStore.ResolveTriggerVk(_settings, evidence.Line);
        int watchdogMs = opt.WatchdogFromCli ? opt.WatchdogSeconds * 1000 : _settings.CaptureTimeoutSeconds * 1000;
        evidence.Line($"settings in effect: trigger=VK 0x{triggerVk:X2} watchdog={watchdogMs} ms " +
                      $"(trigger from {(opt.TriggerKeyFromCli ? "command line" : "settings file")}, " +
                      $"watchdog from {(opt.WatchdogFromCli ? "command line" : "settings file")})");

        using var hit = new HitLayer(evidence, monitors, Native.OverlayAlpha);
        using var guard = new ReleaseGuard(evidence, hit, watchdogMs);
        var detector = new DragDetector(evidence, triggerVk, opt.AssumeExplorerForeground);

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
        using var progress = new ProgressPresenter(evidence, progressLayer, opt.ProgressTrace);
        // runner 一定要釋放：它擁有 worker 行程與管道（Dispose 會送 shutdown 並確保 worker 收掉）
        using var runner = new ConversionRunner(Environment.ProcessPath ?? $"{ProductInfo.Name}.exe", evidence.Line, progress);
        // 格式能力探測也要**預先做**：第一次使用 WPF/WIC 會載入 PresentationCore，
        // 若等到酬載進來才做，那筆成本會直接落在「酬載 → 顯示」的 100 ms 預算上。
        var catalog = BuildCatalog(evidence);
        var presenter = new WheelPresenter(evidence, visual, opt.WheelBackdrop);
        presenter.TriggerKeyLabel = Options.VirtualKeyLabel(triggerVk);

        // M1f：設定頁的「儲存 → 當場套用」（規格 §8）。設定頁跑在自己的 WPF 執行緒上，所以它的回呼
        // 只做「排隊 ＋ PostMessage 叫醒這裡」；真正的套用一律在主執行緒發生（日誌與覆蓋層都不是
        // 執行緒安全的，跨執行緒直接呼叫只會變成隨機損壞）。三個設定值各有單一擁有者：
        // 觸發鍵交給 detector、逾時交給 guard、其餘由 _settings 帶著走（轉檔時才讀）。
        void ApplyFromSettingsUi()
        {
            while (_settingsUiLog.TryDequeue(out string? uiLine)) evidence.Line(uiLine);
            Settings? updated = Interlocked.Exchange(ref _pendingSettings, null);
            if (updated == null) return;
            _settings = updated;
            uint appliedVk = SettingsStore.ResolveTriggerVk(updated, evidence.Line);
            detector.SetTriggerKey(appliedVk);
            guard.SetVisibleTimeout(updated.CaptureTimeoutSeconds * 1000);
            evidence.SetDiagnostics(updated.Diagnostics);
            presenter.TriggerKeyLabel = Options.VirtualKeyLabel(appliedVk);

            // 規格 §8 的 `StartWithWindows`：設定頁存檔時是唯一的另一個寫入點。
            // --no-autostart 也要擋住在這裡（否則「隔離過的常駐行程」還是能被設定頁打開開關）。
            ApplyStartWithWindows(opt.NoAutostart ? null : updated.StartWithWindows, evidence);

            evidence.Line($"settings ui: applied {SettingsStore.Describe(updated)} " +
                          $"(trigger=VK 0x{appliedVk:X2}, watchdog={updated.CaptureTimeoutSeconds * 1000} ms)");
        }
        visual.SettingsChanged += ApplyFromSettingsUi;

        // M1g：開機自動啟動的擁有者（規格 §8，待辦 4）。整個常駐路徑只有這一個實例，
        // 而且只有它會寫 Run 機碼。
        _startup = Autostart.Default(evidence.Line);

        if (opt.SettingsUiLive)
        {
            string uiPath = string.IsNullOrEmpty(opt.SettingsFile) ? SettingsStore.DefaultPath() : opt.SettingsFile;
            SettingsUi.Open(_settings ?? SettingsStore.Load(null, evidence.Line), uiPath,
                saved =>
                {
                    _pendingSettings = saved;         // 交接點：ApplyFromSettingsUi 會在主執行緒取走
                    visual.RequestSettingsApply();
                },
                uiLine =>
                {
                    _settingsUiLog.Enqueue(uiLine);
                    visual.RequestSettingsApply();
                });
            evidence.Line($"settings ui: opened in the running app (--settings-ui-live, path={uiPath}); " +
                          "saving in the window applies immediately");
        }

        // 規格 §8 的 `StartWithWindows`（待辦 4）。**只有常駐程式套用它**——`--settings-set`
        // 是純設定檔編輯器，碰 Run 機碼會讓每一次自動化驗收都悄悄改掉使用者的開機啟動設定。
        //
        // 「常駐程式」在這裡的定義是「跑得到這一行」：`--cycle-test`、`--wheel-demo`、
        // `--dump-monitors` 之類在更上面就 return 了，所以它們永遠不會碰 Run 機碼；
        // 而 `--wheel-payload`、`--wheel-hover-selftest`、`--raise`、`--settings-ui-live`
        // 會走到這裡——它們是真的把接收層與訊息迴圈跑起來的診斷模式。
        // 這件事是**量出來的**，不是推論的：m1b 的 `--wheel-payload`、
        // m1f 的 `--settings-ui-live` 都真的在使用者的 Run 機碼留下了一筆 `Confern`。
        // 驗收腳本因此一律帶 `--no-autostart`（見 tools/*.ps1）。
        ApplyStartWithWindows(opt.NoAutostart ? null : _settings?.StartWithWindows ?? false, evidence);

        // 托盤用 using：離開這個方法（＝行程正在收尾）時一定要把圖示從 shell 拿掉、
        // 把 HICON／HMENU 釋放掉。行程結束時 shell 也會清，但那只在「正常結束」時成立，
        // 而 Dispose 是**我們自己做得到**的那一半。
        using TrayIcon tray = new(
            evidence,
            onOpenSettings: () => OpenSettingsFromTray(opt, evidence, visual, _settings, LogFromSettingsUiThread),
            // 注意這條回呼**不碰 tray 變數**：它自己就是 tray 的建構引數，編譯器不允許在那裡
            // 讀取一個還沒賦值的區域變數（CS0165，而且那是真的——回呼有可能在指派完成前被叫到）。
            // 圖示顯示的狀態改由 TrayIcon 在回呼跑完之後**問這裡**（見 TrayIcon.Dispatch）：
            // 傳進去的是一個查詢，不是一份快照，所以它永遠問得到最新的事實。
            onTogglePause: () => SetCapturePaused(!_capturePaused, guard, detector, evidence),
            isCaptureDisabled: () => guard.CaptureDisabled || detector.IsDisabled,
            // 這條也刻意不碰 tray（同上，而且連 using 的隱藏賦值都算在建構式求值之後）：
            // 結束時要的是「正常關機」，而托盤的 Dispose 由 using 負責，不需要在這裡讀那個變數。
            onExit: () => RequestCleanExit(evidence, guard));

        // 驗收用的「請它自己結束」：計時器在執行緒池上，所以只**張貼**托盤命令，不直接呼叫回呼
        // （直接呼叫會從錯誤的執行緒碰到托盤的視窗與選單）。關機路徑因此與選單的「結束」完全同一條。
        if (opt.TrayExitAfterMs > 0)
        {
            IntPtr trayHwnd = tray.WindowHandle;
            if (trayHwnd == IntPtr.Zero)
            {
                evidence.Line("tray: --tray-exit-after requested but the tray window does not exist; nothing will quit the app");
            }
            else
            {
                evidence.Line($"tray: --tray-exit-after {opt.TrayExitAfterMs} ms (asks the tray to quit through the menu path)");
                var exitTimer = new System.Threading.Timer(
                    _ => Native.PostMessageW(trayHwnd, Native.WM_APP_TRAY_COMMAND,
                                             new IntPtr(Native.TrayCommandExit), IntPtr.Zero),
                    null, opt.TrayExitAfterMs, Timeout.Infinite);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => exitTimer.Dispose();
            }
        }

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
            // 診斷用：確定性地重現「第一次回呼來得太晚」這個時序（見 ProgressPresenter.Update）。
            // CommitWheel -> ConversionClient.Start 會為了等 worker 連線而擋住 UI 執行緒，
            // 而 worker 的實際工作只要幾毫秒；再睡一下，就保證第一個回呼看到的是「已完成」。
            // 少了這一段，這條路徑只有大約 1/6 的機率踩到（S12.10d 就是這樣漏掉的）。
            if (committed) Thread.Sleep(600);
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
                // 規格 §7.3 的失敗清單，列資料走的是**這條真實路徑**：worker 解碼失敗 → 管道訊息 →
                // 快照 → 列資料。`--convert` 那條路是同一顆引擎但**不經過管道**，
                // 所以它驗不到「管道另一端送來的東西在這裡被正確拆成兩欄」——
                // 而那一拆正是最容易默默出錯的一段（訊息是排版，檔名要用欄位帶）。
                // 這裡只留證據（不改變後面的淡出與放手流程）：每一列寫成 detail row，並 dump 面板。
                List<ConversionFailureRow> rows = final.DetailRows.ToList();
                foreach (ConversionFailureRow row in rows)
                    evidence.Line($"detail row: file={row.FileName} reason='{row.Reason}'");
                DumpDetailPanels(opt, evidence, rows);
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
        var presenter = new WheelPresenter(evidence, visual, opt.WheelBackdrop);
        presenter.TriggerKeyLabel = Options.VirtualKeyLabel(opt.TriggerKey);
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
            if (opt.WheelDumpRaw != null) presenter.DumpRawTo(opt.WheelDumpRaw);
            return 0;
        }
        if (opt.WheelDumpRaw != null)
        {
            presenter.DumpRawTo(opt.WheelDumpRaw);
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
    /// 開機自動啟動的唯一擁有者（待辦 4）。常駐路徑建立它，診斷路徑建立的是**別的**子機碼
    /// （<c>--autostart-probe</c>）或根本沒有（單一入口讓「誰會寫 Run 機碼」是查得出來的事）。
    /// </summary>
    private static Autostart? _startup;

    /// <summary>托盤目前的暫停狀態。與 <see cref="ReleaseGuard.CaptureDisabled"/> 同義，只是給日誌用。</summary>
    private static bool _capturePaused;

    /// <summary>
    /// 設定頁在 WPF 執行緒上按下「儲存」之後放的結果，由主執行緒取走並套用。
    /// 交接用參考賦值 ＋ <see cref="Interlocked.Exchange{T}(ref T, T)"/>：設定是 record（參考型別），
    /// 這種交接不需要鎖，也不會讓主執行緒讀到半套的狀態。
    /// </summary>
    private static Settings? _pendingSettings;

    /// <summary>
    /// 設定頁那條執行緒產生的日誌行。**只能由主執行緒代寫**：<see cref="Evidence.Line"/> 沒有上鎖，
    /// 兩條執行緒同時寫會互相插斷（而且不會當場報錯）。
    /// </summary>
    private static ConcurrentQueue<string> _settingsUiLog = new();

    /// <summary>
    /// M1f：`--settings-ui`——只開設定頁，不啟動覆蓋層，**視窗關掉就結束**。
    /// 托盤（待辦 4）之前這是使用者唯一的入口，所以它必須像正常的設定視窗：不留背景行程、
    /// 不需要工作管理員。存檔本身由設定頁走 `SettingsStore.Save`（檔案當場就變），這裡只留證據。
    /// </summary>
    internal static int RunSettingsUiStandalone(Options opt, Evidence evidence)
    {
        string path = string.IsNullOrEmpty(opt.SettingsFile) ? SettingsStore.DefaultPath() : opt.SettingsFile;
        Settings settings = SettingsStore.Load(File.Exists(path) ? path : null, evidence.Line);
        var lines = new ConcurrentQueue<string>();

        evidence.Line($"settings ui standalone: opening (path={path})");
        SettingsUi.Open(settings, path,
            saved => lines.Enqueue($"settings ui standalone: saved {SettingsStore.Describe(saved)}"),
            lines.Enqueue);

        var sw = Stopwatch.StartNew();
        while (!SettingsUi.IsOpen && sw.ElapsedMilliseconds < 5000) Thread.Sleep(25);
        if (!SettingsUi.IsOpen)
        {
            while (lines.TryDequeue(out string? early)) evidence.Line(early);
            evidence.Line("settings ui standalone: FAILED to open (see the lines above)");
            return 1;
        }
        evidence.Line("settings ui standalone: open — this process exits when the window closes");

        // 上限只是防呆：視窗若因為某個 bug 永遠不關，這裡不可以變成一個殺不掉的背景行程。
        while (SettingsUi.IsOpen && sw.ElapsedMilliseconds < 30 * 60 * 1000) Thread.Sleep(100);
        bool timedOut = SettingsUi.IsOpen;
        if (timedOut) SettingsUi.Close();
        while (lines.TryDequeue(out string? line)) evidence.Line(line);
        evidence.Line($"settings ui standalone: done (timedOut={timedOut}) after {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    /// <summary>
    /// M1f：設定頁的生命週期自測（規格 §8）。設定頁跑在自己的 WPF 執行緒上，而它最現實的兩個失效模式
    /// 人眼都看不到：「開得起來卻關不掉」與「關掉之後行程結束不掉」（後者會讓程式一直掛在背景）。
    /// 這裡逐項斷言，任何一項不成立就回 1；過程全寫進證據日誌，不是只回一個成功。
    /// </summary>
    internal static int RunSettingsUiSelfTest(Options opt, Evidence evidence)
    {
        // **絕不可以**讓自測寫到使用者真正的設定檔：SelfTestSave 是按真的「儲存」。
        // 所以沒給路徑時一律用暫存檔；自己產生的暫存檔才清掉，操作者指定的路徑不動。
        bool tempPath = string.IsNullOrEmpty(opt.SettingsFile);
        string path = tempPath
            ? Path.Combine(Path.GetTempPath(), ProductInfo.Slug + "-settings-selftest.json")
            : opt.SettingsFile;
        if (tempPath && File.Exists(path)) File.Delete(path);   // 從乾淨的預設值開始，斷言才有意義
        Settings settings = SettingsStore.Load(File.Exists(path) ? path : null, evidence.Line);
        var lines = new ConcurrentQueue<string>();

        static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(25);
            }
            return condition();
        }

        evidence.Line($"settings ui selftest: opening (path={path})");
        SettingsUi.Open(settings, path, _ => lines.Enqueue("settings ui selftest: onSaved fired"), lines.Enqueue);
        bool opened = WaitFor(() => SettingsUi.IsOpen, 5000);
        evidence.Line($"settings ui selftest: opened={opened} isOpen={SettingsUi.IsOpen}");

        // 連點兩下不可以出現兩個編輯同一份檔案的視窗：後存的會把先存的蓋掉，而且是靜默的。
        SettingsUi.Open(settings, path, _ => lines.Enqueue("settings ui selftest: SECOND onSaved fired"), lines.Enqueue);
        bool singleInstance = SettingsUi.IsOpen;
        evidence.Line($"settings ui selftest: second open refused (still exactly one window)={singleInstance}");

        // 存檔鏈路：走**真正的按鈕處理常式**（SelfTestSave → SaveButton.RaiseEvent(Click)），
        // 而不是另外寫一份存檔邏輯。這裡的斷言只說「按下去有沒有成功」；檔案裡到底寫了什麼，
        // 由驗收腳本再用 product 自己的讀取器（--settings-probe）驗一次，不由被測者自證。
        Settings good = settings with
        {
            JpgQuality = 71, WebpQuality = 92, TriggerKey = "Tab",
            CaptureTimeoutSeconds = 45, Diagnostics = true, StartWithWindows = false, Language = "en"
        };
        bool saved = SettingsUi.SelfTestSave(good, 5000, out string saveDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: save via the real Save button ok={saved} detail={saveDetail}");

        Settings bad = settings with { TriggerKey = "NotARealKey" };
        bool refused = SettingsUi.SelfTestSave(bad, 5000, out string badDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: an invalid value is refused (save ok={refused}) detail={badDetail}");

        Thread.Sleep(700);      // 讓 WPF 真的完成第一次繪製，而不是只把物件建起來就說「開好了」
        SettingsUi.Close();
        bool closed = WaitFor(() => !SettingsUi.IsOpen, 3000);
        evidence.Line($"settings ui selftest: closed={closed} isOpen={SettingsUi.IsOpen}");

        while (lines.TryDequeue(out string? line)) evidence.Line(line);

        bool ok = opened && singleInstance && closed && saved && !refused;
        evidence.Line($"settings ui selftest done: ok={ok} (opened={opened} singleInstance={singleInstance} " +
                      $"closed={closed} saved={saved} refusedInvalid={refused})");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// M1f：`--settings-ui-dump &lt;png&gt;`——把設定頁畫成一張 PNG 就結束。
    /// 為什麼需要它：設定頁的外觀原本只能靠人眼看，回歸最多只能斷言「開得起來」。
    /// 這張圖讓「畫面長什麼樣」變成一個能保存、能比對、能拿給設計者看的檔案。
    /// </summary>
    internal static int RunSettingsUiDump(Options opt, Evidence evidence)
    {
        string path = string.IsNullOrEmpty(opt.SettingsFile)
            ? Path.Combine(Path.GetTempPath(), ProductInfo.Slug + "-settings-dump.json")
            : opt.SettingsFile;
        Settings settings = SettingsStore.Load(File.Exists(path) ? path : null, evidence.Line);
        var lines = new ConcurrentQueue<string>();

        evidence.Line($"settings ui dump: opening (path={path})");
        SettingsUi.Open(settings, path, _ => { }, lines.Enqueue);

        var sw = Stopwatch.StartNew();
        while (!SettingsUi.IsOpen && sw.ElapsedMilliseconds < 5000) Thread.Sleep(25);
        if (!SettingsUi.IsOpen)
        {
            while (lines.TryDequeue(out string? early)) evidence.Line(early);
            evidence.Line("settings ui dump: FAILED to open");
            return 1;
        }
        Thread.Sleep(500);      // 沒畫完就截會得到半張圖，所以先讓它畫完
        bool ok = SettingsUi.DumpFrame(opt.SettingsUiDump, 5000, out string detail, lines.Enqueue);
        SettingsUi.Close();
        while (lines.TryDequeue(out string? line)) evidence.Line(line);
        evidence.Line($"settings ui dump: ok={ok} detail={detail} path={opt.SettingsUiDump}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// M1f：`--settings-set k=v;k=v`（規格 §8）。設定頁按「儲存」走同一條路，所以它必須能被無頭驗證。
    /// 不接受的鍵／值一律**說出來**並以結束碼 4 收場——靜默只會讓使用者以為設定生效了。
    /// </summary>
    internal static int RunSettingsSet(Options opt, Evidence evidence)
    {
        string path = string.IsNullOrEmpty(opt.SettingsFile) ? SettingsStore.DefaultPath() : opt.SettingsFile;
        Settings next = SettingsStore.Load(path, evidence.Line);
        int applied = 0;
        var rejected = new List<string>();

        foreach (string pair in opt.SettingsSet.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                evidence.Line($"settings: reject '{pair}' (expected key=value)");
                rejected.Add(pair);
                continue;
            }
            if (SettingsStore.TryApply(next, pair[..eq], pair[(eq + 1)..], evidence.Line, out Settings updated))
            {
                next = updated;
                applied++;
            }
            else
            {
                rejected.Add(pair);
            }
        }

        if (applied > 0) SettingsStore.Save(next, path, evidence.Line);

        evidence.Line($"settings set result: {SettingsStore.Describe(next)}");
        evidence.Line($"settings set done: applied={applied} rejected={rejected.Count} path={path}");
        return rejected.Count == 0 ? 0 : 4;
    }

    // =====================================================================================
    // M1g：托盤圖示 ＋ 開機自動啟動（規格 §8，待辦 4）
    // =====================================================================================

    /// <summary>
    /// 套用「開機時自動啟動」（規格 §8）。**唯一呼叫端是常駐路徑**（啟動時、以及設定頁存檔時）。
    ///
    /// 為什麼 `--settings-set` 不呼叫它：那條路是純粹的設定檔編輯器，而驗收腳本會大量使用它。
    /// 若它會寫 Run 機碼，每一次自動化回歸都會悄悄改掉使用者的開機啟動設定——
    /// 而這種副作用在事後幾乎不可能被發現（使用者只會覺得「電腦開機後自己跑出一個程式」）。
    /// </summary>
    /// <param name="enabled">
    /// true＝寫入、false＝移除、<b>null＝完全不要碰</b>（<c>--no-autostart</c>）。
    /// 三個狀態刻意做成可空：把「不要碰」寫成 false 會讓它變成「把使用者的項目刪掉」，
    /// 那是完全不同的兩件事。
    /// </param>
    private static void ApplyStartWithWindows(bool? enabled, Evidence evidence)
    {
        if (enabled == null)
        {
            evidence.Line("autostart: skipped (--no-autostart; the Run key is not read or written by this run)");
            return;
        }
        if (_startup == null)
        {
            // 走到這裡代表有人從非常駐路徑呼叫它；說出來，不要靜默地什麼都不做。
            evidence.Line("autostart: skipped (no Autostart owner in this run mode)");
            return;
        }
        try
        {
            if (enabled.Value) _startup.Enable();
            else _startup.Disable();
        }
        catch (Exception ex)
        {
            // Run 機碼壞掉／被政策鎖住不可以讓程式起不來，但一定要說。
            evidence.Line($"autostart: applying startWithWindows={enabled} failed ({ex.GetType().Name}: {ex.Message})");
        }
        evidence.Line($"autostart: {_startup.Describe()}");
    }

    /// <summary>
    /// 托盤的「開啟設定」。
    ///
    /// 走的是與 `--settings-ui-live` **完全相同**的那條路（<see cref="SettingsUi.Open"/> 的
    /// 同一組回呼）：儲存之後仍然當場套用。設定頁在自己的 STA 執行緒上，這裡只是排隊後返回，
    /// 所以不會擋住主訊息迴圈（托盤的選單與訊息都靠它）。
    ///
    /// <paramref name="log"/>：設定頁那條執行緒產生的日誌行的去處。正式路徑一律排進
    /// <see cref="_settingsUiLog"/> 由主執行緒代寫（<see cref="Evidence.Line"/> 沒有上鎖，
    /// 兩條執行緒同時寫會互相插斷）；自測只有主執行緒會用到它，所以直接寫證據檔。
    /// </summary>
    private static void OpenSettingsFromTray(Options opt, Evidence evidence, VisualLayer visual,
                                             Settings? current, Action<string> log)
    {
        string uiPath = string.IsNullOrEmpty(opt.SettingsFile) ? SettingsStore.DefaultPath() : opt.SettingsFile;
        if (SettingsUi.IsOpen)
        {
            // 第二次開啟會被設定頁拒絕（兩個視窗會互相覆蓋存檔），所以連要求都不要送。
            evidence.Line($"tray: command -> open settings, but the settings page is already open (path={uiPath})");
            return;
        }

        evidence.Line($"tray: command -> open settings (path={uiPath})");
        // 要編輯的設定用**主執行緒手上那一份**（呼叫端傳進來的）。不重新載入是刻意的：
        // 重新載入會多讀一次預設路徑，於是自測的暫存路徑與被讀的路徑會是兩個不同的檔案。
        Settings editing = current ?? SettingsStore.Load(uiPath, evidence.Line);
        SettingsUi.Open(editing, uiPath,
            saved =>
            {
                _pendingSettings = saved;         // 交接點：ApplyFromSettingsUi 會在主執行緒取走
                visual.RequestSettingsApply();
            },
            log);
        evidence.Line("tray: settings ui requested (the window opens on its own STA thread; saving applies at once)");
    }

    /// <summary>
    /// 設定頁那條 WPF 執行緒排過來的日誌行。**只排隊，不寫檔**——<see cref="Evidence.Line"/>
    /// 沒有上鎖，由主執行緒代寫是唯一安全的做法（見 <see cref="_settingsUiLog"/>）。
    /// </summary>
    private static void LogFromSettingsUiThread(string line) => _settingsUiLog.Enqueue(line);

    /// <summary>
    /// 托盤的「暫停轉換 / 繼續轉換」。
    ///
    /// **不是第二套停用機制**：狀態就是 <see cref="ReleaseGuard.CaptureDisabled"/>（保險 #5 的同一個狀態）
    /// 與 <see cref="DragDetector.IsDisabled"/>。這裡只負責把兩者一起切換、並且把結果寫進日誌——
    /// 兩者若各切一半，會出現「接收層不再升起、但偵測器還以為自己活著」這種一半停用的狀態。
    /// </summary>
    private static void SetCapturePaused(bool paused, ReleaseGuard guard, DragDetector detector,
                                         Evidence evidence)
    {
        if (guard.CaptureDisabled != detector.IsDisabled)
            evidence.Line("WARNING tray: guard and detector disagreed about the pause state; " +
                          $"reasserting the guard's value (guard={guard.CaptureDisabled}, detector={detector.IsDisabled})");

        if (paused)
        {
            // 托盤的暫停＝**可以復原的那一種**。刻意不叫 guard.OnKillSwitch()：那會把
            // `*** KILL SWITCH ***` 印在托盤暫停的日誌上（m1a 的 S3.8 正是斷言那一行，
            // 於是那條斷言會被托盤的暫停滿足），也會把「直到重啟」的閂扣上。
            guard.Pause("tray pause");
            detector.SetPaused(true, "tray pause");
        }
        else
        {
            // 緊急停用之後這裡會被拒絕（見 ReleaseGuard.Resume）——那正是規格 §4.2.2 要的。
            guard.Resume();
            detector.SetPaused(false, "tray resume");
        }

        // 真相只有一個：記下的是兩個擁有者**實際**的狀態，不是使用者剛按下的意圖。
        // 意圖與結果不同時（緊急停用之後要求繼續），日誌裡必須看得出差別。
        _capturePaused = guard.CaptureDisabled || detector.IsDisabled;
        evidence.Line($"tray: capture paused={_capturePaused} (requested={paused}) " +
                      $"(guard.CaptureDisabled={guard.CaptureDisabled}, detector.IsDisabled={detector.IsDisabled}, " +
                      $"killSwitchLatched={guard.KillSwitchLatched})");
    }

    /// <summary>
    /// 托盤的「結束」。
    ///
    /// 用的是**這個程式既有的乾淨結束路徑**：<c>PostQuitMessage</c> 讓主訊息迴圈
    /// （<see cref="RunMessageLoop"/>）的 <c>GetMessageW</c> 回 0 而自然離開，
    /// 於是 <c>RunCore</c> 的 <c>using</c> 依序釋放 hooks／runner／兩層覆蓋層／托盤。
    /// 刻意不呼叫 <c>Environment.Exit</c>：那會跳過全部 <c>Dispose</c>，
    /// 留下 worker 行程與沒有從 shell 拿掉的托盤圖示。
    /// </summary>
    private static void RequestCleanExit(Evidence evidence, ReleaseGuard guard)
    {
        evidence.Line("tray: command -> exit requested; taking the normal shutdown path (posting the quit request)");

        // 收層與停用先做：結束路徑上不留下任何還武裝著的保險（保險 #3 的定時器此刻可能正在跑）。
        guard.OnUserResolved("tray exit");
        // 這裡刻意用 Pause 而不是 OnKillSwitch：正常結束不是緊急停用，不該印 `*** KILL SWITCH ***`
        // （那會讓「日誌裡有 KILL SWITCH」不再等於「熱鍵真的被按過」），也不需要扣那個閂。
        guard.Pause("clean shutdown");
        Native.PostQuitMessage(0);
        evidence.Line("tray: quit request posted (the message loop will exit and every using-block will dispose)");
    }

    /// <summary>
    /// M1g：`--tray-selftest`——托盤圖示的自測。
    ///
    /// 為什麼需要它：托盤最現實的失效模式**全部是靜默的**。圖示根本沒出現在通知區域、
    /// 選單少一項、提示文字沒跟著暫停狀態換、關掉之後 shell 還留著一個幽靈圖示——
    /// 這些都不會讓程式壞掉，只會讓使用者覺得「這個程式好像沒在跑」。
    /// 這裡逐項斷言，任何一項不成立就回 1，過程全部寫進證據日誌（每行都以 `tray:` 開頭）。
    ///
    /// 三個命令一律經過 <see cref="TrayIcon.InvokeCommand"/>——與真實選單點擊**同一條分派**。
    /// 自測若能走一條只有它自己會走的路，它驗到的就只是自測自己。
    /// </summary>
    internal static int RunTraySelfTest(Options opt, Evidence evidence)
    {
        int failed = 0;
        int checks = 0;
        void Report(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failed++;
            evidence.Line($"tray: {(ok ? "PASS" : "FAIL")} {name}" +
                          (detail.Length > 0 ? $"  [{detail}]" : string.Empty));
        }

        // 自測**絕不可以**寫到使用者真正的設定檔（下面的設定頁檢查會走真正的開啟路徑）。
        // 沒給路徑時用暫存檔；自己產生的暫存檔才清掉，操作者指定的路徑不動。
        bool tempSettings = string.IsNullOrEmpty(opt.SettingsFile);
        string settingsPath = tempSettings
            ? Path.Combine(Path.GetTempPath(), ProductInfo.Slug + "-tray-selftest.json")
            : opt.SettingsFile;
        // 換成暫存路徑的那一份選項，供下面的「開啟設定」使用（Options 不是 record，所以是複製）
        Options trayOpt = opt.ForSelfTest(settingsPath);
        Settings settings = SettingsStore.Load(File.Exists(settingsPath) ? settingsPath : null, evidence.Line);
        using var visual = new VisualLayer(evidence);          // 設定頁的「排隊＋叫醒」需要它

        // 自測用的守衛與偵測器：**與正式路徑同一組型別、同一組呼叫**（SetCapturePaused）。
        // 沒有螢幕，所以命中層是零個視窗——HideNow 會以真實狀態判斷「沒有東西要收」而直接返回，
        // 這正是它在正式路徑上每次被叫到時的第一個判斷。
        using var hit = new HitLayer(evidence, Array.Empty<MonitorEntry>());
        using var guard = new ReleaseGuard(evidence, hit);
        var detector = new DragDetector(evidence, Program.DefaultTriggerKey);
        bool pausing = false;          // 自測用的暫停狀態（與正式回呼同一組呼叫）

        using var tray = new TrayIcon(
            evidence,
            // 走**真正的**「托盤開啟設定」路徑。這條要求只是排隊後返回（設定頁跑在自己的 STA
            // 執行緒上），所以它不會把托盤的訊息迴圈擋住——而「選單按下去整個程式就卡住」
            // 正是這裡最值得驗、又最不容易發現的失效模式。
            onOpenSettings: () => OpenSettingsFromTray(trayOpt, evidence, visual, settings, evidence.Line),
            // 與常駐路徑同一個查詢（見那裡的理由）：圖示顯示事實，不是意圖。
            isCaptureDisabled: () => guard.CaptureDisabled || detector.IsDisabled,
            // 與正式路徑同一件事：翻轉狀態、讓守衛與偵測器一起停用／復原。圖示自己的提示文字
            // 由 TrayIcon 在分派命令時同步（所以這裡不必、也不可以碰 tray 這個變數，見正式路徑的註解）。
            onTogglePause: () =>
            {
                pausing = !pausing;
                SetCapturePaused(pausing, guard, detector, evidence);
                evidence.Line($"tray: dispatch toggle-pause paused={pausing}");
            },
            onExit: () => evidence.Line("tray: dispatch exit"));

        Report("icon created", tray.IsCreated, $"menu items={TrayIcon.MenuItemCount}");

        // 提示文字：產品名稱來自 ProductInfo（唯一擁有者），狀態是 ASCII 的 running/paused。
        Report("tooltip names the product and the state",
               tray.Tooltip == $"{ProductInfo.Name} - running",
               $"tooltip='{tray.Tooltip}' expected='{ProductInfo.Name} - running'");
        Report("menu has exactly the three spec items", TrayIcon.MenuItemCount == 3,
               $"count={TrayIcon.MenuItemCount}");
        Report("menu labels are the spec labels",
               tray.LabelsMatchSpec(),
               $"'{TrayIcon.MenuOpenSettings}' / '{TrayIcon.MenuPause}' / '{TrayIcon.MenuExit}'");
        Report("the shell holds the icon", tray.ProbeShellStillHasIcon(), "NIM_MODIFY accepted");

        // 三個命令各自走一次（與真實選單點擊同一條分派）。
        tray.InvokeCommand(TrayCommand.OpenSettings);
        tray.InvokeCommand(TrayCommand.TogglePause);
        tray.InvokeCommand(TrayCommand.Exit);
        // 命令是 posted message，排到擁有視窗的那條執行緒上；跑一下訊息讓它們落地。
        for (int i = 0; i < 5; i++) Native.PumpMessages();

        Report("pause state reached the icon", tray.IsPaused && tray.Tooltip == $"{ProductInfo.Name} - paused",
               $"tooltip='{tray.Tooltip}'");
        Report("the menu label follows the pause state",
               tray.LabelsMatchSpec(),
               $"menu1 expected '{TrayIcon.MenuResume}' while paused");
        tray.InvokeCommand(TrayCommand.TogglePause);
        for (int i = 0; i < 5; i++) Native.PumpMessages();
        Report("resume returns to the running state",
               !tray.IsPaused && tray.Tooltip == $"{ProductInfo.Name} - running",
               $"tooltip='{tray.Tooltip}'");

        // 保險 #5 之後，托盤**不可以**把捕捉偷偷武裝回來（規格 §4.2.2：「直到重啟」）。
        // 這條檢查會失敗的那個版本＝把 Resume 寫成無條件復原的那一版（托盤的暫停直接叫
        // OnKillSwitch、繼續直接叫 Resume）；那種寫法看起來完全正常，只有這裡問得出來。
        guard.OnKillSwitch();
        detector.Disable();
        // 兩次分派：第一次是「暫停」（此時已經停用，是 no-op），第二次才是「繼續」——
        // 也就是被拒絕的那一次。刻意走 InvokeCommand，與真實選單點擊同一條分派。
        tray.InvokeCommand(TrayCommand.TogglePause);
        tray.InvokeCommand(TrayCommand.TogglePause);
        for (int i = 0; i < 5; i++) Native.PumpMessages();
        Report("kill switch stays latched: the tray cannot resume capture",
               guard.CaptureDisabled && detector.IsDisabled,
               $"guard.CaptureDisabled={guard.CaptureDisabled} detector.IsDisabled={detector.IsDisabled} " +
               $"latched={guard.KillSwitchLatched}");
        Report("the icon tells the truth after a refused resume", tray.IsPaused,
               $"tray.IsPaused={tray.IsPaused} tooltip='{tray.Tooltip}'");

        // 「托盤的開啟設定真的開得了設定頁」。自測裡的呼叫端不會把日誌寫進 Evidence
        // （那是設定頁那條執行緒的事），所以這裡直接問 SettingsUi 的狀態。
        var sw = Stopwatch.StartNew();
        while (!SettingsUi.IsOpen && sw.ElapsedMilliseconds < 5000) Thread.Sleep(25);
        bool uiOpen = SettingsUi.IsOpen;
        Report("the open-settings command reached the settings page", uiOpen,
               $"SettingsUi.IsOpen={SettingsUi.IsOpen} after {sw.ElapsedMilliseconds} ms");
        SettingsUi.Close();
        var swClose = Stopwatch.StartNew();
        while (SettingsUi.IsOpen && swClose.ElapsedMilliseconds < 3000) Thread.Sleep(25);
        Report("the settings page closes again (the process can exit)", !SettingsUi.IsOpen,
               $"SettingsUi.IsOpen={SettingsUi.IsOpen} after {swClose.ElapsedMilliseconds} ms");

        // 關掉之後 shell 不可以還留著它。ProbeShellStillHasIcon 在 Dispose 之後一律回 false
        // （它本身就是「還留著嗎」），所以這裡同時檢查 NIM_DELETE 有回報成功。
        tray.Dispose();
        Report("the shell no longer holds the icon after dispose",
               !tray.ProbeShellStillHasIcon(), "ProbeShellStillHasIcon() == false");

        if (tempSettings && File.Exists(settingsPath))
        {
            // 這個暫存檔是自測自己產生的（設定頁的 Save 路徑可能會寫它），所以自己清掉。
            try { File.Delete(settingsPath); } catch (IOException) { /* 清不掉不改變判定 */ }
        }

        evidence.Line($"tray: selftest done: checks={checks} failed={failed} ok={failed == 0}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// M1g：`--autostart-probe &lt;subkey&gt;`——在指定的 HKCU 子機碼上完整走一次
    /// 「不存在 → 啟用 → 讀回 → 停用 → 不存在」。
    ///
    /// **只碰被指定的子機碼**。真正的 Run 機碼不會被這個入口動到，理由見 <see cref="Autostart"/>：
    /// 驗收若直接寫真機碼，每一次回歸都會悄悄改掉使用者的開機啟動設定。
    /// </summary>
    internal static int RunAutostartProbe(string subkeyPath, Evidence evidence)
    {
        int failed = 0;
        int checks = 0;
        void Report(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failed++;
            evidence.Line($"autostart: {(ok ? "PASS" : "FAIL")} {name}" +
                          (detail.Length > 0 ? $"  [{detail}]" : string.Empty));
        }

        if (string.IsNullOrWhiteSpace(subkeyPath))
        {
            evidence.Line("autostart: FAIL no subkey given (--autostart-probe <subkey>)");
            return 1;
        }
        if (subkeyPath.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase))
        {
            // 防手滑：這個入口的整個存在理由就是「不要碰真機碼」。
            evidence.Line($"autostart: REFUSED — '{subkeyPath}' is the real Run key; " +
                          "use a scratch subkey (this entry exists so acceptance runs cannot change the user's autostart)");
            return 1;
        }

        var autostart = new Autostart(subkeyPath, evidence.Line);
        string expected = autostart.ExpectedValue();
        evidence.Line($"autostart: probe subkey=HKCU\\{subkeyPath} expected={expected}");
        Report("the expected value is a quoted absolute path to this exe",
               expected.Length > 2 && expected.StartsWith('"') && expected.EndsWith('"') &&
               expected.Contains(ProductInfo.Slug, StringComparison.OrdinalIgnoreCase),
               expected);
        Report("absent before the probe", autostart.CurrentValue() == null && !autostart.IsEnabled(),
               $"current={(autostart.CurrentValue() ?? "(absent)")}");

        autostart.Enable();
        string after = autostart.CurrentValue() ?? "(absent)";
        Report("enabled and the value round-trips", after == expected && autostart.IsEnabled(), $"current={after}");

        // 反向對照：值存在但路徑不對時必須回報「未啟用」。少了這一條，「只看值存不存在」的實作
        // 也會通過，而那正是「換了安裝位置之後畫面顯示已啟用、開機卻跑舊 exe」的成因。
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subkeyPath, writable: true);
            key?.SetValue(ProductInfo.Name, "\"C:\\somewhere\\else\\stale.exe\"",
                          Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            evidence.Line($"autostart: could not write the stale-path control value ({ex.GetType().Name}: {ex.Message})");
        }
        Report("a stale path does not read as enabled", !autostart.IsEnabled(),
               $"current={autostart.CurrentValue() ?? "(absent)"}");

        autostart.Enable();          // 覆寫回正確值（Enable 必須能改掉一個錯的值）
        Report("enable repairs a stale value", autostart.CurrentValue() == expected && autostart.IsEnabled(),
               $"current={autostart.CurrentValue() ?? "(absent)"}");

        autostart.Disable();
        Report("disabled and the value is gone", autostart.CurrentValue() == null && !autostart.IsEnabled(),
               $"current={(autostart.CurrentValue() ?? "(absent)")}");

        autostart.Disable();         // no-op，不是錯誤
        Report("disabling twice is a no-op, not an error", autostart.CurrentValue() == null,
               "second Disable() logged 'already disabled'");

        evidence.Line($"autostart: probe done: checks={checks} failed={failed} ok={failed == 0}");
        return failed == 0 ? 0 : 1;
    }

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
    /// 規格 §7.3 的失敗清單：畫成 PNG，並且**每一列的資料都寫成日誌**（`detail row: file=… reason=…`）。
    ///
    /// 為什麼列資料也要寫進日誌：驗收要斷言的是「每一列都有檔名、都有**中文**原因」。
    /// 只驗圖片的話，「原因不見了」只表現為字變少；只驗日誌的話，畫不出來的失敗又看不到。
    /// 兩者都留，才不會出現「檢查通過但使用者畫面上什麼都沒有」。
    ///
    /// 帶 `--convert &lt;壞檔&gt;` 時，變體 `real` 的列**來自一次真的轉檔失敗**（與 UI 路徑同一條
    /// worker 管線），不是合成字串——合成字串永遠會通過，包含那些產品根本產生不出來的字串。
    /// </summary>
    internal static int RunProgressDumpDetail(Options opt, Evidence evidence)
    {
        ConversionSummary? realSummary = null;
        if (!string.IsNullOrEmpty(opt.ConvertPayload))
        {
            // 真的跑一次轉檔。壞檔失敗**是預期結果**（exit 9），不是這個 dump 的失敗，
            // 所以這裡不看它的回傳碼——它只是資料來源。
            evidence.Line("detail dump: --convert given, so the 'real' variant comes from a real conversion");
            realSummary = ConvertOnce(opt, evidence);
            evidence.Line($"detail dump: real rows={realSummary.FailureRows.Count} " +
                          "(from the conversion just run)");
        }

        string outPath = !string.IsNullOrEmpty(opt.ProgressDumpDetail)
            ? opt.ProgressDumpDetail
            : opt.ProgressDumpDetailRaw;
        string? rawPath = string.IsNullOrEmpty(opt.ProgressDumpDetailRaw) ? null : opt.ProgressDumpDetailRaw;
        if (string.IsNullOrEmpty(outPath) && string.IsNullOrEmpty(opt.ProgressDumpDetailJson)) return 10;

        string? directory = string.IsNullOrEmpty(outPath)
            ? Path.GetDirectoryName(Path.GetFullPath(opt.ProgressDumpDetailJson))
            : Path.GetDirectoryName(Path.GetFullPath(outPath));
        try { if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory); }
        catch (Exception ex)
        {
            evidence.Line($"progress-dump-detail: cannot create {directory}: {ex.Message}");
            return 10;
        }

        // D4：寬度上限＝工作區寬 − 2 × 24，取自**產品自己用的那個機制**（MonitorLayout →
        // `GetMonitorInfoW` 的 rcWork，行程是 PerMonitorV2 所以單位是實體像素）。
        // 無頭 dump 也走這一條：驗收斷言的「上限」若與畫面上的上限不同，那條斷言只是在
        // 驗一個不存在於產品裡的數字。
        // 注意：這裡算出來的 cap 是**這台機器**的；PNG 的實際寬度在下面逐變體寫出來，
        // 兩者都寫進日誌，所以「有沒有真的被夾住」是可比的。
        Rectangle workArea = ProgressPresenter.WorkAreaAt(0, 0);
        int cap = ProgressPresenter.MaxPanelWidthFor(workArea);
        evidence.Line($"detail dump: work area={workArea.X},{workArea.Y},{workArea.Width},{workArea.Height} " +
                      $"cap={cap} (0 = unlimited: the work area could not be queried)");

        var shortRows = new List<ConversionFailureRow>
        {
            new("a.png", "無法解碼"),
            new("b.heic", "檔案被鎖定"),
        };
        var longRows = new List<ConversionFailureRow>
        {
            new("2026-09-30 家庭旅遊 第 12 批 全部照片 最終版 真的最終版.png", "無法解碼"),
            new("b.heic", "檔案被鎖定"),
        };
        // 9 列 = 上限 8 ＋ 1 筆溢出。「有沒有真的停在上限」只能靠這種超過上限的輸入驗出來。
        var manyRows = new List<ConversionFailureRow>();
        for (int i = 1; i <= 9; i++) manyRows.Add(new ConversionFailureRow($"shot{i:00}.jpg", "無法解碼"));
        // D4：極端檔名（200 個字元）。沒有這一列，「極長檔名不得把面板撐到無邊」這條
        // 就沒有東西可以驗——上面最長的變體只有 540 px，遠低於上限。
        //
        // 兩個變體是刻意的：`extreme` 的檔名全是同一種字元（中略後頭尾等長，看不出
        // 「中略」與「尾巴截斷」的差別），`extreme-mid` 的檔名頭尾不同（前面是日期、
        // 後面是 `.png`），所以「頭尾都被保留、省略號在中間、副檔名還在」是可以逐字驗的。
        var extremeRows = new List<ConversionFailureRow>
        {
            new(new string('W', 200) + ".png", "無法解碼"),
            new("b.heic", "檔案被鎖定"),
        };
        var extremeMidRows = new List<ConversionFailureRow>
        {
            // 32 組 `NNNN-NNNN-`（320 個字元 ＋ `.png`）：頭是數字與減號、尾是 `.png`，
            // 兩端長得不一樣，所以「中略的頭真的是檔名開頭、尾真的是檔名結尾」可以逐字驗。
            // 字數要夠多到**真的超過上限**，否則這一列根本不會被縮（第一版 204 個字元量出來
            // 只有 1756 px，第二版 244 個字元是 2018 px，都在 2512 px 的上限之下，
            // 於是那兩條斷言什麼都沒驗到——這是量出來的，不是猜的）。
            new(string.Concat(Enumerable.Range(0, 32).Select(_ => "2222-9999-")) + ".png", "無法解碼"),
            new("b.heic", "檔案被鎖定"),
        };

        int failed = 0;
        int variants = 0;
        var stats = new List<DumpVariant>();
        DumpOne(evidence, cap, "short", shortRows,
                Panels(outPath, "short")!, Panels(rawPath, "short"), stats, ref failed);
        variants++;
        DumpOne(evidence, cap, "long", longRows,
                Panels(outPath, "long")!, Panels(rawPath, "long"), stats, ref failed);
        variants++;
        DumpOne(evidence, cap, "many", manyRows,
                Panels(outPath, "many")!, Panels(rawPath, "many"), stats, ref failed);
        variants++;
        DumpOne(evidence, cap, "extreme", extremeRows,
                Panels(outPath, "extreme")!, Panels(rawPath, "extreme"), stats, ref failed);
        variants++;
        DumpOne(evidence, cap, "extreme-mid", extremeMidRows,
                Panels(outPath, "extreme-mid")!, Panels(rawPath, "extreme-mid"), stats, ref failed);
        variants++;

        if (realSummary != null)
        {
            DumpOne(evidence, cap, "real", realSummary.FailureRows,
                    Panels(outPath, "real")!, Panels(rawPath, "real"), stats, ref failed);
            variants++;
            foreach (ConversionFailureRow row in realSummary.FailureRows)
                // reason 一律用引號夾起來：空的 reason 若寫成 `reason=` 後面沒東西，
                // 驗收端就沒有東西可以斷言（讀到的是空字串而不是「這裡少了一個原因」）。
                evidence.Line($"detail row: file={row.FileName} reason='{row.Reason}'");
        }
        else
        {
            evidence.Line("detail dump: no --convert payload, so there is no real-conversion row " +
                          "(the acceptance suite drives that variant with a deliberately broken file)");
        }

        // 藥丸那一行也要一起留：清單是藥丸的展開，兩者寬度都必須依文字量測。
        // 短／長兩個變體讓「文字變長 → 圖片變寬」可以在同一次執行裡比較（固定寬度會被這條抓到）。
        // ⚠ 檔名要與 panel 變體**分開**：同名的話藥丸會蓋掉剛剛寫出的面板 dump，
        //    而驗收只會看到「檔案存在」——那種覆寫不會有任何錯誤訊息。
        using (Bitmap shortPill = ProgressPresenter.Compose("a.png 無法解碼", 9, 10, true, 1, 255))
        {
            string pillPath = Panels(outPath, "pill-short")!;
            shortPill.Save(pillPath, System.Drawing.Imaging.ImageFormat.Png);
            evidence.Line($"detail dump line: variant=pill-short width={shortPill.Width} height={shortPill.Height} " +
                          $"rows=1 cap={ProgressPresenter.MaxDetailRows} -> {pillPath}");
        }
        variants++;
        using (Bitmap longPill = ProgressPresenter.Compose(
                   "2026-09-30 家庭旅遊 第 12 批 全部照片 最終版 真的最終版.png 無法解碼", 9, 10, true, 1, 255))
        {
            string pillPath = Panels(outPath, "pill-long")!;
            longPill.Save(pillPath, System.Drawing.Imaging.ImageFormat.Png);
            evidence.Line($"detail dump line: variant=pill-long width={longPill.Width} height={longPill.Height} " +
                          $"rows=1 cap={ProgressPresenter.MaxDetailRows} -> {pillPath}");
        }
        variants++;

        // D4 的證據檔（可選）。放在最後才寫：它要涵蓋**所有**變體，包括上面的真的轉檔那一列。
        if (!string.IsNullOrEmpty(opt.ProgressDumpDetailJson))
            WriteDetailLayoutJson(evidence, opt.ProgressDumpDetailJson, workArea, cap,
                                  ProgressPresenter.MaxDetailRows, stats);

        evidence.Line($"progress dump detail done: variants={variants} failed={failed}");
        return failed == 0 ? 0 : 7;
    }

    /// <summary>
    /// 變體檔名：`panel.png` ＋ `short` → `panel-short.png`。
    /// 呼叫端傳進來的路徑本身就是驗收指定的位置，所以這裡只加後綴，不改目錄——
    /// 驗收要看的是「它自己指定的那個檔名旁邊有什麼」，不是另一個地方的一堆檔案。
    /// </summary>
    private static string? Panels(string? path, string suffix) =>
        string.IsNullOrEmpty(path) ? null : ReplaceFileName(path, suffix);

    /// <summary>畫一個變體並寫出兩個檔（flat ＋ raw）。回傳 flat 的路徑（失敗時 null）。</summary>
    private static string? DumpOne(Evidence evidence, int cap, string name, IReadOnlyList<ConversionFailureRow> rows,
                                  string flatPath, string? rawPath,
                                  List<DumpVariant> stats,
                                  ref int failed)
    {
        string? flatTarget = null;
        try
        {
            flatTarget = flatPath;
            // ComposeDetail 回傳的 texts 就是**交給 GDI+ 的那一份**，
            // 不是另外再排版一次（兩次排版就可能不一致，而驗收會驗到一份沒有被畫出來的字串）。
            var (panel, texts) = ProgressPresenter.ComposeDetail(rows, 255, cap);
            using (panel)
            {
                using (var flat = new Bitmap(panel.Width, panel.Height,
                                             System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(flat))
                    {
                        g.Clear(Color.FromArgb(255, 32, 34, 38));
                        g.DrawImageUnscaled(panel, 0, 0);
                    }
                    flat.Save(flatTarget, System.Drawing.Imaging.ImageFormat.Png);
                }
                if (rawPath != null) panel.Save(rawPath, System.Drawing.Imaging.ImageFormat.Png);

                int shown = Math.Min(rows.Count, ProgressPresenter.MaxDetailRows);
                // `cap=` 是**列數上限**（MaxDetailRows），`widthCap=` 才是 D4 的寬度上限。
                // 舊欄位名不動：驗收套件已經在讀它，改名會讓舊斷言靜默地永遠不匹配。
                //
                // `fileNameLength` 是給驗收用的：`extreme` 這一列的檔名有 204 個字元，
                // 「cap 是有效的上限」與「這一列真的長到會被上限夾住」是兩件不同的事，
                // 只有前者成立的話，那條斷言會在輸入變短時靜默地變成裝飾品。
                int longestName = rows.Count == 0 ? 0 : rows.Max(r => r.FileName.Length);
                evidence.Line($"detail dump line: variant={name} width={panel.Width} height={panel.Height} " +
                              $"rows={rows.Count} shown={shown} " +
                              $"overflow={rows.Count - shown} cap={ProgressPresenter.MaxDetailRows} " +
                              $"widthCap={cap} fileNameLength={longestName} -> {flatTarget}");
                var laidOutRows = new List<DumpRowLayout>();
                for (int i = 0; i < shown; i++)
                    laidOutRows.Add(new DumpRowLayout(rows[i].Text, texts[i], rows[i].FileName,
                                                      LaidOutFileName(rows[i].Text, texts[i], rows[i].Reason),
                                                      rows[i].Reason));
                if (rows.Count > shown)
                {
                    // 溢出那一列沒有「原字串」可言（它的字是組出來的），但它的排版結果要留著：
                    // 驗收對「每一列都要有完整原因」的斷言會把 `…還有 N 筆` 排除掉（原因為空），
                    // 而它是不是真的被畫出來，由像素的墨跡帶數已經驗過了。
                    laidOutRows.Add(new DumpRowLayout(texts[texts.Count - 1], texts[texts.Count - 1],
                                                      "", texts[texts.Count - 1], ""));
                }
                stats.Add(new DumpVariant(name, panel.Width, panel.Height, laidOutRows));
            }
        }
        catch (Exception ex)
        {
            failed++;
            evidence.Line($"detail dump line: variant={name} FAILED {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        return flatTarget;
    }

    /// <summary>
    /// 一個 dump 變體的一列：原始那一列的字、**真正交給 GDI+ 的字**，以及那一列的兩欄
    /// （檔名／原因）。
    ///
    /// 為什麼要連檔名與原因都寫出來：驗收要斷言的「檔名被中略、原因沒被動」是**兩欄之間**
    /// 的關係（哪一段是哪一段）。只給合併後的字串，驗收就只能用「用空白切開」去猜界線，
    /// 而檔名本來就可能含空白——那條斷言會在遇到空白檔名時靜默地變成錯的。
    ///
    /// <see cref="LaidOutFileName"/> 是**排版後**的檔名本身（被中略的那一串）。它與
    /// <see cref="FileName"/>（原始檔名）的差別是驗收唯一能分辨「中略」與「重排」的地方：
    /// 中略後的檔名比較短，但它的頭是原檔名的頭、尾是原檔名的尾。
    /// </summary>
    private sealed record DumpRowLayout(string Text, string LaidOut, string FileName, string LaidOutFileName,
                                       string Reason);

    /// <summary>一個 dump 變體的結果（寬高、以及每一列的排版）。</summary>
    private sealed record DumpVariant(string Name, int Width, int Height, List<DumpRowLayout> Rows);

    /// <summary>
    /// D4 的證據檔：真正交給 GDI+ 的每一列字串（`--progress-dump-detail-json`）。
    ///
    /// 為什麼不只用日誌：日誌行裡的字串混在幾百行其他輸出之間，而且中略號與空格在
    /// 複製貼上時會走樣；這個檔案是排版結果本身，驗收端可以直接讀。
    ///
    /// 為什麼要有 `laidOutWidth`／`ellipsisIndex`：驗收要分辨「中略」與「被尾巴截掉」，
    /// 而在純文字上兩者長得很像（都有 `…`，只是位置不同）。把中略號的**字元位置**寫出來，
    /// 驗收就不必自己猜，也不必用會誤判的 `StartsWith` 比對。
    ///
    /// `text` 是排版前的原字串，`laidOut` 是排版後的字串：兩者一起寫出來，
    /// 驗收才驗得到「正常長度的那一列一個字都沒被動過」。
    /// </summary>
    private static void WriteDetailLayoutJson(Evidence evidence, string path, Rectangle workArea, int cap,
                                             int maxRows, List<DumpVariant> stats)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"workArea\":[{workArea.X},{workArea.Y},{workArea.Width},{workArea.Height}],");
        sb.Append($"\"maxWidthPx\":{cap},");
        sb.Append($"\"maxRows\":{maxRows},");
        sb.Append("\"variants\":[");
        for (int v = 0; v < stats.Count; v++)
        {
            DumpVariant variant = stats[v];
            if (v > 0) sb.Append(',');
            sb.Append($"{{\"name\":{Json(variant.Name)},\"width\":{variant.Width}," +
                      $"\"height\":{variant.Height},\"rows\":[");
            for (int i = 0; i < variant.Rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                DumpRowLayout row = variant.Rows[i];
                string layout = row.LaidOut;
                sb.Append($"{{\"index\":{i}," +
                          $"\"fileName\":{Json(row.FileName)},\"fileNameLength\":{row.FileName.Length}," +
                          $"\"laidOutFileName\":{Json(row.LaidOutFileName)}," +
                          $"\"laidOutFileNameLength\":{row.LaidOutFileName.Length}," +
                          $"\"reason\":{Json(row.Reason)},\"reasonLength\":{row.Reason.Length}," +
                          $"\"text\":{Json(row.Text)},\"laidOut\":{Json(layout)}," +
                          $"\"laidOutLength\":{layout.Length}," +
                          $"\"ellipsisIndex\":{layout.IndexOf('\u2026')}," +
                          $"\"laidOutWidth\":{MeasureTextForDump(layout)}}}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            evidence.Line($"detail dump json: wrote {path} " +
                          $"(workAreaWidth={workArea.Width} maxWidthPx={cap} variants={stats.Count})");
        }
        catch (Exception ex)
        {
            evidence.Line($"detail dump json: FAILED to write {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 排版後那一列裡的**檔名**部分。未被改寫時就是原字串；被縮短時等於「排版後的字串
    /// 減掉尾巴的原因與那一個分隔空白」。
    ///
    /// 為什麼要在 dump 裡把這件事算出來，而不是讓驗收自己切：切錯界線不會有任何錯誤，
    /// 只會得到一個看起來很有道理的錯檔名——那正是這個專案在消滅的第二個真相來源。
    /// </summary>
    private static string LaidOutFileName(string text, string laidOut, string reason)
    {
        if (laidOut == text) return text;
        string suffix = reason.Length == 0 ? string.Empty : " " + reason;
        return suffix.Length > 0 && laidOut.EndsWith(suffix)
            ? laidOut.Substring(0, laidOut.Length - suffix.Length)
            : laidOut;
    }

    /// <summary>JSON 字串（含控制字元跳脫）。手寫是刻意的：這個 dump 不引入任何新套件。</summary>
    private static string Json(string text)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// JSON 裡回報的「這一列有多寬」：**問繪製端自己**（同一支字型、同一個格式），
    /// 不在這裡另寫一份量測——兩份量測就是第二個真相來源，而它會在下一次改字型時默默分歧。
    /// </summary>
    private static int MeasureTextForDump(string text) =>
        (int)Math.Ceiling(ProgressPresenter.MeasureDetailRowWidth(text));


    /// <summary>`C:\x\panel.png` ＋ `short` → `C:\x\panel-short.png`。</summary>
    private static string ReplaceFileName(string path, string suffix)
    {
        string? dir = Path.GetDirectoryName(path);
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(dir) ? $"{name}-{suffix}{ext}" : Path.Combine(dir, $"{name}-{suffix}{ext}");
    }

    /// <summary>
    /// 從**真實轉檔路徑**（`--wheel-payload`）dump 規格 §7.3 的面板。
    ///
    /// 為什麼需要這一條，而不是只留 `--convert` 那一條：`--convert` 直接呼叫引擎，
    /// 訊息由 <c>ConversionResult.UserMessage</c> 當場組出來；而 UI 路徑的訊息是**跨管道**
    /// 送過來、再被拆回「檔名 ＋ 原因」。兩條路都可能出錯，而且錯法不同：
    /// 前者是訊息組錯，後者是拆錯（拆錯不會拋例外，只會顯示成一個錯的檔名）。
    /// 只有走這一條，驗收才能把「拿掉中文原因」這種壞掉方式照出來。
    ///
    /// 沒有給 `--progress-dump-detail*` 時完全不做任何事（一般執行不受影響）。
    /// </summary>
    private static void DumpDetailPanels(Options opt, Evidence evidence, List<ConversionFailureRow> rows)
    {
        if (string.IsNullOrEmpty(opt.ProgressDumpDetail) && string.IsNullOrEmpty(opt.ProgressDumpDetailRaw)
            && string.IsNullOrEmpty(opt.ProgressDumpDetailJson))
            return;
        string outPath = !string.IsNullOrEmpty(opt.ProgressDumpDetail)
            ? opt.ProgressDumpDetail
            : opt.ProgressDumpDetailRaw;
        string? rawPath = string.IsNullOrEmpty(opt.ProgressDumpDetailRaw) ? null : opt.ProgressDumpDetailRaw;

        // 上限與畫面同一條規則（工作區寬 − 2 × 24）。這一條路徑是**跨管道的真實資料**，
        // 它也要受同一個上限約束，否則「上限」只在無頭 dump 裡成立。
        Rectangle workArea = ProgressPresenter.WorkAreaAt(0, 0);
        int cap = ProgressPresenter.MaxPanelWidthFor(workArea);

        int failed = 0;
        var stats = new List<DumpVariant>();
        if (!string.IsNullOrEmpty(outPath))
            DumpOne(evidence, cap, "real", rows, Panels(outPath, "real")!, Panels(rawPath, "real"), stats, ref failed);
        if (!string.IsNullOrEmpty(opt.ProgressDumpDetailJson))
            WriteDetailLayoutJson(evidence, opt.ProgressDumpDetailJson, workArea, cap,
                                  ProgressPresenter.MaxDetailRows, stats);
        // 舊的斷言讀的是這一行（`variant=real-pipeline rows=N failed=0`），所以欄位順序不動，
        // widthCap 只能加在後面。
        evidence.Line($"detail dump line: variant=real-pipeline rows={rows.Count} " +
                      $"failed={failed} (rows came from the worker over the pipe, not from --convert) " +
                      $"[widthCap={cap}]");
    }

    /// <summary>
    /// M1d：不需要滑鼠就能跑完整條轉檔管線——**驗證必須靠這個，不是靠人工拖曳**。
    /// 同時也是使用者可用的「命令列轉檔」入口（明天測試時很好用）。
    /// </summary>
    internal static int RunConvert(Options opt, Evidence evidence)
    {
        ConversionSummary summary = ConvertOnce(opt, evidence);
        return summary.AllOk ? 0 : 9;
    }

    /// <summary>
    /// 跑一次 `--convert` 並回傳摘要。與 <see cref="RunConvert"/> 只差在「回傳什麼」：
    /// 失敗清單的 dump 需要**那一次的列資料**（真的失敗，不是合成字串），
    /// 而重跑一次轉檔會產生第二個輸出檔、也會多一次 worker 啟動——那不是驗收想看的東西。
    /// </summary>
    internal static ConversionSummary ConvertOnce(Options opt, Evidence evidence)
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
        return summary;
    }

    /// <summary>
    /// 規格 §7.3 的行為自測：不必畫面、不必滑鼠，驗四件會直接影響使用者的事。
    ///
    /// 為什麼值得一個入口：`IsExpanded` 一開始必須是 false（§7.3 的「清單**不自動彈出**」），
    /// 而這個性質最容易在重構（例如「完成時順手展開失敗清單」）中被弄丟，且**不會有任何錯誤**。
    /// 另外三項（ToggleDetail 真的翻動它、矩形跟著展開長大、Contains 同意那塊矩形）
    /// 是「接上真實點擊之後」命中判定的全部依據——現在不驗，等於把風險留給那一天。
    /// </summary>
    internal static int RunProgressDetailSelfTest(Evidence evidence)
    {
        int failed = 0;
        int checks = 0;
        void Report(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failed++;
            evidence.Line($"detail: {(ok ? "PASS" : "FAIL")} {name}" +
                          (detail.Length > 0 ? $"  [{detail}]" : string.Empty));
        }

        using var layer = new VisualLayer(evidence);
        using var progress = new ProgressPresenter(evidence, layer);
        var snapshot = new ConversionSnapshot(9, 10, null, true, 9, 1,
            new[] { "broken.png 無法解碼" }, null, new[] { @"C:\tmp\broken.png" });

        // 1) 尚未顯示任何東西：沒有矩形、沒有展開。
        Report("nothing is expanded before any toggle (spec 7.3: the list never pops up by itself)",
               !progress.IsExpanded, $"expanded={progress.IsExpanded}");
        Report("no rectangle exists before anything was drawn",
               progress.PillRect == Rectangle.Empty && !progress.Contains(0, 0),
               $"rect={progress.PillRect}");

        // 2) 先畫（Update 就是 tick 走的同一條路），再問它自己的矩形。
        progress.Start(500, 400);
        progress.Update(snapshot);
        Rectangle pill = progress.PillRect;
        Report("after drawing, the pill knows its own rectangle",
               pill.Width > 0 && pill.Height > 0, $"rect={pill.X},{pill.Y},{pill.Width},{pill.Height}");
        int cx = pill.X + pill.Width / 2, cy = pill.Y + pill.Height / 2;
        Report("Contains agrees with the rectangle it just reported (hit test is not a second geometry)",
               progress.Contains(cx, cy) && !progress.Contains(pill.X - 5, pill.Y - 5),
               $"center=({cx},{cy}) outside=({pill.X - 5},{pill.Y - 5})");
        Report("drawing the pill is NOT what expands the list (spec 7.3)",
               !progress.IsExpanded, $"expanded={progress.IsExpanded}");

        // 3) 展開：狀態要翻過去，而且畫面（矩形）要真的變大。
        progress.ToggleDetail();
        Rectangle expanded = progress.PillRect;
        Report("ToggleDetail expands the list", progress.IsExpanded, $"expanded={progress.IsExpanded}");
        Report("the expanded list is bigger than the pill it came from",
               expanded.Width >= pill.Width && expanded.Height > pill.Height,
               $"pill={pill.Width}x{pill.Height} expanded={expanded.Width}x{expanded.Height}");
        Report("the hit rectangle follows the expanded list",
               progress.Contains(expanded.X + expanded.Width / 2, expanded.Y + expanded.Height - 4),
               $"rect={expanded.X},{expanded.Y},{expanded.Width},{expanded.Height}");

        // 4) 再切一次＝收起，而且矩形要回到藥丸大小（不是卡在大的）。
        progress.ToggleDetail();
        Rectangle collapsed = progress.PillRect;
        Report("ToggleDetail collapses again", !progress.IsExpanded, $"expanded={progress.IsExpanded}");
        Report("collapsing restores the pill-sized rectangle",
               collapsed.Height == pill.Height && collapsed.Width == pill.Width,
               $"pill={pill.Width}x{pill.Height} collapsed={collapsed.Width}x{collapsed.Height}");

        // 5) 新的一次轉檔＝清單收起（否則上一次失敗的清單會留在畫面上）。
        progress.ToggleDetail();
        progress.Start(500, 400);
        Report("a new conversion starts with the list collapsed",
               !progress.IsExpanded, $"expanded={progress.IsExpanded}");

        evidence.Line($"detail: selftest done: checks={checks} failed={failed} ok={failed == 0}");
        return failed == 0 ? 0 : 1;
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
        string missing = Path.Combine(Path.GetTempPath(), $"{ProductInfo.Slug}-missing-{Guid.NewGuid():N}.png");
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

        // 2026-10-01（方向 D）：FlipEdge 改為回傳**輪盤本體**的左上角（以前是「框」，
        // 框含外擴＋8px 陰影留白，於是輪盤本體實際落在游標 +32，與規格 §4.3 的 +18 不符）。
        // D 的柔暈外緣 ≈ 39.4px，留白由 MarginPx 往外長；視窗原點仍是游標 − 24（驗收腳本不變）。
        var work = new Native.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1032 };
        var (fx, fy) = WheelGeometry.FlipEdge(2500, 1000, work);
        Report($"flip at bottom-right -> ({fx},{fy}), want (2262,762)", fx == 2262 && fy == 762);

        var (gx, gy) = WheelGeometry.FlipEdge(100, 100, work);
        Report($"no flip near the top-left -> ({gx},{gy}), want (118,118)", gx == 118 && gy == 118);

        var tiny = new Native.RECT { Left = 0, Top = 0, Right = 200, Bottom = 200 };
        var (hx, hy) = WheelGeometry.FlipEdge(100, 100, tiny);
        Report($"clamped when the work area is smaller than the wheel -> ({hx},{hy}), want (42,42)",
               hx == WheelGeometry.MarginPx && hy == WheelGeometry.MarginPx);

        Report($"wheel diameter = {WheelGeometry.DiameterPx} px (want 220)", WheelGeometry.DiameterPx == 220);
        Report($"margin around the wheel = {WheelGeometry.MarginPx} px (want 42)",
               WheelGeometry.MarginPx == 42);
        Report($"bitmap = {WheelGeometry.BitmapWidthPx}x{WheelGeometry.BitmapHeightPx} (want 304x349)",
               WheelGeometry.BitmapWidthPx == 304 && WheelGeometry.BitmapHeightPx == 349);
        Report($"cursor sits {WheelGeometry.CursorInBitmapPx} px into the bitmap (want 24: origin = cursor-24)",
               WheelGeometry.CursorInBitmapPx == 24);

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
