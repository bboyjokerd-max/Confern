using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
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
            // 證據檔：**一個 worker 一個檔**。以前是全部共用 `worker.log`，而 Evidence 以
            // 獨占模式開啟它 ⇒ 第二個同時存在的 worker 一開檔就炸（見 WorkerLog 的說明）。
            // `--log` 有指定時照舊不覆蓋（那是呼叫端明確要的路徑，而鏡射仍然會維護共用名——
            // 讀取者要的是那個穩定檔名，而不是「誰寫的」）。
            string workerLog = opt.LogPath ?? WorkerLog.ForJob(opt.WorkerPipe);
            // 一個工作一個檔，所以要有上限：清掉 7 天前的舊檔。用年齡而不是「保留最新 N 個」，
            // 因為後者會刪到**正在跑**的 worker 的檔案（見 WorkerLog.PruneStale）。
            WorkerLog.PruneStale(TimeSpan.FromDays(7));
            var workerEvidence = new Evidence(workerLog, opt.Quiet);
            var workerMirror = new WorkerLog.Mirror(workerLog, WorkerLog.SharedPath(),
                                                    opt.WorkerPipe, workerEvidence.Line);
            workerEvidence.Mirrored += workerMirror.Flush;
            try
            {
                // ⚠ 處置順序：`Evidence` 最後那一行（`=== session end ===`）在 Dispose 裡寫，
                // 而它會觸發鏡射。鏡射因此必須**比證據晚**被停用，否則共用檔會少一個結尾。
                // 這一層 try/finally 就是那個順序的唯一保證。
                return WorkerServer.Run(opt.WorkerPipe, workerEvidence);
            }
            finally
            {
                workerEvidence.Dispose();
                workerMirror.Dispose();
            }
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

        // 待辦 5 · D1：把 WM_LBUTTONUP 送進**真實的 PillWindow.WndProc**，驗展開／收合、
        // 視窗矩形與 hover。同樣不必有畫面、不必動滑鼠（訊息是直接送給那個視窗的）。
        if (opt.PillSelfTest)
        {
            Native.AttachParentConsole();
            using var pillEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunPillSelfTest(opt, pillEvidence);
        }

        // 待辦 5 · D1/D2：把可點的藥丸顯示出來並活著 N 毫秒，讓外部注入合成輸入。
        if (opt.PillInteractiveMs > 0)
        {
            Native.AttachParentConsole();
            using var liveEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunPillInteractive(opt, liveEvidence);
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

        // M3 第五顆扇形（2026-10-07 切片 2）：`--qr-worker` 走**真的 worker 行程**。
        //
        // 它補的是前一個切片誠實留下的缺口：`--read-qr` 是同一個行程內直接呼叫引擎，
        // 所以 `op=read-qr` 的分派臂與 `WorkerMessage.text` 這個**跨行程的欄位**
        // 當時沒有任何執行時斷言走過，只能用原始碼斷言代替。這一條起真的 worker、
        // 走真的管道，而且斷言 client 收到的文字逐字元等於已知 payload。
        // 位置同樣在單一實例守衛之前（理由與 `--crop` 一字不差）。
        if (!string.IsNullOrEmpty(opt.QrWorker))
        {
            Native.AttachParentConsole();
            using var qrWorkerEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return QrDiagnostic.RunWorker(qrWorkerEvidence, opt.QrWorker!);
        }

        // M3 第五顆扇形（2026-10-07 切片 2）：分類自測——**完全不碰 WPF**。
        // 分類決定了一件有副作用的事（開外部程式），所以它必須能在任何環境下被驗到。
        if (opt.QrClassifySelfTest)
        {
            Native.AttachParentConsole();
            using var qrClassifyEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return QrDiagnostic.RunClassify(qrClassifyEvidence);
        }

        // M3 第五顆扇形（2026-10-07 切片 2）：`--qr-open-selftest`——文字視窗 ＋ 開瀏覽器的縫。
        // ⚠ 它會**真的開一張 WPF 視窗**（在螢幕上、關得掉），但**絕不開瀏覽器**
        // （見 QrBrowserOpener 的兩道鎖；這是這個切片最重要的一條安全斷言）。
        if (opt.QrOpenSelfTest)
        {
            Native.AttachParentConsole();
            using var qrOpenEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return QrDiagnostic.RunTextWindow(qrOpenEvidence, opt.QrSource ?? opt.ReadQr, opt.QrExtraUrl);
        }

        // M3 第六顆扇形（2026-10-07）：`--merge-pdf`——走**完整的一條路**（worker 行程、
        // op=merge-pdf、管道、快照）。位置同樣在單一實例守衛之前（理由與 `--crop` 一字不差）。
        if (!string.IsNullOrEmpty(opt.MergePdf))
        {
            Native.AttachParentConsole();
            using var mergeEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.MergePdfDiagnostic(mergeEvidence, opt);
        }

        // M3 第六顆扇形（2026-10-07）：`--merge-pdf-inspect <file>`——印出一個 PDF 的頁數與
        // **逐頁**尺寸／旋轉／內容流雜湊。**只讀不寫**，而且它是套件唯一能客觀斷言頁序的縫
        // （見 <see cref="Options.MergePdfInspect"/>）。同樣在守衛之前。
        if (!string.IsNullOrEmpty(opt.MergePdfInspect))
        {
            Native.AttachParentConsole();
            using var inspectEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.MergePdfInspectPdf(inspectEvidence, opt.MergePdfInspect!);
        }

        // M3 第七顆扇形（2026-10-08）：`--remove-bg`——走**完整的一條路**（worker 行程、
        // op=remove-bg、管道、快照）。位置同樣在單一實例守衛之前（理由與 `--crop` 一字不差）。
        //
        // ⚠ 它**刻意走真的 worker**而不是同一個行程內的引擎：模型路徑是這個 op 唯一跨管道的
        // 新事實（WorkerMessage.removeBgModel），而「UI 找到模型、worker 找不到」正是同一個
        // 行程內的呼叫**永遠驗不到**的那件事（兩邊的工作目錄不同）。
        if (!string.IsNullOrEmpty(opt.RemoveBg))
        {
            Native.AttachParentConsole();
            using var removeBgEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RemoveBgDiagnostic(removeBgEvidence, opt);
        }

        // M3 第七顆扇形（2026-10-08）：`--remove-bg-inspect <png>`——**只讀**量一張圖的 alpha
        // 統計。它是套件唯一能從**產品自己的產物**回答「主體留下了、背景不見了」的縫
        // （見 <see cref="Options.RemoveBgInspect"/>）。同樣在守衛之前。
        if (!string.IsNullOrEmpty(opt.RemoveBgInspect))
        {
            Native.AttachParentConsole();
            using var inspectEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RemoveBgInspectPng(inspectEvidence, opt.RemoveBgInspect!);
        }

        // M3 第四顆扇形：`--crop`——走**完整的一條路**（worker 行程、op=crop、管道、快照）。
        //
        // ⚠ 位置刻意在單一實例守衛**之前**（與 `--convert` 同一條規矩）：守衛是為了擋下第二個
        // **常駐**行程，而這條診斷跑完就結束。若把它放在守衛之後，使用者自己開著 Confern 的時候
        // （＝最可能想跑它的時候）這條診斷只會回一個「已經有一個在跑」的 3，什麼都量不到。
        if (!string.IsNullOrEmpty(opt.Crop))
        {
            Native.AttachParentConsole();
            using var cropEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.CropDiagnostic(cropEvidence, opt);
        }
        // M3 第四顆扇形：`--tools-list`——把工具層**有哪些扇區**變成一個不必開視窗、不必滑鼠、
        // 也不必搶單一實例就能問的事實。
        //
        // 為什麼需要它：工具層的組成原本只能從 `--wheel-payload --wheel-tools` 那條路看出來，
        // 而那一條會跑起完整的常駐路徑（會撞單一實例守衛、會建覆蓋層）。「有哪幾顆工具」是這條
        // 產品線最容易被改動、也最該被釘住的一件事（加一顆＝改協議、改輪盤、改介面），
        // 所以它必須隨時問得到——包含使用者自己開著 Confern 的時候。
        if (opt.ToolsList)
        {
            Native.AttachParentConsole();
            using var toolEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunToolsList(opt, toolEvidence);
        }

        // M3 第四顆扇形：裁切視窗的生命週期與互動自測（不必畫面、**不必滑鼠**）。
        // 與上面的 `--crop` 是兩條不同的路：這一條驗的是**視窗**，那一條驗的是**引擎與協議**。
        if (opt.CropUiSelfTest || !string.IsNullOrEmpty(opt.CropUiDump))
        {
            Native.AttachParentConsole();
            using var cropUiEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunCropUiSelfTest(opt, cropUiEvidence);
        }

        // M3 第六顆扇形（切片 B）：合併排序視窗的生命週期與互動自測（不必輪盤、**不必滑鼠**）。
        // 與 `--merge-pdf` 是兩條不同的路：這一條驗的是**視窗**，那一條驗的是**引擎與協議**。
        if (!string.IsNullOrEmpty(opt.MergeUiSelfTest) || !string.IsNullOrEmpty(opt.MergeUiDump))
        {
            Native.AttachParentConsole();
            using var mergeUiEvidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
            return App.RunMergeUiSelfTest(opt, mergeUiEvidence);
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
            return App.RunAutostartProbe(opt.AutostartProbe, opt.AutostartAckPath,
                                         opt.AutostartAckTimeoutMs > 0 ? opt.AutostartAckTimeoutMs : 5000,
                                         startupEvidence);
        }

        // 單一實例守衛的**診斷接縫**：只問「這個行程拿不拿得到常駐槽位」，回報完就放掉。
        //
        // 為什麼這一條非有不可：守衛本身是「安靜地不做事」，而「安靜地不做事」跟
        // 「根本沒在作用」在外部看起來一模一樣。有了它，「守衛有效」才是一個當場量得到的事實，
        // 而不是讀程式碼相信的事。它**必須**排在下面那一行守衛之前——否則它問到的會變成
        // 「守衛有沒有把我擋掉」。
        if (opt.SingleInstanceProbe)
        {
            return SingleInstance.Probe(opt.LogPath ?? "");
        }

        // 單一實例守衛（待辦）。A13 要求「被強制結束後能立刻重啟」，
        // 因此這裡不留下任何需要清理的檔案鎖。
        //
        // ⚠ **這就是「常駐 GUI 模式」的判定點——判準是位置，不是旗標。**
        // 所有診斷分支（含 `--worker`）都已經在前面 return 掉了，所以「能走到這一行」
        // 就等於「沒有任何診斷旗標被設定過」＝這是唯一那條會裝全域鉤子、建托盤、
        // 蓋一層常駐覆蓋層的路。守衛必須留在這裡，而且**只**留在這裡：
        //   · 上移一格會讓 worker（每個工作一個，天生要同時存在多個）被擋 ⇒ 轉檔全壞；
        //   · 下移一格沒有意義（那時視窗與鉤子已經建好了，正是要防的事）。
        // 細節與「為什麼不用旗標判定」的理由見 Startup/SingleInstance.cs。
        using SingleInstance instance = SingleInstance.Acquire();
        if (!instance.IsOwner)
        {
            return SingleInstance.Refuse(opt.LogPath ?? "");
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
          --raise-with-hooks        diagnostic: exactly --raise, EXCEPT the low-level hooks ARE
                                    installed (same wiring as the resident path). This is the only
                                    mode that both receives a synthetic OLE drag and has a keyboard
                                    hook, so it is the only headless way to observe
                                    "wheel layer switch: ..." while Shift is injected.
          --wheel-layer-probe <png> diagnostic: show the wheel (format layer) for that image, then
                                    send a REAL synthetic Shift keydown, confirm the switch to the
                                    tool layer, send keyup, confirm the switch back; exit 0 when both
                                    happened, 1 otherwise. The keys are injected from another thread
                                    so the WH_KEYBOARD_LL callback can actually run; nothing here
                                    calls the layer switch directly (see the method comment).
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
          --wheel-tools             with --wheel-demo: paint the SECOND layer (tool wheel) instead
          --wheel-tool <id>         with --wheel-payload: select that tool sector by id (e.g.
                                    compress) before Enter commits it, so the commit really goes
                                    through the worker process (spec 9.1); product code never reads it
          --rotate90 <path>         run tool rotate90 (90 deg clockwise) on <path> and report;
                                    the output folder follows Settings.OutputFolder (spec 9.5),
                                    the source file is never touched
                                    (the only way to check the alpha contract: the glass body
                                    must be alpha 255, everything outside must be a pure layer)
          --strip-metadata <path>   run tool strip-metadata on <path> and report; JPEG loses its
                                    metadata in the container (pixels stay byte-identical),
                                    everything else is decoded and re-encoded without metadata;
                                    the source file is never touched
          --compress <path>         run tool compress on <path> and report; the level comes from the
                                    settings file (compressionMode / compressionTargetKb), never
                                    from the command line; the source file is never touched
          --read-qr <path>          run tool read-qr on <path> and print the decoded text; NO file is
                                    ever written (this tool's product is text, not a file) and the
                                    source file is never touched; a picture without a QR code is
                                    refused by name (QrNotFound), not with an exception
          --qr-worker <path>        the same tool through the real WORKER process (op=read-qr over
                                    the pipe): proves the decoded text survives the cross-process
                                    field, which an in-process call cannot show; still writes nothing
          --merge-pdf <p1;p2;...>   run tool merge-pdf on these files IN THIS ORDER through the real
                                    WORKER process (op=merge-pdf over the pipe); the semicolon
                                    order IS the page order (a;b and b;a are different requests).
                                    The inputs may be PDFs (whole pages are copied) or images
                                    (each image becomes one page); mixing the two is refused;
          --merge-page-size <mode>  with --merge-pdf: original | first | a4 (default original)
                                    the product semantics ruled by the user on 2026-10-07 are
                                    fail-fast: if ANY file cannot be merged, NOT ONE output file is
                                    written and the message names the offending file and why; the
                                    sources are never touched
          --merge-pdf-inspect <file>
                                    print one PDF's page count and, per page, its size in points,
                                    its /Rotate and the length and SHA-256 of its decoded content
                                    stream; READ ONLY (it never writes) and it reads a file that is
                                    already on disk. This is the only objective way to assert page
                                    ORDER: merge-pdf's product is a binary, so "is page 3 really
                                    fx-c's second page" can only be answered from each page's own
                                    geometry and content
          --remove-bg <path>        run tool remove-bg on <path> through the real WORKER process
                                    (op=remove-bg over the pipe) and report; the output is a PNG of
                                    the SAME SIZE as the source with the background made
                                    transparent. Three states are named and write NOTHING: the
                                    portrait model is missing (ModelUnavailable), the model file is
                                    corrupt or half-downloaded (ModelCorrupt - a DIFFERENT message),
                                    and "no portrait subject in this picture" (NoSubjectFound: the
                                    model is a PORTRAIT model, so a QR code or a landscape gives an
                                    almost empty mask); the source is never touched
          --remove-bg-model <file>  with --remove-bg: which model file to use (default: models\
                                    model_fp16.onnx beside the executable). This is the seam that
                                    lets the suite walk the missing and the corrupt state for real;
                                    the product path always uses the default location
          --remove-bg-inspect <png> READ ONLY: print one PNG's size, its alpha statistics (alpha
                                    present, min/max/mean over the whole alpha channel, the fraction
                                    of fully opaque and fully transparent pixels) and the alpha mean
                                    of the centre band against the four corner bands. This is what
                                    makes "the subject is opaque and the background is not" a
                                    measurement instead of a claim; it writes nothing
          --remove-bg-refine <mode> with --remove-bg: edge refinement of the alpha matte, guided |
                                    off (default guided). The model only ever sees a 512-edge tensor,
                                    so on a 1920x1281 photo each mask pixel covers 2.5 image pixels;
                                    "guided" runs a guided filter (He et al. 2010) with the SOURCE
                                    LUMINANCE as the guide and puts the high-frequency edge structure
                                    back into the alpha at full resolution. "off" writes the raw
                                    upscaled mask, i.e. the bytes this build produced before the
                                    refinement existed -- that is the control arm of the measurement
          --qr-classify-selftest    print the http/https classification of 18 known inputs (and
                                    whether the browser target agrees) and exit; touches no window
                                    and never starts a process
          --qr-open-selftest        decode --qr-source, then exercise BOTH outcomes of read-qr: a
                                    non-url really opens the text window (character-for-character
                                    checked, copy button pressed, single instance, reopen) and the
                                    url path goes through the browser seam instead of a real browser
                                    (so the suite never pops a browser on this machine)
          --qr-source <path>        the image with a QR code used by --qr-open-selftest (default: the
                                    --read-qr path)
          --qr-extra-url <url>      with --qr-open-selftest: the url routed through the seam to prove
                                    the url arm is really taken (default https://example.com/...)
          --crop <p1;p2>            run tool crop on these files through the real WORKER process
                                    (op=crop over the pipe) and report; one normalized rectangle is
                                    applied to every file, each according to its own size;
                                    the source files are never touched
          --crop-rect x,y,w,h       the NORMALIZED crop rectangle (0..1, '.' as the decimal point).
                                    This is the no-mouse seam: it is what dragging in the crop window
                                    would have produced, so the whole path can be verified without
                                    touching the user's mouse
          --crop-ratio <id>         aspect preset for --crop: free | 1:1 | 16:9 | 9:16 | 4:3 | 3:4 | original | original-flipped
                                    (default free)
          --crop-ui-selftest        open the crop window, set a rectangle programmatically, drag with
                                    synthetic coordinates, confirm, close and reopen (no screen, no mouse)
          --crop-ui-dump <png>      render the crop window to a PNG (offscreen composite, not a screen
                                    capture) so the layout and the absence of scrollbars can be asserted
          --crop-source <path>      the image shown by --crop-ui-selftest / --crop-ui-dump
          --merge-ui-selftest <a;b;c>
                                    open the MERGE PDF sorting window with this order, move a row,
                                    remove a row, confirm, cancel and reopen (no screen, no mouse).
                                    It drives the same methods the real drag handlers call
          --merge-ui-dump <png>     render the merge sorting window to a PNG (offscreen composite);
                                    the row order comes from --merge-ui-selftest
          --tools-list              print the tool layer's sectors (ids, labels, icons) and exit;
                                    no window, no mouse, and it works while Confern is already running
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
          --pill-selftest           send WM_LBUTTONUP through the REAL pill WndProc and verify the
                                    expand/collapse state, the window rectangle and the hover events (no mouse)
          --pill-interactive <ms>   show the clickable pill (with a failure list) on screen for N ms, so
                                    acceptance can inject a real click / cursor dwell and watch the fade
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
          --single-instance-probe   ask whether THIS process can take the resident single-instance
                                    slot, print the answer and exit: 0 = yes (no resident Confern is
                                    running), 3 = no (a resident Confern already holds it). It never
                                    becomes resident, installs no hooks, creates no window and
                                    releases the slot again before it exits
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

    /// <summary>
    /// `--raise-with-hooks`：**與 <see cref="ForceRaiseHitLayer"/> 完全同一件事，差別只在鉤子照裝**。
    ///
    /// 為什麼不是直接改 `--raise`：`--raise` 是 m1a 套件（tools/verify-m1a.ps1，本次不得修改）
    /// 的 S3b／S4／S5 用的診斷，而那三節是**對著它現在這個行為**驗收過的（「沒有鉤子」是它當時
    /// 被量到的性質之一）。改它會讓那三節量到的東西在沒有人改腳本的情況下悄悄換掉。
    /// 這一條是**新增**的第三種組合，任何既有路徑一個位元組都不動。
    ///
    /// 結構上的缺口（2026-10-05 第五段量到的）：`--raise` 送得達真的 OLE 拖放但沒有鍵盤鉤子；
    /// 常駐路徑有鉤子，合成拖放卻送不達（日誌停在 trigger key DOWN (state=Idle)，沒有 DragEnter）。
    /// 於是「Shift 顯示期間切層」那兩行沒有任何一條自動化路徑碰得到。加上這一條之後有：
    ///   ① 接收層**開場就升起**（不靠觸發鍵，合成拖放送得達）；
    ///   ② 鉤子裝著（Shift 有聽眾），而且接線與常駐路徑是**同一份**（見 <see cref="WireHooks"/>）。
    /// </summary>
    public bool RaiseWithHooks;

    /// <summary>
    /// `--wheel-layer-probe &lt;png&gt;`：只驗一件事——輪盤顯示期間的 Shift 切層**走的是鍵盤鉤子那條路**。
    /// 見 <see cref="WheelLayerProbe"/>。
    /// </summary>
    public string? WheelLayerProbe;
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
    /// M3（2026-10-05）：讓 `--wheel-demo` 畫**第二層（工具輪盤）**而不是格式輪盤。
    /// 扇區清單來自 <see cref="ToolCatalog"/>（唯一權威），所以 dump 出來的圖就是產品真的會畫的東西
    /// ——不是另外畫一張示意圖。這樣「第二層長什麼樣」才有一份可重複產生的證據。
    /// </summary>
    public bool WheelTools;
    /// <summary>
    /// 診斷（2026-10-06）：`--wheel-tool &lt;id&gt;` 搭配 <c>--wheel-payload</c>，把工具輪盤的選取
    /// **指名**移到某一顆工具上（例如 <c>compress</c>），再讓 Enter 走真正的提交。
    ///
    /// 為什麼非有它不可：`--compress` 那條診斷走的是**行程內的引擎**，它驗不到規格 §9.1 的
    /// <c>op</c> 欄位——也就是「UI 真的把 compress 這個 op 交給 worker 行程」。
    /// 沒有這個旗標，那一段話就只能靠讀程式碼相信。產品路徑不讀它。
    /// </summary>
    public string? WheelTool;
    /// <summary>
    /// M3 診斷：`--rotate90 &lt;path&gt;` 直接對這個檔案跑工具 `rotate90`（順時針 90 度）。
    /// **走的是 worker 用的同一個引擎**，但同一個行程內直接呼叫——跨管道的 `op` 欄位還沒接。
    /// 存在的理由：工具的「能不能做」必須先是可重複量測的事實，才准出現在介面上。
    /// </summary>
    public string? Rotate90;
    /// <summary>
    /// M3 診斷（2026-10-06）：`--strip-metadata &lt;path&gt;` 直接對這個檔案跑工具 `strip-metadata`。
    /// 形狀與 <see cref="Rotate90"/> 一字不差（同一個引擎、同一條設定→命名→寫檔鏈），
    /// 差別只在它驗的是「**像素不變、metadata 消失**」而不是「長寬對調」。
    /// </summary>
    public string? StripMetadata;
    /// <summary>
    /// M3 診斷（2026-10-06）：`--compress &lt;path&gt;` 直接對這個檔案跑工具 `compress`（第三顆扇形）。
    /// 形狀與 <see cref="Rotate90"/>／<see cref="StripMetadata"/> 一字不差（同一個引擎、
    /// 同一條設定→命名→寫檔鏈）。
    ///
    /// ⚠ 等級**不從命令列來**：它從設定檔來（<see cref="Settings.ToCompressionPolicy"/>）。
    /// 這是刻意的——診斷要驗的正是「設定頁選的那一級真的走到引擎」，多開一個 `--compress-mode`
    /// 旗標等於讓驗收繞過那條鏈，驗到一個產品裡不存在的組合。
    /// </summary>
    public string? Compress;
    /// <summary>
    /// M3 診斷（2026-10-07）：`--read-qr &lt;path&gt;` 直接對這個檔案跑工具 `read-qr`（第五顆扇形）。
    /// 形狀與 <see cref="Rotate90"/>／<see cref="StripMetadata"/> 一字不差（同一個引擎、同一個行程內），
    /// 差別在**它沒有任何輸出的檔案**：產物是一段文字，印到 stdout。
    ///
    /// ⚠ 這一條的 stdout 是**驗收的斷言對象**（見 <see cref="ReadQrDiagnostic"/>）：
    /// 「解出來的文字」必須能從產品的輸出**重新推導**出來，而不是只相信它印的 `ok=True`。
    /// </summary>
    public string? ReadQr;
    /// <summary>
    /// M3 診斷（2026-10-07）：`--merge-pdf &lt;p1;p2;...&gt;` 對這幾個 PDF（**依序**）跑工具
    /// `merge-pdf`（第六顆扇形），走**完整的一條路**（worker 行程、`op=merge-pdf`、管道、快照）
    /// ——形狀與 <see cref="Crop"/> 一字不差（它也是分號分隔的一次批次）。
    ///
    /// ⚠ **分號的順序就是頁序**，這不是排版而是這個工具的定義：`a;b` 與 `b;a` 是兩個不同的請求，
    /// 必須產出兩個頁序不同的檔。所以這一條診斷的輸出必須讓驗收**反推得出順序**
    /// （見 <see cref="MergePdfInspect"/>：逐頁印尺寸與旋轉）。
    ///
    /// ⚠ 這一條**走真的 worker**而不是同一個行程內的引擎：合併是唯一一個「多輸入、單輸出」的 op，
    /// 而「順序有沒有跨過管道」在另一端完全看不出來——一個弄丟順序的實作會產生一個
    /// 頁序全錯、但每一個數字都對的檔。同一個行程內的呼叫驗不到那件事。
    /// </summary>
    public string? MergePdf;

    /// <summary>
    /// `--merge-page-size <original|first|a4>`：合併時的頁面大小（2026-10-14）。
    /// 這是**診斷接縫**，正式路徑的權威是排序視窗上的選擇（見 MergeWindow.PageSize）。
    /// 預設 original＝每一頁是那張圖自己的實際尺寸。
    /// </summary>
    public string? MergePageSize;
    /// <summary>
    /// M3 診斷（2026-10-07）：`--merge-pdf-inspect &lt;file&gt;` 印出一個 PDF 的**頁數**與
    /// **每一頁的尺寸（point）與旋轉角度**——**只讀、不寫**，而且讀的是**已經在磁碟上的檔**
    /// （它不會、也不能讀一個剛剛 `Save()` 過的記憶體物件：PDFsharp 在存檔之後會把那個物件鎖住，
    /// 連 `PageCount` 都會丟 `InvalidOperationException`，見 `ConversionEngine.MergePdf`）。
    ///
    /// 為什麼這是套件**唯一**能客觀斷言頁序的手段：合併的產物是一個二進位檔，而「第 3 頁是不是
    /// `fx-c` 的第 2 頁」只能從**每一頁自己的幾何**看出來。所以 fixture 的每一頁尺寸都不同
    /// （見 `fixtures/pdf/README.md`），而這一條把那個序列印成**固定、可被 regex 反解**的格式：
    ///
    /// <code>
    /// merge-pdf inspect: page=&lt;n&gt; size=&lt;w&gt;x&lt;h&gt; rotate=&lt;deg&gt; contents=&lt;bytes&gt; sha=&lt;12 hex&gt;
    /// merge-pdf inspect: file=&lt;name&gt; pages=&lt;N&gt; read=&lt;N&gt; ok=True
    /// merge-pdf inspect done: file=&lt;name&gt; pages=&lt;N&gt; read=&lt;N&gt; ok=True
    /// </code>
    ///
    /// ⚠ `pages=` 是文件**自己宣稱**的頁數（`PageCount`），`read=` 是**真的唸完**的頁數——
    /// 兩者只有全部成功時才相等。分開印是因為它們會在壞檔上不一致，而那個不一致正是
    /// #388 的形狀（見 <see cref="MergePdfInspectPdf"/> 內的說明）。
    ///
    /// ⚠ 尺寸印**一個小數位**（`Point` 的原始值是 double；`XUnit.FromPoint(200).Point` 可能是
    /// 199.99999…）。四捨五入到小數一位讓 fixture 的期望值可以寫成人看得懂的数字，
    /// 而 0.1 pt 的解析度對「這是不是原來那一頁」完全足夠（fixture 之間差 10 pt 以上）。
    /// </summary>
    public string? MergePdfInspect;

    /// <summary>
    /// M3 診斷（2026-10-08）：`--remove-bg &lt;path&gt;` 對這個檔案跑工具 `remove-bg`
    /// （**第七顆扇形，M3 的最後一顆**），走**完整的一條路**（worker 行程、`op=remove-bg`、
    /// 管道、快照）——形狀與 <see cref="Crop"/> 一字不差。
    ///
    /// ⚠ 這一條**走真的 worker**而不是同一個行程內的引擎：模型路徑是這個 op **唯一**
    /// 跨管道的新事實（<c>WorkerMessage.removeBgModel</c>），而「UI 找到模型、worker 找不到」
    /// 是同一個行程內的呼叫**永遠驗不到**的那件事（兩邊的工作目錄不同）。
    ///
    /// ⚠ 它把三種狀態都當成**正常結果**印出來（回傳碼 3），而不是讓它們變成例外：
    /// 缺模型、模型損壞、找不到人像主體，三者的共同點是**一個檔案都不寫**。
    /// 回傳碼：0 成功產出 PNG、1 來源被動到（規格 §5 紅線）、2 參數或來源檔本身的問題、
    /// 3 引擎拒絕（三種狀態之一）——「正確的拒絕」與「這條診斷壞了」必須分得開。
    /// </summary>
    public string? RemoveBg;

    /// <summary>
    /// M3 診斷（2026-10-08）：`--remove-bg-model &lt;file&gt;` 指定去背要用哪一顆模型
    /// （預設＝執行檔旁邊的 `models\model_fp16.onnx`）。
    ///
    /// **為什麼這條接縫非有不可**：這顆模型不進版控，所以「檔案不在」是一個使用者真的會遇到的
    /// 狀態；而「檔案在但是壞的」是另一個（下載中斷）。兩個狀態的訊息**刻意不同**
    /// （見 <see cref="ConversionError.ModelUnavailable"/>／<see cref="ConversionError.ModelCorrupt"/>），
    /// 而要有辦法斷言那件事，就必須能把引擎指向一個不存在的檔與一個被截斷的檔。
    /// 正式路徑（輪盤 → commit → worker）**一律用預設位置**，不讀這個旗標。
    /// </summary>
    public string? RemoveBgModel;

    /// <summary>
    /// M3 診斷（2026-10-08，全解析度邊緣精修那一刀）：`--remove-bg-refine off|guided`——
    /// 指定去背的 alpha 要不要做引導濾波精修（見 <see cref="EdgeRefine"/>；**預設是開的**）。
    ///
    /// **為什麼這條接縫非有不可**：精修的整個賣點是「同一張輸入、只有這一個開關不同時，
    /// 邊緣細節變多」——那是**同一張圖的兩個版本之間的比較**，而沒有這個旗標就沒有第二次
    /// 測量的立足點（把預設改掉再重跑一次不是一個可重複的實驗）。它同時也是使用者
    /// 遇到「這一刀不喜歡」時的唯一退路（設定檔的 <c>RemoveBgRefine</c> 在正式路徑上做同一件事）。
    ///
    /// 認不得的值＝**拒絕並回 2**（不靜默退回預設）：與 <see cref="RemoveBgModel"/> 同一條規矩，
    /// 診斷的參數打錯要當場看得出來。
    /// </summary>
    public string? RemoveBgRefine;

    /// <summary>
    /// M3 診斷（2026-10-08）：`--remove-bg-inspect &lt;png&gt;`——**只讀**印出一張 PNG 的
    /// 尺寸、alpha 統計（有沒有 alpha 通道、min/max/mean、全不透明與全透明的像素比例）
    /// 以及「中央帶 vs 四角帶」的平均 alpha。
    ///
    /// **為什麼這是整個切片的關鍵**：去背的產物是一個二進位檔，而「主體留下來了、背景不見了」
    /// 不可以靠產品的 `ok=True` 回答——那只證明它自己覺得成功了。這一條把**成品自己的像素**
    /// 變成可斷言的數字，所以套件是拿**產品寫出來的檔**在比對，不是拿產品的報告在比對。
    ///
    /// 輸出格式（固定、可被 regex 反解，改它要先改 <c>tools/verify-m3d.ps1</c> 的 S22）：
    /// <code>
    /// remove-bg inspect: file=&lt;name&gt; size=&lt;w&gt;x&lt;h&gt; format=&lt;pf&gt; hasAlpha=&lt;True|False&gt;
    /// remove-bg inspect: alpha min=&lt;f4&gt; max=&lt;f4&gt; mean=&lt;f4&gt; opaque=&lt;f4&gt; clear=&lt;f4&gt;
    /// remove-bg inspect: matte band=&lt;f4&gt; edge=&lt;f4&gt; (fraction of pixels in the 0&lt;a&lt;255 transition; mean |grad alpha| inside it, 0..1 per pixel)
    /// remove-bg inspect: band centre=&lt;f4&gt; corner=&lt;f4&gt; top=&lt;f4&gt; bottom=&lt;f4&gt; left=&lt;f4&gt; right=&lt;f4&gt;
    /// remove-bg inspect done: file=&lt;name&gt; ok=True
    /// </code>
    /// ⚠ <c>corner=</c> 是**四個角帶的平均**（每一個角帶是各邊 10 %），所以它是「背景」最粗的
    /// 代理指標；中央帶是中間 40 %（0.30–0.70）。
    /// </summary>
    public string? RemoveBgInspect;

    /// <summary>
    /// M3 診斷（2026-10-07 切片 2）：`--qr-worker &lt;path&gt;` 對這個檔案跑工具 `read-qr`，
    /// 但**走真的 worker 行程**（`op=read-qr`、管道、快照）——形狀與 <see cref="Crop"/> 一字不差。
    ///
    /// 為什麼非有它不可（前一個切片誠實留下的缺口）：`--read-qr` 走的是**同一個行程內的引擎**，
    /// 它驗不到 `op` 欄位，也驗不到 `WorkerMessage.text` 這個**跨行程的序列化欄位**。
    /// 「worker 裡面解對了」與「UI 那一端收到同一串字」是兩件不同的事，而後者才是產品真正走的路。
    /// </summary>
    public string? QrWorker;
    /// <summary>
    /// M3 診斷（2026-10-07 切片 2）：`--qr-classify-selftest`——只驗分類（**完全不碰 WPF**）。
    ///
    /// 分開一條的理由：分類決定了一件有副作用的事（開外部程式），而它可以、也應該在
    /// **任何**環境下被驗到——包含沒有互動式工作階段的機器。視窗那一條（<see cref="QrOpenSelfTest"/>）
    /// 需要一個真的 WPF 視窗，這兩者失敗的原因完全不同，合成一條會讓診斷的訊息說不出是哪一種壞。
    /// </summary>
    public bool QrClassifySelfTest;
    /// <summary>
    /// M3 診斷（2026-10-07 切片 2）：`--qr-open-selftest`——讀 QR 的**兩條出路**自測。
    ///
    /// 它驗的是這一刀最貴的兩個承諾：
    ///   ①非網址 → 文字視窗真的開得起來、裡面的字**逐字元**等於解出來的內容；
    ///   ②⚠ **開瀏覽器那條路徑在自測模式下沒有真的開瀏覽器**（見 <see cref="QrBrowserOpener"/>）——
    ///     否則套件一跑就會在使用者的桌面上彈出瀏覽器，那是對使用者環境的副作用。
    /// 搭配 <see cref="QrSource"/> 指定有 QR 的圖（不給就用 <c>--read-qr</c> 那一張）。
    /// </summary>
    public bool QrOpenSelfTest;
    /// <summary>`--qr-open-selftest` 要解的那張有 QR 的圖（預設沿用 `--read-qr` 的路徑）。</summary>
    public string? QrSource;
    /// <summary>`--qr-open-selftest` 額外走一次路由的網址（預設一個例子），證明 url 那一條也真的被走過。</summary>
    public string QrExtraUrl = "https://example.com/confern-qr-selftest";
    /// <summary>
    /// M3 診斷（2026-10-06）：`--crop &lt;p1;p2&gt;` 走**完整的一條路**（worker 行程、`op=crop`、
    /// 管道、快照）對這幾個檔案跑工具 `crop`（第四顆扇形）。分號分隔＝一次批次，
    /// 而批次正是「同一比例套用到每一張」唯一的驗法。
    ///
    /// 與前三個工具診斷的差別（刻意的）：前三條是**同一個行程內直接呼叫引擎**，
    /// 這一條起真的 worker 行程。理由是裁切帶著**五個新的協議欄位**
    /// （<c>cropX</c>…<c>cropRatio</c>）——那五個欄位有沒有真的跨過管道，只有走管道才驗得到。
    /// 一個「在本地算得好好的」矩形，跟一個「跨了管道之後還算得對」的矩形，
    /// 壞掉的原因完全不同（後者最可能的壞法是欄位沒送／被當成 0）。
    /// </summary>
    public string? Crop;
    /// <summary>
    /// `--crop-rect x,y,w,h`：**正規化**的裁切矩形（0..1，小數點固定是「.」）。
    ///
    /// 這個旗標就是「無滑鼠的診斷接縫」：裁切的互動是拖曳，而本專案 2026-10-06 起
    /// **預設跑套件絕不碰使用者的滑鼠**。所以拖曳的產物（一個正規化矩形）必須能從命令列餵進來，
    /// 讓整條路徑（矩形 → worker → 引擎 → 每一張圖各自的像素矩形 → 寫檔）在沒有任何輸入下被驗。
    /// 它同時也是 `--wheel-payload --wheel-tools --wheel-tool crop` 的矩形來源：
    /// 少了它，那條診斷會去開裁切視窗並永遠等不到人按確認。
    /// </summary>
    public string CropRect = "";
    /// <summary>
    /// `--crop-ratio &lt;id&gt;`：比例預設（`free`／`1:1`／`16:9`／`9:16`／`4:3`／`3:4`／
    /// `original`／`original-flipped`，見 <see cref="CropRatios"/>）。空＝`free`。
    /// 認不得的值會被拒絕並說出來（不靜默退回）。
    ///
    /// ⚠ 這裡列的是 **<see cref="CropRatios.All"/>（合法值集合）**，不是按鈕列的
    /// <see cref="CropRatios.Presets"/>：`9:16` 等三個直式 id 沒有自己的按鈕（它們是視窗上
    /// 「方向」鈕切出來的），但它們**是**合法的命令列值與合法的 worker 值。
    /// </summary>
    public string CropRatio = "";
    /// <summary>
    /// `--crop-ui-selftest`：裁切視窗的生命週期與互動自測（不必畫面、**不必滑鼠**）。
    /// 見 <see cref="App.RunCropUiSelfTest"/>；形狀比照 `--settings-ui-selftest`。
    /// </summary>
    public bool CropUiSelfTest;
    /// <summary>
    /// `--crop-ui-dump &lt;png&gt;`：把裁切視窗畫成一張 PNG（**離屏合成**，不是螢幕擷取），
    /// 讓驗收能斷言「視窗長什麼樣、有沒有捲軸」。搭配 `--crop-rect` 可以指定圖上框的那一塊。
    /// </summary>
    public string CropUiDump = "";
    /// <summary>--crop-ui-selftest／--crop-ui-dump 要顯示的影像（第一張＝預覽那一張）。</summary>
    public string? CropUiSource;
    /// <summary>
    /// `--merge-ui-selftest a.pdf;b.pdf;c.pdf`：**合併排序視窗**的生命週期與互動自測
    /// （不必輪盤、**不必滑鼠**）。見 <see cref="App.RunMergeUiSelfTest"/>；形狀比照 `--crop-ui-selftest`。
    ///
    /// ⚠ 它驗的是**視窗本身**（初始順序、拖曳、移除、取消），與 `--merge-pdf` 驗**引擎與協議**
    ///   是兩條不同的路。兩條都要跑，「順序真的變成輸出頁序」才不只是視窗自己說的。
    /// </summary>
    public string? MergeUiSelfTest;
    /// <summary>`--merge-ui-dump &lt;png&gt;`：把排序視窗畫成一張 PNG（離屏合成），
    /// 讓驗收能斷言「視窗長什麼樣、有沒有捲軸」。內容順序來自 `--merge-ui-selftest` 的那一組檔。</summary>
    public string MergeUiDump = "";
    /// <summary>
    /// `--merge-ui-autoconfirm`：讓下一個開起來的排序視窗**自己按確認**（走真視窗的確認鈕，
    /// 不注入合成滑鼠）。搭配 `--wheel-payload … --wheel-tool merge-pdf` 時，這一條診斷會
    /// **留下來等**那個延後的合併真的跑完——這是「畫面順序 → 確認回呼 → 排進佇列 → 抽乾 →
    /// 真的產出檔案」整條正式路徑唯一能端到端被驗到的方式（見 <see cref="MergeUi.EnableAutoConfirm"/>）。
    /// 不給這個開關時，一行都不會執行，行為與加它之前逐位元相同。
    /// </summary>
    public bool MergeUiAutoConfirm;
    /// <summary>
    /// M3：`--tools-list`——把工具層的扇區清單印出來就結束（不必視窗、不必滑鼠、不必搶單一實例）。
    /// 見 <see cref="App.RunToolsList"/>。
    /// </summary>
    public bool ToolsList;
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

    /// <summary>
    /// --pill-selftest：對**真實的 <c>PillWindow.WndProc</c>** 送 <c>WM_LBUTTONUP</c>（不必動滑鼠）。
    ///
    /// 為什麼這一條與「真的合成點擊」都要有：合成點擊證明的是「使用者的手點得到」，
    /// 而這一條證明的是「點擊進到 WndProc 之後走的是展開那條路」。兩者失敗的原因不同
    /// （前者失敗＝視窗或座標錯了，後者失敗＝訊息分派或回呼錯了），所以不能只留一個。
    /// </summary>
    public bool PillSelfTest;

    /// <summary>
    /// --pill-selftest-dump &lt;png&gt;：把**展開後的合成圖**（藥丸 ＋ 失敗清單，同一張點陣圖）
    /// 寫成 PNG（不含 alpha／深底，人眼看的版本）。
    ///
    /// 為什麼需要它：展開狀態是這個功能唯一的產出，而「展開」在畫面上只表現為「一塊比較大的
    /// 紅色矩形」。若沒有這張圖，驗收只能斷言「矩形變大了」——那對「清單裡真的有字、
    /// 字沒有被裁掉」什麼都證明不了。
    /// </summary>
    public string PillSelfTestDump = "";

    /// <summary>同 <see cref="PillSelfTestDump"/>，但**保留 alpha**（唯一能取樣文字墨水的檔案）。</summary>
    public string PillSelfTestDumpRaw = "";

    /// <summary>
    /// --pill-selftest-dump-json &lt;json&gt;：展開合成圖的幾何 ＋ **真正交給 GDI+ 的那幾列字串**。
    /// 與 m1h 既有的 panel JSON 同一個理由：只驗像素的話，「字比較短」與「字被截掉」分不出來。
    /// </summary>
    public string PillSelfTestDumpJson = "";

    /// <summary>
    /// --pill-selftest-shot &lt;png&gt;：抓**畫面上真正顯示的那一塊**（視窗矩形的螢幕像素）。
    ///
    /// 這一項與 composite dump 互補，而且只有它能回答一個 dump 答不了的問題：
    /// **那個視窗在螢幕上到底有沒有畫出來**（layered window 可能沒被 composited、
    /// 可能被別的 topmost 視窗蓋住、可能位置錯了）。dump 是我們自己畫的圖，
    /// 螢幕截圖是作業系統合成的結果——後者才是使用者看到的東西。
    ///
    /// 抓不到時（遠端桌面、最小化的 session、被鎖定的畫面）**必須說出來**，
    /// 不能安靜地留一個空檔案讓驗收以為通過了。
    /// </summary>
    public string PillSelfTestShot = "";

    /// <summary>
    /// --pill-interactive &lt;ms&gt;：把藥丸（可展開的失敗清單）顯示在畫面上並活著 N 毫秒，
    /// 讓**外部**能對它注入合成輸入（真實點擊、游標停留），然後自然淡出。
    ///
    /// 為什麼需要一個獨立的診斷模式，而不是拿 `--wheel-payload` 來測：
    /// 驗收要量的是「點在藥丸上會不會展開」「游標停在上面會不會阻止淡出」，
    /// 這兩件事都需要**在藥丸還活著的時候**從外面動手。`--wheel-payload` 會依自己的節奏
    /// 一路跑到淡出才結束，驗收只有「剛好在那幾秒內插進去」才會命中——那種檢查會變成
    /// 隨時間偶爾紅的假檢查。這裡讓生命週期變成一個明確的參數。
    ///
    /// 這一條**只提供畫面與生命週期**：展開、淡出、hover 全部走正式路徑的同一組物件與同一組規則。
    /// </summary>
    public int PillInteractiveMs;
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
    /// `--autostart-ack &lt;路徑&gt;`：把「值已經被外部觀察者真的取樣到了」變成一個可等待的訊號。
    ///
    /// <para>
    /// 沒有這一條的時候，驗收只能在外面**碰運氣**取樣：探測把正確值寫進去之後，中間只隔一次
    /// 讀回、一次反向對照的寫入就改掉了——實測這個窗口只有 1 毫秒左右（日誌時間戳
    /// <c>ENABLED</c> 到 <c>DISABLED</c> 全在同一個毫秒內）。外部輪詢迴圈只要被作業系統
    /// 排程踢掉一次就會整個錯過，於是**產品完全正確而檢查變紅**（實測 400 次裡紅 3 次，
    /// 那 3 次的探測日誌都是 <c>checks=7 failed=0 ok=True</c>）。
    /// </para>
    ///
    /// <para>
    /// 有了它，探測在第一次 <c>Enable()</c> 之後**按住**那個值不放：觀察者在真的比對到預期值
    /// 的那一刻建立這個檔案，探測看到它才繼續往下走。於是「外面看得到它」不再取決於排程運氣。
    /// 這不是把斷言放寬：觀察者仍然只認「值等於預期的加引號絕對路徑」，差一格照樣紅；紅的時候
    /// 探測會在超時後照常把值改掉，檢查因此**穩定地**紅，而不是偶爾紅。
    /// 沒給這個參數時行為與以前完全相同（不等待、不多寫任何東西）。
    /// </para>
    /// </summary>
    public string AutostartAckPath = "";
    /// <summary>`--autostart-ack-timeout-ms &lt;ms&gt;`：等待上面那個訊號的上限（預設 5000）。</summary>
    public int AutostartAckTimeoutMs;
    /// <summary>
    /// --single-instance-probe：**只問不佔**——回報本行程拿不拿得到常駐槽位，然後立刻放掉。
    ///
    /// <para>
    /// 存在的理由與 <see cref="ToolsList"/> 同一類：守衛的行為是「安靜地什麼都不做」，
    /// 而「安靜地什麼都不做」與「根本沒生效」從外面看是一模一樣的。這一條把
    /// 「槽位現在被誰佔著」變成一個當場量得到的答案（離開碼 0＝拿得到／3＝被佔著），
    /// 於是「守衛有效」與「守衛被拿掉」可以用同一支腳本分辨出來。
    /// </para>
    ///
    /// <para>
    /// 它排在守衛**之前**：它是唯一被允許問「守衛會怎麼判」的診斷。不建視窗、不裝鉤子、
    /// 不碰托盤、不改設定，而且跑完就把槽位放掉。
    /// </para>
    /// </summary>
    public bool SingleInstanceProbe;
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
    public bool TriggerKeyFromCli;    /// <summary>--watchdog-seconds 是否由命令列明確指定（同上）。</summary>
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
                case "--raise-with-hooks": o.RaiseWithHooks = true; break;
                case "--wheel-layer-probe": if (i + 1 < args.Length) o.WheelLayerProbe = args[++i]; break;
                case "--assume-explorer-foreground": o.AssumeExplorerForeground = true; break;
                case "--wheel-dump": if (i + 1 < args.Length) o.WheelDump = args[++i]; break;
                case "--wheel-dump-raw": if (i + 1 < args.Length) o.WheelDumpRaw = args[++i]; break;
                case "--wheel-tools": o.WheelTools = true; break;
        case "--wheel-tool": if (i + 1 < args.Length) o.WheelTool = args[++i]; break;
                case "--rotate90": if (i + 1 < args.Length) o.Rotate90 = args[++i]; break;
                case "--strip-metadata": if (i + 1 < args.Length) o.StripMetadata = args[++i]; break;
                case "--compress": if (i + 1 < args.Length) o.Compress = args[++i]; break;
                case "--read-qr": if (i + 1 < args.Length) o.ReadQr = args[++i]; break;
                // 形狀與 `--crop` 一字不差（分號分隔＝一次批次，那正是「有序」唯一的表達方式）。
                // ⚠ 這一條**不設** CropUiSource 那種東西：merge-pdf 沒有第二個視窗要吃這個路徑
                // （排序視窗是下一個切片），所以這裡沒有對應的 ??= 那一行。
                case "--merge-pdf": if (i + 1 < args.Length) o.MergePdf = args[++i]; break;
                case "--merge-page-size": if (i + 1 < args.Length) o.MergePageSize = args[++i]; break;
                case "--merge-pdf-inspect": if (i + 1 < args.Length) o.MergePdfInspect = args[++i]; break;
                case "--remove-bg": if (i + 1 < args.Length) o.RemoveBg = args[++i]; break;
                case "--remove-bg-model": if (i + 1 < args.Length) o.RemoveBgModel = args[++i]; break;
                case "--remove-bg-refine": if (i + 1 < args.Length) o.RemoveBgRefine = args[++i]; break;
                case "--remove-bg-inspect": if (i + 1 < args.Length) o.RemoveBgInspect = args[++i]; break;
                case "--qr-worker": if (i + 1 < args.Length) o.QrWorker = args[++i]; break;
                case "--qr-classify-selftest": o.QrClassifySelfTest = true; break;
                case "--qr-open-selftest": o.QrOpenSelfTest = true; break;
                case "--qr-source": if (i + 1 < args.Length) o.QrSource = args[++i]; break;
                case "--qr-extra-url": if (i + 1 < args.Length) o.QrExtraUrl = args[++i]; break;
                case "--crop":
                    if (i + 1 < args.Length) { o.Crop = args[i + 1]; o.CropUiSource ??= args[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(); i++; }
                    break;
                case "--crop-rect": if (i + 1 < args.Length) o.CropRect = args[++i]; break;
                case "--crop-ratio": if (i + 1 < args.Length) o.CropRatio = args[++i]; break;
                case "--crop-source": if (i + 1 < args.Length) o.CropUiSource = args[++i]; break;
                case "--crop-ui-selftest": o.CropUiSelfTest = true; break;
                case "--tools-list": o.ToolsList = true; break;
                case "--crop-ui-dump": if (i + 1 < args.Length) o.CropUiDump = args[++i]; break;
                case "--merge-ui-selftest": if (i + 1 < args.Length) o.MergeUiSelfTest = args[++i]; break;
                case "--merge-ui-dump": if (i + 1 < args.Length) o.MergeUiDump = args[++i]; break;
        case "--merge-ui-autoconfirm": o.MergeUiAutoConfirm = true; break;
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
                case "--pill-selftest":
                    o.PillSelfTest = true;
                    break;
                case "--pill-selftest-dump":
                    if (i + 1 < args.Length) { o.PillSelfTestDump = args[i + 1]; i++; }
                    break;
                case "--pill-selftest-dump-raw":
                    if (i + 1 < args.Length) { o.PillSelfTestDumpRaw = args[i + 1]; i++; }
                    break;
                case "--pill-selftest-dump-json":
                    if (i + 1 < args.Length) { o.PillSelfTestDumpJson = args[i + 1]; i++; }
                    break;
                case "--pill-selftest-shot":
                    if (i + 1 < args.Length) { o.PillSelfTestShot = args[i + 1]; i++; }
                    break;
                case "--pill-interactive":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int pillMs)) { o.PillInteractiveMs = pillMs; i++; }
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
                case "--autostart-ack":
                    if (++i < args.Length) o.AutostartAckPath = args[i];
                    break;
                case "--autostart-ack-timeout-ms":
                    if (++i < args.Length && int.TryParse(args[i], out int ackMs) && ackMs > 0) o.AutostartAckTimeoutMs = ackMs;
                    break;
                case "--single-instance-probe": o.SingleInstanceProbe = true; break;
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
            // 2026-10-02 可證偽性稽核（P1-11）：這一條以前是無條件 `return 0`，所以
            // 驗收端斷言「exit code 0」其實只證明「沒有崩潰」——監視器列舉回傳 0 筆、
            // 一行 `monitor#` 都印不出來時照樣是綠的。診斷的**產物**就是那份清單，
            // 沒有產物就沒有結果可看：3 = 「跑完了，但沒有結果」（與 9 = 崩潰不同）。
            if (monitors.Length == 0)
            {
                evidence.Line("dump-monitors: the enumeration returned NO monitors -> exit 3");
                Console.Error.WriteLine("dump-monitors: no monitors were enumerated");
                return 3;
            }
            return 0;
        }

        // M1f：設定（規格 §8）必須在**建立偵測器之前**載入——觸發鍵與接收層逾時都來自它。
        // 優先順序：命令列（診斷用）> 設定檔 > 規格預設值；預設值即規格值，檔案壞掉／超範圍都會說明。
        // 選項本身也留在進程層級（CommitWheel 的裁切那一條要讀 `--crop-rect`）。
        _options = opt;
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

        if (opt.Rotate90 != null)
            return Rotate90Diagnostic(evidence, opt);
        if (opt.StripMetadata != null)
            return StripMetadataDiagnostic(evidence, opt);
        if (opt.Compress != null)
            return CompressDiagnostic(evidence, opt);
        if (opt.ReadQr != null)
            return ReadQrDiagnostic(evidence, opt);

        // 診斷：--wheel-tools 讓無頭驗收能走到第二層，不必合成鍵盤輸入（產品路徑不讀這個旗標）。
        _forceToolLayer = opt.WheelTools;

        if (opt.WheelDemoX.HasValue)
            return WheelDemo(hit, guard, evidence, opt);

        // 視覺層同樣**預先建立**：規格 §9.3 要求輪盤首次顯示 < 100 ms，不可在拖曳中才建視窗。
        using var visual = new VisualLayer(evidence);
        // 進度顯示（規格 §4.4／§7.3）改用**自己的可點視窗**（待辦 5 · D1 定案：方案 A）。
        // 為什麼不是共用的 VisualLayer：那一層**必須**有 WS_EX_TRANSPARENT（拖放要穿透它），
        // 而那個位元的意思正是「滑鼠事件穿過去」——藥丸在它上面永遠點不到，
        // 規格 §7.3 的「可點擊展開」就無從實作。外觀一行都沒改：兩者共用同一條
        // UpdateLayeredWindow 繪製路徑，幾何也仍然只有 ProgressPresenter 一份。
        //
        // 預先建立的理由與視覺層相同：轉檔開始時不可以再多花一次建視窗的成本。
        using var progressLayer = new PillWindow(evidence);
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
            // 抽乾設定頁那條執行緒排過來的日誌行。**抽乾本身只有這一個實作**（見
            // DrainSettingsUiLog）：這裡與常駐路徑的週期回呼都呼叫它，所以不存在第二個擁有者。
            DrainSettingsUiLog(evidence);
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

        // 常駐路徑的可觀測性（2026-10-05）。
        //
        // 缺口（實測，不是推論）：設定頁那條 WPF 執行緒的日誌行一律排進 _settingsUiLog，由主執行緒
        // 代寫；而抽乾點原本只有上面那個 ApplyFromSettingsUi——它掛在 visual.SettingsChanged 上，
        // 也就是**只有設定頁按下「儲存」**（或 --settings-ui-live 的那條回呼）才會被叫到。
        // 托盤那條路（OpenSettingsFromTray → LogFromSettingsUiThread）只『排隊』、不叫醒任何人，
        // 所以純托盤操作（開設定頁 → 關掉，從不存檔）跑一整輪之後，日誌裡 `settings ui:` 的行數是 0。
        // 使用者回報的 bug 正好落在那條路上：日誌在這件事上是瞎的。
        //
        // 接在哪裡：**既有**的 VisualLayer 週期回呼（TickRequested），不新增第二個計時器擁有者。
        // 它就是這個程式既有的「執行緒計時器 → PostMessage → 主執行緒 WndProc」機制
        // （Native.WM_APP_TICK 的註解說明了為什麼刻意不走 SetTimer/WM_TIMER），
        // 而抽乾**必須在主執行緒**：Evidence.Line 沒有上鎖，這正是那些行要先排隊的理由。
        // 常駐路徑沒有別的週期回呼可接——托盤是純 Win32 視窗（只被動收訊息，自己沒有任何計時器），
        // 主迴圈是阻塞式 GetMessageW；ProgressPresenter 的 80 ms tick 只活在轉檔期間。
        // 副作用只有一個：這個 tick 的訂閱者在本路徑上原本是空的（輪盤不訂它），
        // 所以這裡多做的唯一一件事，就是每 200 ms 把一個（通常是空的）佇列抽乾。
        visual.TickRequested += () => DrainSettingsUiLog(evidence);
        // 裁切視窗的確認要在**主執行緒**上接手（它要起 worker，而視覺層與進度藥丸是主執行緒的）：
        // 接在同一個週期回呼上，不新增第二個計時器。這條回呼在診斷路徑上通常沒有東西可抽。
        visual.TickRequested += () => DrainPendingCrops(evidence, runner);
        // 合併排序視窗的確認也要在**主執行緒**上接手（同一條理由：它要起 worker，而視覺層與進度
        // 藥丸是主執行緒的）。接在**同一個**既有週期回呼上，**不新增第二個計時器**——這是這個檔案
        // 已經明文寫下來的規矩（見上面那一行與 DrainPendingCrops 的說明）。
        visual.TickRequested += () => DrainPendingMerges(evidence, runner);
        // 讀 QR 的結果同樣接在這裡（2026-10-07 切片 2）：它要做的判定與副作用
        // （開瀏覽器／開文字視窗）都必須在主執行緒上發生，而這一顆工具**沒有進度藥丸**
        // 可以掛（見 ConversionRunner.Start）——週期回呼是唯一不必新增計時器擁有者的落點。
        visual.TickRequested += () => DrainQrResult(evidence, runner);
        visual.StartTick(SettingsUiLogDrainIntervalMs);
        // 這一行刻意**不**以 `settings ui:` 開頭：那個前綴的意思是「設定頁那條執行緒排過來的行」，
        // 而這一行是主執行緒自己說的（而且驗收要用 `settings ui:` 的行數來判斷抽乾有沒有生效）。
        evidence.Line($"settings ui log drain: attached to the resident tick " +
                      $"({SettingsUiLogDrainIntervalMs} ms interval; the settings page's own thread only queues)");

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
            if (opt.MergeUiAutoConfirm)
            {
                // 必須在酬載進來**之前**開好：開窗是 Enter 那一刻發生的事，而那已經在下面了。
                MergeUi.EnableAutoConfirm();
                evidence.Line("payload: --merge-ui-autoconfirm is on (the sorting window will confirm " +
                              "the order it shows, by pressing its own confirm button)");
            }
            evidence.Line($"*** INJECTED PAYLOAD (diagnostic; NOT a real DragEnter): {injected.Length} path(s) ***");
            // 依生產順序模擬：先捕捉（升起接收層），酬載才可能到達。
            guard.OnCaptured();
            ShowWheelFromPayload(presenter, catalog, evidence, injected);

            // 診斷（--wheel-tool <id>）：把選取指名移到某一顆工具上，讓下面的 Enter 提交的
            // **就是那一顆**。這是「compress 真的走 worker 行程」唯一能自動驗到的入口——
            // 沒有它，那一句話只能靠讀程式碼相信。找不到那個 id 就什麼都不做並說出來：
            // 悄悄提交別的工具會讓後面每一條斷言都變成在驗錯的東西。
            if (!string.IsNullOrEmpty(opt.WheelTool))
            {
                int want = -1;
                for (int k = 0; k < PendingTools.Count; k++)
                    if (string.Equals(PendingTools[k].Id, opt.WheelTool, StringComparison.Ordinal)) { want = k; break; }
                if (want < 0)
                    evidence.Line($"--wheel-tool '{opt.WheelTool}': not in the catalog " +
                                  $"[{string.Join(",", PendingTools.Select(t => t.Id))}] -> the selection is " +
                                  "left alone, so the commit below is NOT the tool that was asked for");
                else
                    evidence.Line($"--wheel-tool '{opt.WheelTool}': selecting index=" +
                                  $"{presenter.SelectNext(want - presenter.Selected)}");
            }

            // 2026-10-02 可證偽性稽核（P1-11）：這條診斷以前是無條件 `return 0`，
            // 所以驗收端的「exit code 0」只等於「沒有崩潰」——輪盤根本沒做出決定、
            // 或它承諾的轉檔從來沒跑完，都還是 0。這裡把「這條路徑到底得到什麼結論」
            // 變成回傳碼：0 = 有結論可讀（Ready 且真的能轉，或是刻意的拒絕狀態），
            // 3 = 跑完了但沒有結論（與 9 = 崩潰分開，呼叫端才分得出這兩件事）。
            bool verdict = presenter.Notice != null || presenter.CanCommit;

            // 拒絕狀態（混合選取／不支援）下 Enter 必須不執行，而且輪盤要留在畫面上等 Esc。
            if (opt.WheelDump != null) presenter.DumpTo(opt.WheelDump);
            bool committed = CommitWheel(presenter, guard, evidence, runner);
            // ⚠ 2026-10-07（切片 B，選項 (b) 之後）：`merge-pdf` 是**延後**提交——Enter 只開排序
            //    視窗並 `return false`。開窗是排給 WPF 執行緒的**非同步**要求（`PostToSharedUiThread`
            //    不等待，因為開 modal 視窗要是等下去就等於把主訊息迴圈凍住），所以這一條診斷
            //    必須**留下來等它真的開起來**，否則行程會在視窗建好之前就結束、把 STA 執行緒一起帶走。
            //    實測：不等的那一版，日誌到 `merge ui: open requested` 就 `=== session end ===`
            //    （+170 ms），永遠看不到 `merge ui: window created`；而 S21.07 要驗的正是
            //    「真的開了視窗、而且真的在等人」——那是「取消 ⇒ 什麼都不產出」這句話的證據。
            //    等的時候必須**幫浦訊息**（`PumpUntil`）：抽乾佇列的是主訊息迴圈裡的 `WM_APP_TICK`。
            if (!committed)
            {
                bool opened = PumpUntil(() => MergeUi.IsOpen, 15000);
                evidence.Line($"payload: merge-pdf was deferred -> the sorting window is " +
                              $"{(opened ? "OPEN and waiting for the user (the process stays alive)" : "NOT open")}");
            }
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
            //
            // ⚠ 第二個條件是 2026-10-07（切片 B）加的：`merge-pdf` 走的是**延後**提交——Enter
            //    不執行工具，只開排序視窗並 `return false`（這是刻意的：讓 `--wheel-payload` 不會
            //    去等一個永遠不會開始的工作而回報假的成功 0）。所以在這一條診斷裡，
            //    「有沒有東西要等」不是 `committed` 而是「**有沒有排進一個真的會跑的合併**」。
            //    這正是 `PendingMerges.Enqueue` → `StartMerge` 那一段唯一能端到端被驗到的地方。
            //
            //    `MergeUi.IsOpen` 是**非同步**才變成 true 的（開窗是排給 WPF 執行緒的非同步要求），
            //    所以這裡必須等它一下。實測 2026-10-07：不等就會在視窗開起來之前判定
            //    「沒有東西要等」，行程當場結束，`--merge-ui-autoconfirm` 一次都沒跑到。
            bool awaitingMerge = false;
            if (opt.MergeUiAutoConfirm)
            {
                var mergeWait = System.Diagnostics.Stopwatch.StartNew();
                while (mergeWait.ElapsedMilliseconds < 15000 && !MergeUi.IsOpen)
                    PumpUntil(() => MergeUi.IsOpen, 100);
                awaitingMerge = MergeUi.IsOpen;
                evidence.Line($"payload: the deferred merge window is {(awaitingMerge ? "open" : "NOT open")} " +
                              $"after {mergeWait.ElapsedMilliseconds} ms -> " +
                              $"{(awaitingMerge ? "waiting for the confirmed merge to drain and run" : "nothing to wait for")}");
            }
            if (awaitingMerge)
                evidence.Line("payload: merge-pdf was deferred to the sorting window; waiting for the " +
                              "merge to be confirmed and drained (--merge-ui-autoconfirm)");
            if (committed || awaitingMerge)
            {
                ConversionSnapshot final = runner.Wait(60000);
                // ⚠ 合併走的是**延後**提交，所以「runner 說它完成了」不等於「合併跑完了」：
                //    這一條診斷後面還有 `strip-metadata` 那一輪（release-path），它會把 runner 的
                //    快照重設，而且**可能在視窗還沒按確認之前就完成**。
                //
                //    ⚠⚠ 還有一個更根本的量測（2026-10-07）：**不可以**在這裡用阻塞的 `runner.Wait`。
                //    抽乾 `PendingMerges` 的是既有週期回呼（`visual.TickRequested`），而那個回呼
                //    是**主訊息迴圈**裡的 `WM_APP_TICK`；阻塞主執行緒＝那個訊息永遠不會被派送，
                //    合併就永遠不會被抽乾。實測：用 `runner.Wait(60000)` 等，日誌是
                //    「deferred merge wait = 60000 ms ... ok=0」→ 逾時後**才**看到
                //    `worker: job 1 sent (... op=merge-pdf)`，差了整整 60 秒。
                //    所以這裡改成**一邊幫浦訊息一邊等**（`PumpUntil`，它會派送佇列裡的訊息）。
                if (awaitingMerge)
                {
                    var mergeWait = System.Diagnostics.Stopwatch.StartNew();
                    while (mergeWait.ElapsedMilliseconds < 60000)
                    {
                        PumpUntil(() => runner.Snapshot.Finished && runner.Snapshot.Ok >= 1, 500);
                        if (runner.Snapshot.Finished && runner.Snapshot.Ok >= 1) break;
                    }
                    final = runner.Snapshot;
                    evidence.Line($"payload: deferred merge wait = {mergeWait.ElapsedMilliseconds} ms, " +
                                  $"finished={final.Finished} ok={final.Ok} failed={final.Failed} line='{final.Line}'");
                    if (final.Finished && final.Ok >= 1)
                    {
                        committed = true;
                        evidence.Line("payload: the deferred merge finished -> the window confirmed, " +
                                      "the queue drained, the worker ran the job");
                    }
                    else
                    {
                        evidence.Line("payload: the deferred merge produced NOTHING (the window never " +
                                      "confirmed, or the queue never drained, or the worker never ran)");
                    }
                }
                evidence.Line($"conversion finished: finished={final.Finished} ok={final.Ok} failed={final.Failed} " +
                              $"error={final.Error ?? "-"} line='{final.Line}'");
                // 轉檔是這一條診斷最主要的外部效果：它沒跑完（逾時／worker 沒接上／管道斷）
                // 就是「沒有結論」，不可以回 0。
                if (!final.Finished) verdict = false;
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
                //
                // ⚠ 排序視窗還開著時**不跑這一段**（2026-10-07，切片 B）：那時的語意是「有人在
                //    視窗前面決定順序」，再送一次釋放只會多跑一次轉檔、把輸出目錄弄亂，
                //    而它驗的東西與這裡無關。
                if (!MergeUi.IsOpen)
                {
                    guard.OnCaptured();                       // 接收層再次升起（＝第二次拖曳）
                    ShowWheelFromPayload(presenter, catalog, evidence, injected);
                    int sel = presenter.SelectNext(1);        // 換一個扇區，輸出才不會撞名
                    evidence.Line($"release-path: selected index={sel} target={presenter.SelectedTarget}");
                    detector.ForceRelease("mouse up");        // 走真正的 Released 事件，不是替代品
                    ConversionSnapshot after = runner.Wait(60000);
                    evidence.Line($"release commit finished: finished={after.Finished} ok={after.Ok} " +
                                  $"failed={after.Failed} line='{after.Line}'");
                    if (!after.Finished) verdict = false;
                    PumpUntil(() => !progress.IsVisible, 12000);
                }
                else
                {
                    evidence.Line("release-path: skipped (the sorting window is open; the user is still " +
                                  "choosing the order)");
                }
            }

            // ⚠ 最後一段：排序視窗還開著時**留在這裡等**（2026-10-07，切片 B）。
            //    正式路徑靠的是常駐的訊息迴圈；這一條診斷沒有那個迴圈（它在 Main 的早期就返回了），
            //    所以如果不留下來，視窗會在行程結束時被一起拆掉——「真的開了視窗、而且真的在等
            //    使用者」就無法被外部觀察到，而 S21.07（取消 ⇒ 什麼都不產出）要的正是那個證據。
            //    幫浦到視窗關閉為止（使用者按確認或取消）；逾時就誠實說出來。
            if (MergeUi.IsOpen)
            {
                evidence.Line("payload: the sorting window is open -> staying alive until the user " +
                              "confirms or cancels (up to 900000 ms)");
                bool closed = PumpUntil(() => !MergeUi.IsOpen, 900000);
                evidence.Line($"payload: the sorting window {(closed ? "was closed by the user" : "was still " +
                              "open after 900000 ms")} -> this diagnostic is done");
            }
            if (!verdict)
                evidence.Line("wheel-payload: this diagnostic run produced NO verdict -> exit 3 " +
                              "(a Ready payload with nothing to convert, or a conversion that never finished)");
            return verdict ? 0 : 3;
        }

        using var hooks = new Hooks();
        // 診斷：Shift 切層那兩行的**唯一**可自動化入口。順序刻意與下面兩條相反（先接線、先裝鉤子、
        // 再叫輪盤），因為它一進去就自己送合成按鍵，鉤子必須已經在聽。
        if (opt.WheelLayerProbe != null)
            return WheelLayerProbe(opt.WheelLayerProbe, hooks, detector, presenter, catalog,
                                   guard, evidence, runner);

        if (opt.RaiseWithHooks)
        {
            // --raise-with-hooks：與 --raise 同一件事，差別只在鉤子照裝（理由見 Options.RaiseWithHooks）。
            // 接線用的是**同一份** WireHooks，所以這裡量到的切層行為就是常駐路徑的行為。
            WireHooks(hooks, detector, presenter, guard, evidence, runner);
            hooks.Install(evidence);
            guard.OnCaptured();       // 用真正的保險 #3 計時收層，不用替代品
        }
        else if (opt.ForceRaiseHitLayer)
        {
            evidence.Line("--raise: hooks NOT installed");
            guard.OnCaptured();       // 用真正的保險 #3 計時收層，不用替代品
        }
        else
        {
            WireHooks(hooks, detector, presenter, guard, evidence, runner);
            hooks.Install(evidence);
        }

        evidence.Line("message loop starting");
        return RunMessageLoop(evidence);
    }

    /// <summary>
    /// 把低階鉤子的事件接到偵測器與輪盤。**全專案唯一一處**（常駐路徑、`--raise-with-hooks`、
    /// `--wheel-layer-probe` 三條路都呼叫它）。
    ///
    /// 為什麼要抽出來：Shift 切層那一行住在**鉤子的回呼**裡。若探針自己再接一份長得差不多的
    /// 回呼，那麼「探針過了」只證明探針那一份是對的，而產品跑的是另一份——那正是這一格一直
    /// 沒有證據的原因（驗的東西與跑的東西不是同一份）。抽成一份之後，探針量到的就是常駐
    /// 路徑量到的。
    /// </summary>
    private static void WireHooks(Hooks hooks, DragDetector detector, WheelPresenter presenter,
                                  ReleaseGuard guard, Evidence evidence, ConversionRunner runner)
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
            // 第二層的**即時**切換（規格 §9.4，2026-10-05 使用者裁定）：Shift 按下換成工具層、
            // 放開切回格式層。這一條必須在下面那行**之前**，而且**不能在 isDown 判斷裡面**——
            // 放開（isDown=false）也要送進來，否則切不回格式層。
            // 輪盤沒顯示時 Shift 完全不受影響（照樣傳給 detector）。
            // ⚠ 0xA0／0xA1 是 VK_LSHIFT／VK_RSHIFT：**真實鍵盤的低階鉤子送來的是這兩個**，
            // 不是通用的 VK_SHIFT（0x10）。只比對 VK_SHIFT 的話，真人按住 Shift 拖檔案時畫面
            // 不會有任何反應——只有「拖曳前就先按住 Shift」那條舊路有效，而那正是 2026-10-05
            // 裁定要淘汰的行為。（探針當場量到鉤子送的是 0xA0；把條件放寬後兩行切層日誌立刻出現。）
            // 待辦：這兩個常數應該搬進 Native 與 VK_SHIFT 放在一起（金鑰常數只有一個家）。
            if (presenter.IsVisible &&
                (vk == Native.VK_SHIFT || vk == 0xA0 || vk == 0xA1))
            {
                SwapWheelLayer(isDown, presenter, evidence);
                return;
            }
            // 輪盤可見時，方向鍵／Enter／Esc 屬於輪盤（規格 §4.3 鍵盤表）
            if (isDown && presenter.IsVisible && HandleWheelKey(vk, presenter, guard, evidence, runner))
                return;
            detector.OnKey(vk, isDown);
        };
    }

    /// <summary>
    /// 診斷探針（`--wheel-layer-probe &lt;png&gt;`）：格式層 → 合成 Shift keydown → 工具層 →
    /// 合成 Shift keyup → 格式層，兩次都成立回 0，否則回 1。
    ///
    /// <para>
    /// <b>這一條證明什麼、不證明什麼（最重要的一段）</b>：它**不是**自己呼叫
    /// <see cref="SwapWheelLayer"/> 來「驗」那個方法——那只是把方法本身再跑一次，什麼都沒證明。
    /// 這裡沒有任何一行直接呼叫切層。合成鍵走的是 keybd_event → **系統輸入佇列** →
    /// WH_KEYBOARD_LL → <see cref="WireHooks"/> 接的那份回呼 → <see cref="SwapWheelLayer"/>，
    /// 與真人按鍵走的是同一條路。三項可量到的事實把這件事釘住：
    /// </para>
    /// <list type="number">
    /// <item>原始碼裡沒有任何一行呼叫 SwapWheelLayer（唯一呼叫者在 WireHooks 的回呼裡）；
    ///       探針只讀 <c>presenter.Mode</c>。</item>
    /// <item>切層發生時 <see cref="Hooks.KeyCallbacks"/> 已經增加——鉤子回呼真的被呼叫過。
    ///       少了這個計數，「模式換了」單獨看是說不出來源的。</item>
    /// <item>切層發生時系統的 Shift 狀態是「按著」（<c>GetAsyncKeyState</c>）——
    ///       那個鍵真的進了系統，不是行程內部某個旗標被翻了一下。</item>
    /// </list>
    /// <para>
    /// <b>⚠ 為什麼注入一定要在另一個執行緒</b>：低階鉤子的回呼是在**安裝鉤子的執行緒**上被呼叫的，
    /// 而 keybd_event／SendInput 會擋到那個事件被處理完為止。同一個執行緒送＝那個回呼永遠等不到
    /// 執行緒去跑它；系統會等 LowLevelHooksTimeout（預設 300 ms）之後直接放行（Win7 之後甚至
    /// 把鉤子移除）。那會做出一個**假陰性**——看起來像「鉤子收不到合成鍵」，而真正壞掉的是
    /// 這個探針。所以：注入執行緒送出 → 主執行緒幫浦訊息（<see cref="PumpUntil"/>）→
    /// 回呼在主執行緒上執行 → 切層。
    /// </para>
    /// </summary>
    private static int WheelLayerProbe(string png, Hooks hooks, DragDetector detector, WheelPresenter presenter,
                                       IFormatCatalog catalog, ReleaseGuard guard, Evidence evidence,
                                       ConversionRunner runner)
    {
        int failures = 0;
        void Report(string text, bool ok)
        {
            if (!ok) failures++;
            evidence.Line($"{(ok ? "PASS" : "FAIL")}  wheel-layer-probe: {text}");
        }

        string modeText() => presenter.Mode == WheelPresenter.WheelMode.Tool ? "tool" : "format";

        evidence.Line($"=== wheel-layer-probe: payload={png} ===");
        if (!File.Exists(png))
        {
            evidence.Line($"wheel-layer-probe: no such file -> exit 1 (nothing was proven)");
            return 1;
        }

        // 接線與常駐路徑**同一份**，裝鉤子也走同一個 Install。少了這一步，整個探針就只是自言自語。
        WireHooks(hooks, detector, presenter, guard, evidence, runner);
        // 只讀的第二個訂閱者：把鉤子**實際送來**的 vkCode 記下來。這一條讓失敗自己解釋自己——
        // 產品比對的是 VK_SHIFT(0x10)，而低階鉤子對 Shift 送的是左右分明的 VK_LSHIFT(0xA0)／
        // VK_RSHIFT(0xA1)（量測見 2026-10-05 的 %TEMP%\llkbd-vkcode-measure.ps1：
        // 連硬體掃描碼 0x2A 都得到 0xA0）。沒有這一行的話，失敗只會說「沒切層」，
        // 而真正的原因在日誌裡看不到。
        var hookKeysSeen = new List<string>();
        hooks.KeyEvent += (vk, isDown) => hookKeysSeen.Add($"0x{vk:X2}{(isDown ? "-down" : "-up")}");
        hooks.Install(evidence);
        Report("keyboard hook really installed (WH_KEYBOARD_LL handle != 0)", hooks.KeyboardInstalled);
        if (!hooks.KeyboardInstalled)
        {
            evidence.Line("wheel-layer-probe: without the keyboard hook there is nothing to prove -> exit 1");
            return 1;
        }

        guard.OnCaptured();
        int callbacksBefore = hooks.KeyCallbacks;
        ShowWheelFromPayload(presenter, catalog, evidence, new[] { png });

        bool startOk = presenter.IsVisible && presenter.Mode == WheelPresenter.WheelMode.Format;
        Report($"wheel visible in the FORMAT layer before any key is sent (visible={presenter.IsVisible} " +
               $"mode={modeText()})", startOk);
        if (!startOk)
        {
            evidence.Line("wheel-layer-probe: the probe has no valid starting point -> exit 1");
            guard.OnUserResolved("wheel-layer-probe aborted");
            PumpUntil(() => !presenter.IsVisible, 1000);
            return 1;
        }

        // (1) Shift DOWN：從另一個執行緒注入；主執行緒在這裡幫浦，鉤子才有機會跑。
        bool toTool = InjectKeyFromAnotherThread((byte)Native.VK_SHIFT, down: true,
                                                 () => presenter.Mode == WheelPresenter.WheelMode.Tool);
        int callbacksAfterDown = hooks.KeyCallbacks;
        evidence.Line($"wheel-layer-probe: after injected Shift DOWN -> mode={modeText()} " +
                      $"hookKeyCallbacks={callbacksBefore}->{callbacksAfterDown} " +
                      $"hookKeysSeen=[{string.Join(",", hookKeysSeen)}] " +
                      $"systemShiftStateDown={IsShiftDown()}");
        Report("Shift down switched the wheel to the TOOL layer (log line 'wheel layer switch: format -> tool')",
               toTool);
        Report("the keyboard hook callback really ran for the injected key (not a direct in-process call)",
               callbacksAfterDown > callbacksBefore);
        Report("the switch was observed while the system reports Shift held down",
               toTool && IsShiftDown());

        if (!toTool)
        {
            // 失敗也要把 Shift 放掉：留著一個被按住的修飾鍵在使用者的系統上，比測試變紅嚴重得多。
            InjectKeyFromAnotherThread((byte)Native.VK_SHIFT, down: false, () => false, pumpMs: 700);
            evidence.Line($"wheel-layer-probe: FAILED at the Shift-down step -> exit 1 " +
                          $"(mode={modeText()}, hookKeyCallbacks={hooks.KeyCallbacks}, " +
                          $"hookKeysSeen=[{string.Join(",", hookKeysSeen)}])");
            // 到這裡為止的三件事都已經量到了：鉤子裝著、合成鍵真的走過鉤子回呼、系統狀態是按下。
            // 所以「沒切層」不可能再被讀成「合成鍵送不進鉤子」——剩下的唯一嫌疑是**比對條件**，
            // 那就必須說清楚是哪一個值對不上，否則下一個人只會再撞一次同一面牆。
            if (hooks.KeyCallbacks > callbacksBefore && hookKeysSeen.Count > 0 &&
                !hookKeysSeen[hookKeysSeen.Count - 1].StartsWith("0x10", StringComparison.Ordinal))
                evidence.Line("wheel-layer-probe: DIAGNOSIS — the key DID arrive through WH_KEYBOARD_LL " +
                              $"(hookKeysSeen=[{string.Join(",", hookKeysSeen)}]) but the switch condition " +
                              "in WireHooks accepts only VK_SHIFT (0x10). A low-level keyboard hook does not " +
                              "report the generic code for Shift: it reports the left/right-specific " +
                              "VK_LSHIFT (0xA0) / VK_RSHIFT (0xA1), which is also what a real hardware " +
                              "scan code (0x2A) produces. So this line is not a probe artefact: a human " +
                              "pressing Shift during a drag hits the same condition and gets the same " +
                              "nothing. The probe cannot fix that from here -- it would be testing a " +
                              "condition the product does not have.");
            guard.OnUserResolved("wheel-layer-probe failed");
            PumpUntil(() => !presenter.IsVisible, 1000);
            return 1;
        }

        // (2) Shift UP：同一條路徑回來。只有在 (1) 成立時才跑——否則這一格會因為「本來就是格式層」
        //     而假通過，那是這一種探針最容易被誤讀的地方。
        bool toFormat = InjectKeyFromAnotherThread((byte)Native.VK_SHIFT, down: false,
                                                   () => presenter.Mode == WheelPresenter.WheelMode.Format);
        int callbacksAfterUp = hooks.KeyCallbacks;
        evidence.Line($"wheel-layer-probe: after injected Shift UP -> mode={modeText()} " +
                      $"hookKeyCallbacks={callbacksAfterDown}->{callbacksAfterUp} " +
                      $"hookKeysSeen=[{string.Join(",", hookKeysSeen)}] " +
                      $"systemShiftStateDown={IsShiftDown()}");
        Report("Shift up switched the wheel back to the FORMAT layer (log line 'wheel layer switch: tool -> format')",
               toFormat);
        Report("the keyboard hook callback really ran for the injected key-up too",
               callbacksAfterUp > callbacksAfterDown);
        Report("no modifier is left held down after the probe", !IsShiftDown());

        guard.OnUserResolved("wheel-layer-probe done");
        PumpUntil(() => !presenter.IsVisible, 1000);
        evidence.Line($"wheel-layer-probe: {(failures == 0 ? "PASS (exit 0)" : $"FAIL ({failures} check(s)) -> exit 1")} " +
                      $"hookKeyCallbacks={hooks.KeyCallbacks} (the two 'wheel layer switch:' lines above were " +
                      "produced by the WH_KEYBOARD_LL callback, not by a direct call)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 從**另一個執行緒**送一次合成的按鍵，並在呼叫端幫浦訊息直到 <paramref name="observed"/> 成立。
    ///
    /// 為什麼不能直接 <c>Native.keybd_event(...)</c> 了事：見 <see cref="WheelLayerProbe"/> 的說明——
    /// 同一個執行緒送會讓鉤子回呼永遠等不到執行緒去跑它，逾時後系統直接放行，於是「送成功、
    /// 切層沒發生」，而那會被讀成「鉤子收不到合成鍵」這個錯的結論。
    ///
    /// <paramref name="pumpMs"/> &gt; 0 時改成「固定幫浦這麼久」而不是等條件（給清理路徑用：
    /// 失敗時要確保 keyup 真的送完，但已經沒有條件可等）。回傳真正等到條件成立與否。
    /// </summary>
    private static bool InjectKeyFromAnotherThread(byte vk, bool down, Func<bool> observed, int pumpMs = 0)
    {
        var injector = new Thread(() => Native.keybd_event(vk, 0, down ? 0u : Native.KEYEVENTF_KEYUP,
                                                           UIntPtr.Zero))
        {
            IsBackground = true,
            Name = down ? "wheel-probe-key-down" : "wheel-probe-key-up"
        };
        injector.Start();
        // 呼叫端（＝安裝鉤子的執行緒）在這裡幫浦：鉤子回呼就是靠這些 PeekMessage/DispatchMessage 跑起來的。
        bool ok = pumpMs > 0 ? PumpFor(pumpMs, observed) : PumpUntil(observed, 5000);
        injector.Join(2000);      // 注入執行緒只有在回呼跑完之後才出得來，正常情況這裡是 0 ms
        return ok;
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

        // M3（2026-10-05）第二層：`--wheel-tools` 時改畫**工具輪盤**。
        // 扇區清單同樣來自唯一權威（ToolCatalog），這裡不硬寫任何工具名稱或圖示。
        // 示範用的酬載狀態一律當成 Ready——這是「假設檔案可以轉」的那個前提，
        // 與第一層示範同一套假設（真實路徑上這一格由 PayloadVerdict 決定）。
        var tools = new ToolCatalog().ToolsFor(
            // 示範路徑沒有真的檔案：張數填 1（單張）——於是「合併 PDF」不會出現在示範輪盤上，
            // 與真實的單檔情境一致。要看圖片合併請用 --wheel-payload。
            new PayloadVerdict(PayloadStatus.Ready, opt.WheelSource, 1, CanConvert: true));
        bool toolsMode = opt.WheelTools && tools.Count > 0;
        if (opt.WheelTools && !toolsMode)
            evidence.Line("--wheel-tools: catalog returned no tool for a Ready payload -> formats only");
        if (toolsMode) targets = tools.Select(t => t.Label).ToArray();

        var sw = Stopwatch.StartNew();
        presenter.Show(x, y, opt.WheelSource, targets, opt.WheelSelected,
                       // 工具輪盤**不給檔名預覽**：轉完叫什麼名字是執行階段才知道的事，
                       // 在這裡先寫一個名字就是承諾一件還沒決定的事（Slice B 才會定案）。
                       toolsMode ? null : PreviewFactory(targets, catalog, new[] { $"holiday.{opt.WheelSource.ToLowerInvariant()}" },
                                                         opt.WheelPreviews),
                       mode: toolsMode ? WheelPresenter.WheelMode.Tool : WheelPresenter.WheelMode.Format,
                       caption: toolsMode ? (i => i < tools.Count ? tools[i].Description : null) : null,
                       icons: toolsMode ? tools.Select(t => t.Icon).ToArray() : null);
        sw.Stop();

        evidence.Line($"visual demo shown at ({x},{y}) " +
                      $"size={WheelGeometry.BitmapWidthPx}x{WheelGeometry.BitmapHeightPx} " +
                      $"in {sw.Elapsed.TotalMilliseconds:F1} ms (budget 100 ms)");

        // 工具輪盤：**每一個扇區都必須真的畫出一個圖示**（2026-10-07 切片 2）。
        // 這一條的來源是繪製自己（DrawToolIcon 的返回值），不是幾何推算——
        // 理由與量測見 WheelPresenter.DrawnToolIcons（實測：對 PNG 量墨水分不出圖示與標籤字）。
        if (toolsMode)
            evidence.Line($"visual demo tool icons: drawn={presenter.DrawnToolIcons}/{targets.Length} " +
                          $"icons=[{string.Join(",", presenter.DrawnToolIconLabels)}]");

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
    /// M3 診斷：`--rotate90 &lt;path&gt;`。走的是 **worker 用的同一個引擎**
    /// （<see cref="ConversionEngine.Rotate90"/>），所以量到的就是產品的能力；
    /// 只差沒有跨管道（worker 協議的 `op` 欄位是下一步）。
    ///
    /// 它刻意把三件事印成可斷言的事實：
    ///   ①引擎回報的成功／失敗與輸出檔名；
    ///   ②日誌裡 `rotate90: WxH -> WxH` 那一行（非方形圖的長寬必須對調）；
    ///   ③來源檔**完全沒被動到**——SHA256／長度／mtime 三項前後比對（規格 §5 的第一名）。
    /// 回傳碼：0 成功、1 來源被動到（§5 紅線）、2 找不到來源、3 引擎失敗。
    /// </summary>
    private static int Rotate90Diagnostic(Evidence evidence, Options opt)
    {
        string source = opt.Rotate90!;
        if (!File.Exists(source))
        {
            evidence.Line($"--rotate90: source not found: {source}");
            return 2;
        }

        string beforeHash = Sha256Of(source);
        long beforeLength = new FileInfo(source).Length;
        DateTime beforeWrite = File.GetLastWriteTimeUtc(source);

        var registry = new CodecRegistry();
        var engine = new ConversionEngine(registry, evidence.Line);
        // 輸出資料夾由設定決定（規格 §9.5；null＝來源旁邊）。`--settings-file` 可以指向別的檔案，
        // 所以這一條同時就是「設定 → NamingPolicy → 寫檔」整條鏈的可無頭驗證路徑。
        string? outputDir = (_settings ?? new Settings()).ResolveOutputDirectory();
        evidence.Line($"tool rotate90: outputDir={outputDir ?? "(source folder)"}");
        ConversionResult result = engine.Rotate90(source, journal: null, targetDirectory: outputDir);

        string afterHash = Sha256Of(source);
        bool untouched = beforeHash == afterHash
                         && new FileInfo(source).Length == beforeLength
                         && File.GetLastWriteTimeUtc(source) == beforeWrite;

        evidence.Line($"tool rotate90: ok={result.Ok} error={result.Error} output={result.OutputPath ?? "-"}");
        evidence.Line($"tool rotate90: source-untouched={untouched} " +
                      $"(sha256 {beforeHash.Substring(0, 12)} -> {afterHash.Substring(0, 12)}, " +
                      $"len {beforeLength}, mtime {(beforeWrite == File.GetLastWriteTimeUtc(source) ? "same" : "CHANGED")})");

        if (!result.Ok)
        {
            evidence.Line($"--rotate90 FAILED: {result.UserMessage}");
            return 3;
        }
        if (!untouched)
        {
            evidence.Line("--rotate90 FAILED: the source file was modified (spec 5 violation)");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// M3 診斷：`--strip-metadata &lt;path&gt;`。走的是 **worker 用的同一個引擎**
    /// （<see cref="ConversionEngine.StripMetadata"/>），所以量到的就是產品的能力。
    ///
    /// 它刻意把三件事印成可斷言的事實（與 <see cref="Rotate90Diagnostic"/> 同一個形狀）：
    ///   ①引擎回報的成功／失敗與輸出檔名；
    ///   ②輸出**落在哪一條路徑**（容器層 or 重編碼）與移除了哪些區段——那在日誌裡，
    ///     因為那是引擎自己的事實，在這裡重講一遍就是第二個擁有者；
    ///   ③來源檔**完全沒被動到**——SHA256／長度／mtime 三項前後比對（規格 §5 的第一名）。
    /// 回傳碼：0 成功、1 來源被動到（§5 紅線）、2 找不到來源、3 引擎失敗。
    /// </summary>
    private static int StripMetadataDiagnostic(Evidence evidence, Options opt)
    {
        string source = opt.StripMetadata!;
        if (!File.Exists(source))
        {
            evidence.Line($"--strip-metadata: source not found: {source}");
            return 2;
        }

        string beforeHash = Sha256Of(source);
        long beforeLength = new FileInfo(source).Length;
        DateTime beforeWrite = File.GetLastWriteTimeUtc(source);

        var registry = new CodecRegistry();
        var engine = new ConversionEngine(registry, evidence.Line);
        string? outputDir = (_settings ?? new Settings()).ResolveOutputDirectory();
        evidence.Line($"tool strip-metadata: outputDir={outputDir ?? "(source folder)"}");
        ConversionResult result = engine.StripMetadata(source, journal: null, targetDirectory: outputDir);

        string afterHash = Sha256Of(source);
        bool untouched = beforeHash == afterHash
                         && new FileInfo(source).Length == beforeLength
                         && File.GetLastWriteTimeUtc(source) == beforeWrite;

        evidence.Line($"tool strip-metadata: ok={result.Ok} error={result.Error} output={result.OutputPath ?? "-"}");
        evidence.Line($"tool strip-metadata: source-untouched={untouched} " +
                      $"(sha256 {beforeHash.Substring(0, 12)} -> {afterHash.Substring(0, 12)}, " +
                      $"len {beforeLength}, mtime {(beforeWrite == File.GetLastWriteTimeUtc(source) ? "same" : "CHANGED")})");

        if (!result.Ok)
        {
            evidence.Line($"--strip-metadata FAILED: {result.UserMessage}");
            return 3;
        }
        if (!untouched)
        {
            evidence.Line("--strip-metadata FAILED: the source file was modified (spec 5 violation)");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// M3 診斷：`--compress &lt;path&gt;`（第三顆扇形）。走的是 **worker 用的同一個引擎**
    /// （<see cref="ConversionEngine.Compress"/>），所以量到的就是產品的能力。
    ///
    /// 它刻意把四件事印成可斷言的事實（與 <see cref="StripMetadataDiagnostic"/> 同一個形狀）：
    ///   ①**這一輪用的政策**（模式、上限、標準模式要用的品質）——它來自設定檔，不是命令列；
    ///   ②引擎自己回報的 `compressed ... mode=..., quality=..., A B -> B B`（＝輸入大小 →
    ///     輸出大小 → **實際品質**，那是引擎的事實，在這裡重講一遍就是第二個擁有者）；
    ///   ③「壓縮到」的搜尋痕跡與**最佳性證明**（`compress search:` 與 `compress: target-size proof`
    ///     那兩行，同樣由引擎寫出）；
    ///   ④來源檔**完全沒被動到**——SHA256／長度／mtime 三項前後比對（規格 §5 的第一名）。
    /// 回傳碼：0 成功、1 來源被動到（§5 紅線）、2 找不到來源、3 引擎失敗（含「上限做不到」）。
    /// </summary>
    private static int CompressDiagnostic(Evidence evidence, Options opt)
    {
        string source = opt.Compress!;
        if (!File.Exists(source))
        {
            evidence.Line($"--compress: source not found: {source}");
            return 2;
        }

        string beforeHash = Sha256Of(source);
        long beforeLength = new FileInfo(source).Length;
        DateTime beforeWrite = File.GetLastWriteTimeUtc(source);

        var registry = new CodecRegistry();
        var engine = new ConversionEngine(registry, evidence.Line);
        Settings settings = _settings ?? new Settings();
        CompressionPolicy policy = settings.ToCompressionPolicy();
        string? outputDir = settings.ResolveOutputDirectory();
        evidence.Line($"tool compress: outputDir={outputDir ?? "(source folder)"}");
        evidence.Line($"tool compress: policy {policy.Describe()} " +
                      $"from settings-file (compressionMode={settings.CompressionMode}, " +
                      $"compressionTargetKb={settings.CompressionTargetKb})");
        ConversionResult result = engine.Compress(source, policy, journal: null, targetDirectory: outputDir);

        string afterHash = Sha256Of(source);
        bool untouched = beforeHash == afterHash
                         && new FileInfo(source).Length == beforeLength
                         && File.GetLastWriteTimeUtc(source) == beforeWrite;

        long outBytes = result.Ok && result.OutputPath != null && File.Exists(result.OutputPath)
            ? new FileInfo(result.OutputPath).Length : -1;
        evidence.Line($"tool compress: ok={result.Ok} error={result.Error} output={result.OutputPath ?? "-"}");
        evidence.Line($"tool compress: in={beforeLength}B out={outBytes}B mode={policy.Mode} " +
                      $"targetBytes={policy.TargetBytes}");
        evidence.Line($"tool compress: source-untouched={untouched} " +
                      $"(sha256 {beforeHash.Substring(0, 12)} -> {afterHash.Substring(0, 12)}, " +
                      $"len {beforeLength}, mtime {(beforeWrite == File.GetLastWriteTimeUtc(source) ? "same" : "CHANGED")})");

        if (!result.Ok)
        {
            evidence.Line($"--compress FAILED: {result.UserMessage}");
            return 3;
        }
        if (!untouched)
        {
            evidence.Line("--compress FAILED: the source file was modified (spec 5 violation)");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// M3 診斷：`--read-qr &lt;path&gt;`（第五顆扇形）。走的是 **worker 用的同一個引擎**
    /// （<see cref="ConversionEngine.ReadQr"/>），所以量到的就是產品的能力。
    ///
    /// 它刻意把四件事印成可斷言的事實（與 <see cref="CompressDiagnostic"/> 同一個形狀）：
    ///   ①**解出來的文字本體**——印在 stdout 的固定前綴後面（`tool read-qr: text=`），
    ///     而且**逐字元原封不動**（沒有 trim、沒有截斷、沒有引號）。驗收要能從這一條輸出
    ///     **重新推導**出那段文字，而不是只相信 `ok=True`：`ok=True` 只證明引擎說它成功了；
    ///     ②長度（`chars=N`）——它讓「文字被截斷」變成當場看得見的事實（`Chars` 是 C# 的
    ///     UTF-16 計數，與 QR 的位元組數無關，這裡只當「有沒有變短」的守門員）；
    ///   ③引擎自己那一行 `read-qr: &lt;round&gt; WxH in N ms -&gt; ...`（**哪一輪**解到的、
    ///     花了多久）——那是引擎的事實，在這裡重講一遍就是第二個擁有者；
    ///   ④來源檔**完全沒被動到**——SHA256／長度／mtime 三項前後比對（規格 §5 的第一名）。
    ///     這一條對這個工具特別重要：**它是唯一一個不寫檔的 op**，「來源不變」不是靠寫入路徑
    ///     保證的，是靠「整條路上沒有任何寫入呼叫」保證的，而這裡是那個事實的量測。
    ///
    /// ⚠ **沒有 QR 的圖不是錯誤路徑的意外，是一條正常的回報**：引擎回
    /// <see cref="ConversionError.QrNotFound"/>，這裡把它的 <c>detail</c>（試過哪幾輪）印出來，
    /// 回傳碼 3，而且**沒有任何檔案產生**（`--read-qr` 本來就不寫檔）。
    /// 回傳碼：0 成功解到文字、1 來源被動到（§5 紅線）、2 找不到來源、3 引擎失敗（含找不到 QR）。
    /// </summary>
    private static int ReadQrDiagnostic(Evidence evidence, Options opt)
    {
        string source = opt.ReadQr!;
        if (!File.Exists(source))
        {
            evidence.Line($"--read-qr: source not found: {source}");
            return 2;
        }

        string beforeHash = Sha256Of(source);
        long beforeLength = new FileInfo(source).Length;
        DateTime beforeWrite = File.GetLastWriteTimeUtc(source);

        var registry = new CodecRegistry();
        var engine = new ConversionEngine(registry, evidence.Line);
        // 沒有 outputDir 這回事：這個工具不寫檔（見 ReadQrDiagnostic 的說明）。
        // 印出來是為了讓「它真的沒有輸出資料夾」變成看得見的事實，而不是少一行要讀原始碼才知道。
        evidence.Line("tool read-qr: output=(none) -- this tool never writes a file");
        ConversionResult result = engine.ReadQr(source, journal: null);

        string afterHash = Sha256Of(source);
        bool untouched = beforeHash == afterHash
                         && new FileInfo(source).Length == beforeLength
                         && File.GetLastWriteTimeUtc(source) == beforeWrite;

        // ⚠ 這一行的**格式是驗收斷言的一部分**（固定前綴 ＋ 文字本體 ＋ 長度）。
        // 改格式要先改 `tools/verify-m3d.ps1` 的 S19.01。
        //
        // 「沒有文字」用 `output=` 而不是 `text=`：`Ok` 是「引擎回報成功」，
        // 而 `QrText` 是「真的有文字」。**成功的空字串**（QR 裡編的是空內容）在實務上不存在
        // （ZXing 的編碼器不會產生它），所以這裡用 `?? null` 判斷——萬一真的出現，
        // 這一條會印 `text=` 加零個字元，而驗收的逐字元比對會直接失敗（那是誠實的結果）。
        string? text = result.QrText;
        string textLine = text != null
            ? $"tool read-qr: text={text} chars={text.Length}"
            : $"tool read-qr: output=(none) error={result.Error}";
        evidence.Line(textLine);
        // **同一行也印到 stdout**：`--rotate90`／`--compress` 那一族只寫證據檔（`Evidence.Line`
        // 另外會鏡射到 stderr），但這個工具的產物是**文字本身**——它必須能像 `--version` 那樣
        // 被直接讀走（`Confern --read-qr x.png > out.txt`），否則「把解出來的文字印出來」這件事
        // 就只存在於日誌裡，而日誌是產品的內部格式。**這不是第二個擁有者**：字串是同一個變數，
        // 兩個出口，所以它們不可能說不同的話。
        try { Console.WriteLine(textLine); } catch (IOException) { /* 沒有 stdout（父行程已關）：日誌仍然有 */ }
        evidence.Line($"tool read-qr: ok={result.Ok} error={result.Error} " +
                      $"detail={result.Detail}");
        evidence.Line($"tool read-qr: source-untouched={untouched} " +
                      $"(sha256 {beforeHash.Substring(0, 12)} -> {afterHash.Substring(0, 12)}, " +
                      $"len {beforeLength}, mtime {(beforeWrite == File.GetLastWriteTimeUtc(source) ? "same" : "CHANGED")})");

        if (!untouched)
        {
            // §5 是紅線，所以它排在「引擎成功／失敗」前面：來源被動到比讀不到碼嚴重得多。
            evidence.Line("--read-qr FAILED: the source file was modified (spec 5 violation)");
            return 1;
        }
        if (!result.Ok)
        {
            evidence.Line($"--read-qr FAILED: {result.UserMessage}");
            return 3;
        }
        return 0;
    }

    /// <summary>
    /// M3 診斷：`--crop &lt;p1;p2&gt; --crop-rect x,y,w,h [--crop-ratio id]`（第四顆扇形）。
    ///
    /// 與另外三個工具診斷最大的差別：**它起真的 worker 行程、走真的管道**。前三個是「同一個行程內
    /// 直接呼叫引擎」，所以它們驗不到協議欄位；裁切帶著五個新欄位（<c>cropX</c>…<c>cropRatio</c>），
    /// 而「欄位有沒有真的跨過去」正是最可能壞掉、又最難看出來的一件事（送出去的是
    /// <c>x=0.125</c>，另一端讀到的是 null/0，結果是一個沒有裁到的檔案，而且回報成功）。
    ///
    /// 它刻意把五件事印成可斷言的事實：
    ///   ①**這一輪用的矩形與比例**（正規化；來自命令列，不是猜的）；
    ///   ②worker **自己那一行** `worker job N: ... op=crop crop=(...) ratio=...`——矩形真的跨過管道
    ///     的證據在**另一端**，不在這裡；
    ///   ③引擎每一張圖各自回報的 `crop: name WxH -> WxH (rect=..., ...)`——「同一比例、各自換算」
    ///     的實測值就是這一行的左邊與右邊；
    ///   ④每一張的**輸出尺寸**（用產品自己的解碼器量回來的畫格，不是算式）；
    ///   ⑤來源檔**完全沒被動到**——SHA256／長度／mtime 三項前後比對（規格 §5 的第一名）。
    ///
    /// 回傳碼：0 全部成功、1 來源被動到（§5 紅線）、2 參數或來源有問題（**沒有送出任何工作**）、
    /// 3 引擎失敗（含「空的矩形」與「等於整張圖」這兩種具名拒絕）。
    /// </summary>
    internal static int CropDiagnostic(Evidence evidence, Options opt)
    {
        if (!CropSpec.TryParse(opt.CropRect, out CropSpec parsed, out string parseError))
        {
            evidence.Line($"--crop: refusing to run: {parseError}");
            evidence.Line("--crop FAILED (nothing was sent to the worker)");
            return 2;
        }
        if (opt.CropRatio.Length > 0 && !CropRatios.IsCanonical(opt.CropRatio))
        {
            // 認不得的比例**不靜默退回 free**：那會讓「1:1」這種打錯字變成一條看起來成功的裁切。
            evidence.Line($"--crop: unknown --crop-ratio '{opt.CropRatio}' " +
                          $"(expected one of {string.Join("/", CropRatios.All.Select(p => p.Id))})");
            evidence.Line("--crop FAILED (nothing was sent to the worker)");
            return 2;
        }
        CropSpec spec = parsed with { RatioId = CropRatios.CanonicalId(opt.CropRatio) };

        string[] paths = opt.Crop!.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0)
        {
            evidence.Line("--crop: no source path was given");
            return 2;
        }
        foreach (string path in paths)
        {
            if (!File.Exists(LongPath.Prepare(path)))
            {
                evidence.Line($"--crop: source not found: {path}");
                return 2;
            }
        }

        // 來源的三項指紋（規格 §5）：整批送出之前先量一次，跑完再量一次。
        var before = paths.Select(p => (Path: p, Hash: Sha256Of(p), Length: new FileInfo(p).Length,
                                        Mtime: File.GetLastWriteTimeUtc(p))).ToList();

        Settings settings = _settings ?? new Settings();
        string? outputDir = settings.ResolveOutputDirectory();
        evidence.Line($"tool crop: outputDir={outputDir ?? "(source folder)"}");
        evidence.Line($"tool crop: request {spec.Describe()} files={paths.Length} " +
                      $"(normalized rectangle -> every file is converted with ITS OWN size)");

        int journalRecovered = new TempJournal(TempJournal.DefaultPath()).RecoverStale();
        evidence.Line($"temp journal: recovered {journalRecovered} stale temp file(s)");

        // **真的 worker**：同一條路徑、同一份協議、同一個 op。不使用 ConversionRunner 是因為
        // 它一定要一個 ProgressPresenter（＝螢幕上的進度藥丸），而診斷不該在使用者畫面上畫東西。
        string exePath = Environment.ProcessPath ?? $"{ProductInfo.Name}.exe";
        var client = new ConversionClient(exePath, evidence.Line);
        try
        {
            if (!client.Start(paths, "", 100, outputDir: outputDir, toolOp: WorkerProtocol.OpCrop,
                              out string? startError, crop: spec))
            {
                evidence.Line($"--crop FAILED: the worker did not start ({startError})");
                return 3;
            }

            var sw = Stopwatch.StartNew();
            while (!client.Snapshot.Finished && sw.ElapsedMilliseconds < 120000) Thread.Sleep(20);
            ConversionSnapshot snapshot = client.Snapshot;

            evidence.Line($"crop snapshot: finished={snapshot.Finished} done={snapshot.Done}/{snapshot.Total} " +
                          $"ok={snapshot.Ok} failed={snapshot.Failed} error={snapshot.Error ?? "-"} " +
                          $"line='{snapshot.Line}'");
            foreach (string failure in snapshot.Failures)
                evidence.Line($"crop failure: {failure}");

            // ④ 用**產品自己的解碼器**量回每一張輸出（不是把算式再算一次：算式的第二份實作
            //    正好就是最可能出現的那個錯）。輸出檔名取自引擎自己的 `output=`，不在這裡重組規則。
            int measured = 0;
            foreach (string path in paths)
            {
                string? output = LatestCropOutputOf(evidence, path);
                if (output == null || !File.Exists(LongPath.Prepare(output)))
                {
                    evidence.Line($"crop output: {Path.GetFileName(path)} -> (no output file was reported)");
                    continue;
                }
                (int w, int h) = MeasureImage(output);
                evidence.Line($"crop output: {Path.GetFileName(path)} {ImageSizeOf(path)} -> " +
                              $"{Path.GetFileName(output)} {w}x{h}");
                measured++;
            }
            evidence.Line($"crop measured outputs: {measured}/{paths.Length}");

            bool untouched = true;
            foreach ((string path, string hash, long length, DateTime mtime) in before)
            {
                string nowHash = Sha256Of(path);
                bool same = nowHash == hash && new FileInfo(path).Length == length
                            && File.GetLastWriteTimeUtc(path) == mtime;
                untouched &= same;
                evidence.Line($"tool crop: source-untouched[{Path.GetFileName(path)}]={same} " +
                              $"(sha256 {hash.Substring(0, 12)} -> {nowHash.Substring(0, 12)}, " +
                              $"len {length}, mtime {(mtime == File.GetLastWriteTimeUtc(path) ? "same" : "CHANGED")})");
            }

            if (!untouched)
            {
                evidence.Line("--crop FAILED: a source file was modified (spec 5 violation)");
                return 1;
            }
            if (!snapshot.Finished || snapshot.Failed > 0)
            {
                evidence.Line($"--crop FAILED: finished={snapshot.Finished} failed={snapshot.Failed}");
                return 3;
            }
            evidence.Line($"crop summary: ✓ {snapshot.Ok}/{paths.Length} (all outputs written, sources untouched)");
            return 0;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// M3 第六顆扇形（2026-10-07）：`--merge-pdf-inspect &lt;file&gt;`——印出一個 PDF 的頁數與
    /// **逐頁**的尺寸（point）、`/Rotate`、以及**解壓後**內容流的長度與雜湊。**只讀，永不寫檔。**
    ///
    /// 為什麼這條縫是整個切片的關鍵：合併的產物是一個**二進位檔**，而「第 3 頁是不是
    /// `fx-c` 的第 2 頁」不能靠產品的 `ok=True` 回答——那只證明它自己覺得成功了。
    /// 唯一客觀的作法是把每一頁**自己的幾何與內容**印出來，讓套件拿它與**自己寫死的期望序列**
    /// 逐項比對。所以 fixture 的每一頁尺寸都不同（見 `fixtures/pdf/README.md`）：
    /// 任何換序、少頁、多頁、或尺寸被縮放，都逃不過那個序列。
    ///
    /// ⚠ 它讀的是**已經在磁碟上的檔**，這件事不是選擇而是硬性的：PDFsharp 在 `Save()` 之後
    /// 會把那個 in-memory 文件鎖住，連 `PageCount` 都會丟 `InvalidOperationException`
    /// （2026-10-07 實測），所以「存檔後回頭讀那個物件」這條路根本走不通。
    ///
    /// ⚠ **內容流的長度不可以當成內容本身**：兩個都是空的內容流長度相同，於是「內容有沒有
    /// 跟著走」會得到一個**假陽性**。所以這裡印的是 **SHA-256 的前 12 碼**，而且印出長度讓
    /// 套件可以拒絕「兩邊都是 0」這種通過（見 `verify-m3d.ps1` 的 S20.07）。
    ///
    /// ⚠ 內容流是**壓縮**的，所以「在檔案位元組裡搜字串」找不到它是正常的——那不是內容不見了。
    /// 這裡讀的是 `UnfilteredValue`（已解壓），與套件比對的是同一個東西。
    ///
    /// 回傳碼：0 讀成功、2 找不到檔或不是本產品認得的 PDF、3 PDFsharp 讀不開（含加密）。
    /// </summary>
    internal static int MergePdfInspectPdf(Evidence evidence, string path)
    {
        if (!File.Exists(LongPath.Prepare(path)))
        {
            evidence.Line($"--merge-pdf-inspect: file not found: {path}");
            return 2;
        }

        string name = Path.GetFileName(path);
        try
        {
            using PdfSharp.Pdf.PdfDocument doc =
                PdfSharp.Pdf.IO.PdfReader.Open(LongPath.Prepare(path),
                                               PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            int declared = doc.PageCount;
            int read = 0;
            // ⚠ **`ok=True` 不可以印在讀完每一頁之前。**
            // 2026-10-07 實測：`fx-broken-page-tree.pdf` 的 `PageCount` 說 1，而
            // `doc.Pages[0]` 會丟 NullReferenceException——那正是 #388 的形狀
            // （**開得起來、碰到頁面才炸**）。先印 `pages=1 ok=True` 會讓那一行看起來像
            // 「這是一份好好的 1 頁文件」，而下一個讀者（人或套件）就會拿一個假的事實去比對。
            // 所以順序改成：先把每一頁唸完（`read` 逐頁增加），**全部唸完才**宣告成功。
            // 失敗那一行帶 `read=<n>`，讓「唸了幾頁才炸」是一個看得到的事實。
            for (int i = 0; i < declared; i++)
            {
                PdfSharp.Pdf.PdfPage page = doc.Pages[i];
                byte[] contents = PageContentBytes(page);
                string sha = contents.Length > 0
                    ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents))
                             .Substring(0, 12).ToLowerInvariant()
                    : "-";
                // 格式是驗收斷言的一部分（固定、可 regex 反解）：
                //   merge-pdf inspect: page=<n> size=<w>x<h> rotate=<deg> contents=<bytes> sha=<12 hex>
                // 尺寸只有**一個小數位**：Point 是 double，而 200 pt 可能印成 199.99999…
                // （XUnit 的往返誤差）。0.1 pt 的解析度對「這是不是原來那一頁」完全足夠
                // ——fixture 之間差 10 pt 以上，而縮放造成的差更是整數倍。
                evidence.Line($"merge-pdf inspect: page={i + 1} " +
                              $"size={page.Width.Point:0.#}x{page.Height.Point:0.#} " +
                              $"rotate={page.Rotate} contents={contents.Length} sha={sha}");
                read++;
            }
            evidence.Line($"merge-pdf inspect: file={name} pages={declared} read={read} ok=True");
            evidence.Line($"merge-pdf inspect done: file={name} pages={declared} read={read} ok=True");
            return 0;
        }
        catch (Exception ex)
        {
            evidence.Line($"merge-pdf inspect: file={name} failed {ex.GetType().Name}: {ex.Message}");
            evidence.Line($"merge-pdf inspect done: file={name} pages=0 read=0 ok=False");
            return 3;
        }
    }

    /// <summary>
    /// 一頁的**解壓後內容流位元組**（可能有好幾個：`/Contents` 是陣列時要接起來）。
    ///
    /// 為什麼要把好幾個串起來而不是只看第一個：`/Contents` 在 PDF 裡可以是**一個陣列**，
    /// 而只看第一個會讓「內容有兩段、第一段被保留、第二段被丟掉」看起來完全正常。
    /// 串起來之後那個損失會改變雜湊，於是斷言抓得到。
    ///
    /// 讀不到內容（頁面沒有 `/Contents`）時回**空陣列**，而呼叫端會印 `contents=0 sha=-`
    /// ——那是誠實的結果（不是「雜湊是空的」這種看起來像通過的東西）。
    /// </summary>
    private static byte[] PageContentBytes(PdfSharp.Pdf.PdfPage page)
    {
        using var buffer = new MemoryStream();
        foreach (object element in page.Contents.Elements)
        {
            // PdfReference → PdfContent → Stream（PdfDictionary.PdfStream）。
            // ⚠ 型別名稱是 `PdfSharp.Pdf.PdfDictionary+PdfStream`（nested），
            //    而 `PdfSharp.Pdf.PdfStream` 在 6.2.4 **不存在**（編譯會失敗）。
            if (element is PdfSharp.Pdf.Advanced.PdfReference reference
                && reference.Value is PdfSharp.Pdf.Advanced.PdfContent content)
            {
                byte[] raw = content.Stream.UnfilteredValue;
                buffer.Write(raw, 0, raw.Length);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// M3 第六顆扇形（2026-10-07）：`--merge-pdf &lt;p1;p2;...&gt;`——對這幾個 PDF（**依序**）
    /// 跑工具 `merge-pdf`，走**真的 worker**（`op=merge-pdf`、管道、快照）。
    ///
    /// 形狀與 <see cref="CropDiagnostic"/> 一字不差（那一條也是分號分隔的一次批次），
    /// 差別在**它是一個「多輸入、單輸出」的 op**：worker 那一側只跑一次、只回一個結果、
    /// 只寫一個檔。所以這裡的期望也是「一個輸出、零個失敗」而不是「N 個檔各一個輸出」。
    ///
    /// 它把四件事印成可斷言的事實：
    ///   ①**快照那一行**（`ok=`／`failed=`／產品的訊息）——使用者看到的就是那一句；
    ///   ②**失敗清單的原文**（`--merge-pdf failure:`）——fail-fast 的訊息必須**指名是哪個檔**，
    ///     而那句話只在這裡出現，套件要能逐字元讀到它；
    ///   ③**輸出檔名**（`--merge-pdf output:`）——套件拿它去跑 `--merge-pdf-inspect`，
    ///     所以頁序的斷言是**從產品自己的產物**反推，不是從這裡印的摘要；
    ///   ④來源檔的**三項指紋**（sha256／長度／mtime）前後比對（規格 §5 的第一名）——
    ///     這一條對**成功與失敗兩條路徑**都要成立，而那正是 fail-fast 最容易被寫壞的地方。
    ///
    /// ⚠ **回傳碼刻意區分「失敗」的兩種意思**（與 <see cref="CropDiagnostic"/> 同一條規矩）：
    ///   0 ＝ 合併成功且**真的寫出一個檔**；
    ///   3 ＝ **引擎依 fail-fast 拒絕了整批**（這是正確的行為，不是這條診斷壞了）；
    ///   1 ＝ 來源被動到（規格 §5 紅線）、2 ＝ 參數或來源檔本身的問題（根本沒送出去）。
    /// 把「正確的拒絕」回成 3 而不是 0，才讓套件有辦法斷言「這一批真的沒有產出」。
    /// </summary>
    internal static int MergePdfDiagnostic(Evidence evidence, Options opt)
    {
        string[] paths = opt.MergePdf!.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0)
        {
            evidence.Line("--merge-pdf: no source path was given");
            return 2;
        }
        foreach (string path in paths)
        {
            if (!File.Exists(LongPath.Prepare(path)))
            {
                evidence.Line($"--merge-pdf: source not found: {path}");
                return 2;
            }
        }

        var before = paths.Select(p => (Path: p, Hash: Sha256Of(p), Length: new FileInfo(p).Length,
                                        Mtime: File.GetLastWriteTimeUtc(p))).ToList();

        Settings settings = _settings ?? new Settings();
        string? outputDir = settings.ResolveOutputDirectory();
        evidence.Line($"tool merge-pdf: outputDir={outputDir ?? "(source folder)"}");
        // 順序**逐項印出來**：它是這個工具唯一的輸入（`a;b` 與 `b;a` 是兩個不同的請求），
        // 而套件要能從產品的輸出反推「我送出去的順序是什麼」。
        evidence.Line($"tool merge-pdf: request files={paths.Length} order=[" +
                      $"{string.Join(",", paths.Select(Path.GetFileName))}] (the order IS the page order)");

        int journalRecovered = new TempJournal(TempJournal.DefaultPath()).RecoverStale();
        evidence.Line($"temp journal: recovered {journalRecovered} stale temp file(s)");

        string exePath = Environment.ProcessPath ?? $"{ProductInfo.Name}.exe";
        var client = new ConversionClient(exePath, evidence.Line);
        try
        {
            if (!client.Start(paths, "", 100, outputDir: outputDir,
                              toolOp: WorkerProtocol.OpMergePdf,
                              out string? startError, pageSize: opt.MergePageSize))
            {
                evidence.Line($"--merge-pdf FAILED: the worker did not start ({startError})");
                return 3;
            }

            var sw = Stopwatch.StartNew();
            while (!client.Snapshot.Finished && sw.ElapsedMilliseconds < 120000) Thread.Sleep(20);
            ConversionSnapshot snapshot = client.Snapshot;

            evidence.Line($"merge-pdf snapshot: finished={snapshot.Finished} done={snapshot.Done}/{snapshot.Total} " +
                          $"ok={snapshot.Ok} failed={snapshot.Failed} error={snapshot.Error ?? "-"} " +
                          $"line='{snapshot.Line}'");
            foreach (string failure in snapshot.Failures)
                evidence.Line($"merge-pdf failure: {failure}");
            // 「為什麼」＝ stage 與真正的例外型別。它**不是**使用者訊息的一部分（那一句刻意
            // 只說「無法合併這個 PDF」），但使用者 2026-10-07 的裁定是「明確說出是哪個檔、
            // **為什麼**」，所以它必須是一條驗收讀得到的行——只在 worker 日誌裡不算數
            // （診斷的 stdout 才是斷言對象）。
            //
            // ⚠ **只在失敗時印**（2026-10-07 實測踩到）：成功的結果也會帶 `Detail`（那是
            // 「3 file(s), 6 page(s), 4461B in 148 ms -> …」這種摘要），所以不加條件的話
            // 成功的那一輪也會印一行 `merge-pdf why:`，而它的內容與「為什麼失敗」無關。
            // 那會讓驗收拿一個假的事實去比對（而且它看起來完全合理）。
            if (snapshot.Failed > 0 && snapshot.LastDetail != null)
                evidence.Line($"merge-pdf why: {snapshot.LastDetail}");

            // 輸出檔名取自**跑完之後在來源資料夾裡找到的那一個新檔**（見 LatestMergeOutputOf：
            // 它不自己組名字，因為命名規則只有 NamingPolicy 一份）。套件會拿這一個路徑
            // 去跑 `--merge-pdf-inspect`，所以頁序的斷言是**從產品自己的產物**反推的。
            // ⚠ 只有在**合併成功**時才找：fail-fast 拒絕整批時，這一行必須找不到東西
            //    ——套件的斷言就是「這裡沒有任何新檔」。
            string? output = snapshot.Ok > 0 ? LatestMergeOutputOf(evidence, paths) : null;
            if (output != null)
                evidence.Line($"merge-pdf output: {Path.GetFileName(output)} " +
                              $"exists={File.Exists(LongPath.Prepare(output))}");

            bool untouched = true;
            foreach ((string path, string hash, long length, DateTime mtime) in before)
            {
                string nowHash = Sha256Of(path);
                bool same = nowHash == hash && new FileInfo(path).Length == length
                            && File.GetLastWriteTimeUtc(path) == mtime;
                untouched &= same;
                evidence.Line($"tool merge-pdf: source-untouched[{Path.GetFileName(path)}]={same} " +
                              $"(sha256 {hash.Substring(0, 12)} -> {nowHash.Substring(0, 12)}, " +
                              $"len {length}, mtime {(mtime == File.GetLastWriteTimeUtc(path) ? "same" : "CHANGED")})");
            }

            if (!untouched)
            {
                evidence.Line("--merge-pdf FAILED: a source file was modified (spec 5 violation)");
                return 1;
            }
            if (!snapshot.Finished)
            {
                evidence.Line("--merge-pdf FAILED: the worker never finished");
                return 3;
            }
            if (snapshot.Failed > 0 || snapshot.Ok == 0)
            {
                // ⚠ 這一條**不是**「診斷壞了」：fail-fast 拒絕整批時回的就是這條路
                // （使用者 2026-10-07 裁定）。上面那幾行 `merge-pdf failure:` 已經指名是哪個檔、
                // 為什麼；套件要斷言的是「沒有任何新檔」＋「訊息指名了那個檔」。
                evidence.Line($"--merge-pdf: the batch was REFUSED (fail-fast): ok={snapshot.Ok} " +
                              $"failed={snapshot.Failed}; nothing was written");
                return 3;
            }
            evidence.Line($"merge-pdf summary: ✓ 1 output from {paths.Length} source(s), sources untouched");
            return 0;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// 這一輪合併寫出來的那一個檔。
    ///
    /// ⚠ 這**不是**第二份命名規則（與 <see cref="LatestCropOutputOf"/> 同一條理由，逐條相同）：
    /// 命名規則仍然只有 <see cref="NamingPolicy"/> 一份，這裡只是「跑完之後在資料夾裡找
    /// 那一個新檔案」。之所以不自己組：「組出正確的名字」正是會與產品不一致的那種第二份實作
    /// （撞名加「 (1)」、輸出資料夾回退…）；而**這裡若找錯檔案，下面 inspect 的就是另一個檔**。
    ///
    /// 與裁切那一條的兩個差異，兩個都是必要的：
    ///   ① **合併的來源與輸出副檔名相同**（都是 `.pdf`），所以「與來源同主檔名」的候選
    ///      可能**就是來源自己**。裁切那一條靠副檔名不同天然避開這件事，這裡必須明講：
    ///      `sources` 裡的任何一個路徑都不算候選。
    ///   ② 輸出檔名沿用**第一個來源檔**的主檔名（見 `ConversionEngine.MergePdf` 的命名說明），
    ///      所以搜尋的基準是 `sources[0]`，不是每一個來源。
    ///
    /// 取**編號最小**的那一個：否則會回一個舊批次留下的檔案，而「這次有沒有產出」就變成
    /// 一個說不準的答案（這正是 fail-fast 斷言最需要看的一件事）。
    /// </summary>
    private static string? LatestMergeOutputOf(Evidence evidence, string[] sources)
    {
        if (sources.Length == 0) return null;
        string full = Path.GetFullPath(sources[0]);
        string folder = Path.GetDirectoryName(full) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(full);
        // ⚠ **輸出的副檔名是 `.pdf`，不是來源的副檔名**（2026-10-14）。
        // 原本這裡寫 `Path.GetExtension(full)`（來源的副檔名），而它在兩種情況下剛好都對：
        // crop 是同格式進出，而「PDF 合併 PDF」的來源本來就是 `.pdf`。
        // 圖片合併是**第一次**輸出格式與來源不同（`.png` 進、`.pdf` 出），於是這一問找不到
        // 產物，日誌上就出現一句自相矛盾的「成功，但那個資料夾裡沒有新檔案」——
        // 而檔案其實好好地在旁邊。診斷說謊比沒有診斷更糟。
        string extension = ".pdf";

        var sourceSet = new HashSet<string>(
            sources.Select(s => Path.GetFullPath(s)), StringComparer.OrdinalIgnoreCase);

        string? best = null;
        int bestIndex = int.MaxValue;
        for (int n = 0; n <= 9; n++)
        {
            string candidate = n == 0 ? Path.Combine(folder, stem + extension)
                                      : Path.Combine(folder, $"{stem} ({n}){extension}");
            if (sourceSet.Contains(Path.GetFullPath(candidate))) continue;   // 來源自己不是輸出
            if (!File.Exists(LongPath.Prepare(candidate))) continue;
            if (n >= bestIndex) continue;
            bestIndex = n;
            best = candidate;
        }
        if (best == null)
            evidence.Line($"merge-pdf output: no output file was found next to {Path.GetFileName(sources[0])} " +
                          "(the run reported success but nothing new is in that folder)");
        return best;
    }

    /// <summary>
    /// M3 診斷（2026-10-08）：`--remove-bg &lt;path&gt;`——工具 `remove-bg`（第七顆扇形）走
    /// **完整的一條路**（worker 行程、`op=remove-bg`、管道、快照），形狀與
    /// <see cref="MergePdfDiagnostic"/> 一字不差。
    ///
    /// 這一條要能斷言四件事，所以它印的東西比別的診斷多：
    /// <list type="number">
    /// <item><b>三種狀態各自有名字</b>：<c>remove-bg snapshot:</c> 之後的
    /// <c>remove-bg failure:</c> 行會帶 <see cref="ConversionError"/> 的名字
    /// （`ModelUnavailable`／`ModelCorrupt`／`NoSubjectFound`），而回傳碼是 **3**
    /// ——「引擎正確地拒絕」與「這條診斷壞了」必須分得開。</item>
    /// <item><b>拒絕時一個檔都沒寫</b>：跑之前與跑之後各掃一次來源資料夾裡
    /// <b>所有以來源主檔名開頭的檔案</b>（<c>dir state:</c> 兩行 ＋ <c>remove-bg wrote:</c> 一行）。
    /// 掃整個前綴而不是只掃那一個預期檔名，是為了讓「寫到一半的殘檔」也算進去——
    /// 那正是這一條要防的東西。</item>
    /// <item><b>來源檔沒被動到</b>（規格 §5 紅線）：sha256／長度／mtime 三項逐檔比對。</item>
    /// <item><b>產物真的存在且與來源同尺寸</b>：成功時印出那一個新檔的名字與從**它自己的像素**
    /// 量到的尺寸（套件再拿它去跑 <c>--remove-bg-inspect</c> 量 alpha）。</item>
    /// </list>
    ///
    /// <param name="evidence">證據通道（＝斷言對象）。</param>
    /// <param name="opt">命令列。</param>
    /// <returns>
    /// 0 ＝真的寫出了一張 PNG；1 ＝來源檔被動到（規格 §5）；2 ＝參數或來源檔本身的問題；
    /// 3 ＝引擎拒絕（缺模型／模型損壞／找不到人像主體，三者都不寫檔）。
    /// </returns>
    /// </summary>
    internal static int RemoveBgDiagnostic(Evidence evidence, Options opt)
    {
        string source = opt.RemoveBg!;
        if (!File.Exists(LongPath.Prepare(source)))
        {
            evidence.Line($"--remove-bg: source not found: {source}");
            return 2;
        }

        string hash = Sha256Of(source);
        long length = new FileInfo(source).Length;
        DateTime mtime = File.GetLastWriteTimeUtc(source);
        string modelPath = RemoveBgModel.ResolvePath(opt.RemoveBgModel);
        bool modelExists = File.Exists(LongPath.Prepare(modelPath));

        evidence.Line($"tool remove-bg: source={Path.GetFileName(source)} {length}B sha256={hash.Substring(0, 16)}");
        // 模型的三件事**逐項**印出來：它是這一顆扇形唯一的外部資源，而「檔案不在」與
        // 「檔案在但是壞的」的分界就在下面這兩行（`exists=` 與 `sha256=`／`length=`）。
        // 有 sha256 才驗得動「這一輪用的是哪一顆模型」——不然「模型換了但結果一樣」看不出來。
        evidence.Line($"tool remove-bg: model path='{modelPath}' exists={modelExists}" +
                      (opt.RemoveBgModel == null ? " (default beside the exe)" : " (--remove-bg-model)"));
        if (modelExists)
        {
            var info = new FileInfo(modelPath);
            evidence.Line($"tool remove-bg: model length={info.Length}B (expected {RemoveBgModel.ExpectedBytes}B) " +
                          $"sha256={Sha256Of(modelPath)}");
        }

        Settings settings = _settings ?? new Settings();
        string? outputDir = settings.ResolveOutputDirectory();
        evidence.Line($"tool remove-bg: outputDir={outputDir ?? "(source folder)"}");

        // 邊緣精修的模式（2026-10-08）。三層優先序，而且**每一層都印出來是哪一層**：
        //   ①命令列 `--remove-bg-refine`（診斷接縫；認不得的值＝拒絕，回 2）
        //   ②設定檔的 RemoveBgRefine（正式路徑也讀的那一個）
        //   ③EdgeRefine.DefaultMode（guided，開）
        // ⚠ 送給 worker 時**預設值一律送 null**（見 ConversionClient.Start）：送 null 與送
        // "guided" 的行為相同，但只有送 null 能讓既有封包逐位元不變。
        string refineRequest = (opt.RemoveBgRefine ?? "").Trim();
        string refineMode;
        string refineOrigin;
        if (refineRequest.Length > 0)
        {
            refineMode = EdgeRefine.CanonicalModeOrEmpty(refineRequest);
            if (refineMode.Length == 0)
            {
                evidence.Line($"--remove-bg FAILED: --remove-bg-refine '{refineRequest}' is not one of " +
                              $"{string.Join("/", EdgeRefine.Modes)}");
                return 2;
            }
            refineOrigin = "--remove-bg-refine";
        }
        else
        {
            refineMode = EdgeRefine.CanonicalMode(settings.RemoveBgRefine);
            refineOrigin = "settings";
        }
        evidence.Line($"tool remove-bg: refine={refineMode} ({refineOrigin}" +
                      (refineMode == EdgeRefine.DefaultMode ? ", the default" : "") +
                      $") r={EdgeRefine.RadiusGain:G}x upscale (cap {EdgeRefine.RadiusMax}) " +
                      $"eps={EdgeRefine.Epsilon:G}");

        int journalRecovered = new TempJournal(TempJournal.DefaultPath()).RecoverStale();
        evidence.Line($"temp journal: recovered {journalRecovered} stale temp file(s)");

        // 跑之前：來源資料夾裡所有「以來源主檔名開頭」的檔案（含殘檔）。
        var beforeFiles = FilesWithStem(source);
        evidence.Line($"remove-bg dir state before: {DescribeDirState(beforeFiles)}");

        string exePath = Environment.ProcessPath ?? $"{ProductInfo.Name}.exe";
        var client = new ConversionClient(exePath, evidence.Line);
        try
        {
            // ⚠ 模型路徑**送絕對路徑**（ResolvePath 已經轉過）：相對路徑在 worker 那一側
            // 會對上它自己的工作目錄，那不是這一端解析出來的那一個。
            if (!client.Start(new[] { source }, "", 100, outputDir: outputDir,
                              toolOp: WorkerProtocol.OpRemoveBg, out string? startError,
                              removeBgModel: modelPath,
                              removeBgRefine: refineMode == EdgeRefine.DefaultMode ? null : refineMode))
            {
                evidence.Line($"--remove-bg FAILED: the worker did not start ({startError})");
                return 3;
            }

            var sw = Stopwatch.StartNew();
            // 上限比合併寬：session 第一次建要 139–190 ms，加上 512 邊的前處理與推論，
            // 實測單張 ~1.5 s（含 worker 起停）。30 s 是「這條路真的活著」與「它卡住了」的分界。
            while (!client.Snapshot.Finished && sw.ElapsedMilliseconds < 30000) Thread.Sleep(20);
            ConversionSnapshot snapshot = client.Snapshot;

            evidence.Line($"remove-bg snapshot: finished={snapshot.Finished} done={snapshot.Done}/{snapshot.Total} " +
                          $"ok={snapshot.Ok} failed={snapshot.Failed} error={snapshot.Error ?? "-"} " +
                          $"line='{snapshot.Line}'");
            foreach (string failure in snapshot.Failures)
                evidence.Line($"remove-bg failure: {failure}");
            // 「為什麼」＝引擎的 Detail。⚠ 只在失敗時印（與 merge-pdf 同一條規矩：成功的
            // Detail 是一句摘要，「為什麼失敗」與它無關——印出來會讓套件拿一個假的事實去比對）。
            if (snapshot.Failed > 0 && snapshot.LastDetail != null)
                evidence.Line($"remove-bg why: {snapshot.LastDetail}");

            var afterFiles = FilesWithStem(source);
            evidence.Line($"remove-bg dir state after: {DescribeDirState(afterFiles)}");
            var newFiles = afterFiles.Keys.Where(k => !beforeFiles.ContainsKey(k)).OrderBy(k => k).ToList();
            evidence.Line($"remove-bg wrote: count={newFiles.Count}" +
                          (newFiles.Count == 0 ? " (none)" : $" [{string.Join(",", newFiles.Select(Path.GetFileName))}]"));

            string? output = null;
            if (snapshot.Ok > 0 && newFiles.Count > 0)
            {
                output = newFiles[0];
                (int w, int h) = MeasureImage(output);
                evidence.Line($"remove-bg output: {Path.GetFileName(output)} " +
                              $"exists={File.Exists(LongPath.Prepare(output))} size={w}x{h}");
            }
            else if (snapshot.Ok > 0)
            {
                evidence.Line("remove-bg output: the run reported success but no new file is in that folder");
            }

            bool untouched = true;
            {
                string nowHash = Sha256Of(source);
                bool same = nowHash == hash && new FileInfo(source).Length == length
                            && File.GetLastWriteTimeUtc(source) == mtime;
                untouched = same;
                evidence.Line($"tool remove-bg: source-untouched[{Path.GetFileName(source)}]={same} " +
                              $"(sha256 {hash.Substring(0, 12)} -> {nowHash.Substring(0, 12)}, " +
                              $"len {length}, mtime {(mtime == File.GetLastWriteTimeUtc(source) ? "same" : "CHANGED")})");
            }

            if (!untouched)
            {
                evidence.Line("--remove-bg FAILED: the source file was modified (spec 5 violation)");
                return 1;
            }
            if (!snapshot.Finished)
            {
                evidence.Line("--remove-bg FAILED: the worker never finished");
                return 3;
            }
            if (snapshot.Failed > 0 || snapshot.Ok == 0)
            {
                // ⚠ 這一條**不是**「診斷壞了」：缺模型、模型損壞、找不到人像主體，三者都是
                // **正確的拒絕**（而且都不寫檔）。上面那幾行 `remove-bg failure:` 已經指名是哪一種，
                // 套件要斷言的是「錯誤碼是哪一個」＋「`remove-bg wrote: count=0`」。
                evidence.Line($"--remove-bg: REFUSED by the engine: ok={snapshot.Ok} " +
                              $"failed={snapshot.Failed}; nothing was written");
                return 3;
            }
            evidence.Line($"remove-bg summary: ✓ 1 png from 1 source, source untouched");
            return 0;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// 來源資料夾裡**所有以「來源主檔名」開頭的檔案**（完整路徑 → 長度）。
    ///
    /// ⚠ 刻意用**前綴**掃而不是只掃那一個預期檔名：去背的輸出是「同主檔名 ＋ `.png`」，
    /// 而撞名時產品會寫 `name (1).png`（<see cref="NamingPolicy"/>）。只掃一個候選名字的話，
    /// 一個「寫了 `name (1).png`」的實作會被判成「什麼都沒寫」——那正是這一條要防的假綠燈。
    /// 前綴掃法同時涵蓋殘檔（`name.tmp-…`），而殘檔正是「拒絕時一個檔都不該有」的重點。
    /// </summary>
    private static Dictionary<string, long> FilesWithStem(string source)
    {
        var found = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string full = Path.GetFullPath(source);
        string folder = Path.GetDirectoryName(full) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(full);
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                // 來源自己不算輸出（`photo.jpg` 也以 `photo` 開頭）。
                if (string.Equals(file, full, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                try { found[file] = new FileInfo(file).Length; } catch (Exception) { }
            }
        }
        catch (Exception ex)
        {
            found[$"(unreadable: {ex.GetType().Name})"] = -1;
        }
        return found;
    }

    /// <summary>上一個方法那一份字典的一行摘要（排序後，所以兩次呼叫可以直接比對）。</summary>
    private static string DescribeDirState(Dictionary<string, long> state) =>
        state.Count == 0
            ? "count=0"
            : $"count={state.Count} [{string.Join(",", state.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                                                          .Select(k => $"{Path.GetFileName(k.Key)}:{k.Value}B"))}]";

    /// <summary>
    /// M3 診斷（2026-10-08）：`--remove-bg-inspect &lt;png&gt;`——**只讀**量一張圖的 alpha。
    ///
    /// **為什麼這一條是整個切片的關鍵**：去背的產物是一個二進位檔，而產品的 `ok=True`
    /// 只證明「它自己覺得成功了」（一個「只把 alpha 全部寫成 0」的實作照樣會回 ok）。
    /// 這一條從**成品自己的像素**把遮罩變成數字，所以套件是拿產品寫出來的檔在比對。
    ///
    /// 量的是五個區域的平均與最大 alpha：中央帶（中間 40 %）、四個角帶（各邊 10 %）、
    /// 以及上下左右四條邊帶（各 10 %）。**角帶是「背景」最粗的代理指標**，而中央帶是
    /// 「主體」的——人像構圖下主體的頭在畫面中上偏中，所以中央帶一定落在主體上。
    ///
    /// ⚠ 這**不是**在猜「哪裡是人」：它是一組**幾何固定**的區域統計，而選哪幾個區域、
    /// 拿哪一條當斷言，是驗收那一側的判斷（見 <c>tools/verify-m3d.ps1</c> 的 S22）。
    /// </summary>
    internal static int RemoveBgInspectPng(Evidence evidence, string path)
    {
        if (!File.Exists(LongPath.Prepare(path)))
        {
            evidence.Line($"--remove-bg-inspect: file not found: {path}");
            return 2;
        }

        // ⚠ WIC 的型別一律**寫全名**（System.Windows.Media.Imaging.…）：這個檔案已經有
        // `using System.Drawing.Imaging;`，而 WPF 的 `PixelFormats` 與 GDI+ 的 `PixelFormat`
        // 只差一個 s。加一行 `using System.Windows.Media.Imaging;` 不會撞名，但下一個在這裡
        // 寫 `PixelFormats` 的人會以為它是那個 GDI+ 型別——全名讓這一格沒有歧義。
        System.Windows.Media.Imaging.BitmapSource frame;
        try
        {
            (frame, _) = ConversionEngine.DecodeFirstFrame(path);
        }
        catch (Exception ex)
        {
            evidence.Line($"--remove-bg-inspect: could not decode {Path.GetFileName(path)}: " +
                          $"{ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        string pixelFormat = frame.Format.ToString();
        // 「這張圖有沒有 alpha 通道」——**只有兩個位元組是真的帶 alpha 的來源格式**：
        // Bgra32（非預乘）與 Pbgra32（預乘）。刻意**不用 `pixelFormat.Contains("A")` 這種寫法**：
        // 那是對字串的猜測，而它會把一堆沒有 alpha 的格式判成有 alpha。
        // 這一格是「JPEG 沒有透明通道，所以去背一定要寫 PNG」那一條斷言的依據，猜錯會讓它失去意義。
        // 其餘格式（含 PNG 的 24 位元組）＝**沒有 alpha**，那是誠實的答案。
        bool hasAlpha = frame.Format == System.Windows.Media.PixelFormats.Bgra32
                        || frame.Format == System.Windows.Media.PixelFormats.Pbgra32;

        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[(long)stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        // 全圖 alpha 統計。
        byte minA = 255, maxA = 0;
        double sum = 0;
        long opaque = 0, clear = 0;
        for (int p = 0; p < pixels.Length; p += 4)
        {
            byte a = pixels[p + 3];
            if (a < minA) minA = a;
            if (a > maxA) maxA = a;
            sum += a;
            if (a == 255) opaque++;
            else if (a == 0) clear++;
        }
        long total = (long)w * h;
        if (total == 0)
        {
            evidence.Line($"--remove-bg-inspect: {Path.GetFileName(path)} has no pixels");
            return 2;
        }

        evidence.Line($"remove-bg inspect: file={Path.GetFileName(path)} size={w}x{h} " +
                      $"format={pixelFormat} hasAlpha={hasAlpha}");
        evidence.Line($"remove-bg inspect: alpha min={minA / 255.0:F4} max={maxA / 255.0:F4} " +
                      $"mean={sum / total / 255.0:F4} opaque={opaque / (double)total:F4} " +
                      $"clear={clear / (double)total:F4}");

        // 過渡帶（羽化）有多寬、裡面有多陡。**這是這一顆工具唯一在改的兩個數字**（邊緣精修把
        // 模型那張 512 短邊的遮罩放大回來，代價就是過渡帶被放大倍率撐寬、梯度被撐平），
        // 所以它們必須是**產品自己講得出來**的事實，而不是驗收腳本自己算的：驗收的 S22 直接
        // 讀這兩欄，而引擎日誌那一行（在 worker 的日誌裡）用的也是同一個定義。
        //   band ＝ 0 < alpha < 255 的像素佔比（羽化的比例，越小＝光暈越窄）
        //   edge ＝ 過渡帶內 |grad alpha| 的平均（中央差分 ÷2，邊界像素不計；0..1／像素，
        //          越大＝過渡越陡＝邊緣越硬）。與 alpha 一樣除以 255 正規化。
        long band = 0;
        double edgeSum = 0;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * stride;
            for (int x = 1; x < w - 1; x++)
            {
                int p = row + x * 4 + 3;
                if (pixels[p] == 0 || pixels[p] == 255) continue;
                band++;
                double gx = (pixels[p + 4] - pixels[p - 4]) / 2.0;
                double gy = (pixels[p + stride] - pixels[p - stride]) / 2.0;
                edgeSum += Math.Sqrt(gx * gx + gy * gy) / 255.0;
            }
        }
        evidence.Line($"remove-bg inspect: matte band={band / (double)total:F4} " +
                      $"edge={(band > 0 ? edgeSum / band : 0):F4} " +
                      "(fraction of pixels in the 0<a<255 transition; mean |grad alpha| " +
                      "inside it, 0..1 per pixel)");

        (double cm, double cx) = BandStats(pixels, w, h, 0.30, 0.70, 0.30, 0.70);
        (double c1m, double c1x) = BandStats(pixels, w, h, 0.00, 0.10, 0.00, 0.10);
        (double c2m, double c2x) = BandStats(pixels, w, h, 0.90, 1.00, 0.00, 0.10);
        (double c3m, double c3x) = BandStats(pixels, w, h, 0.00, 0.10, 0.90, 1.00);
        (double c4m, double c4x) = BandStats(pixels, w, h, 0.90, 1.00, 0.90, 1.00);
        double cornerMean = (c1m + c2m + c3m + c4m) / 4;
        double cornerMax = Math.Max(Math.Max(c1x, c2x), Math.Max(c3x, c4x));
        (double topM, _) = BandStats(pixels, w, h, 0.00, 1.00, 0.00, 0.10);
        (double botM, _) = BandStats(pixels, w, h, 0.00, 1.00, 0.90, 1.00);
        (double leftM, _) = BandStats(pixels, w, h, 0.00, 0.10, 0.00, 1.00);
        (double rightM, _) = BandStats(pixels, w, h, 0.90, 1.00, 0.00, 1.00);

        // 兩個小數位：套件的門檻（0.80／0.10／0.05）離實測值很遠，所以這一級解析度足夠，
        // 而少一位就少一個「因為四捨五入而在邊界上翻面」的機會。
        evidence.Line($"remove-bg inspect: band centre={cm:F2} ({cx:F2}) corner={cornerMean:F2} ({cornerMax:F2}) " +
                      $"top={topM:F2} bottom={botM:F2} left={leftM:F2} right={rightM:F2}");
        // 四個角**各自**一列（2026-10-08 加入）。原本只有它們的平均，而平均會把「一個乾淨的角」
        // 藏在其他三個角後面：這張人像照的左下角真的有東西（西裝與兩隻狗，實測 0.9662），
        // 所以平均值 0.30 既不是「四角都乾淨」也不是「四角都有東西」——那個數字不能拿來斷言
        // 任何一角。分開列之後，「右上角是乾淨的背景」才是一句可以被推翻的話。
        evidence.Line($"remove-bg inspect: corners TL={c1m:F2} TR={c2m:F2} BL={c3m:F2} BR={c4m:F2} " +
                      $"(fraction of the full frame each)");
        evidence.Line($"remove-bg inspect done: file={Path.GetFileName(path)} ok=True");
        return 0;
    }

    /// <summary>
    /// 一個矩形區域（座標是**比例** 0..1，右／下界不含）的平均與最大 alpha。
    /// 空區域回 (0,0)——呼叫端只會拿固定比例去問，所以空區域是一種「這張圖太小」的症狀，
    /// 而 0 在那時正是誠實的答案（沒有任何像素是全不透明的）。
    /// </summary>
    private static (double Mean, double Max) BandStats(byte[] pixels, int width, int height,
                                                       double x0, double x1, double y0, double y1)
    {
        int px0 = Math.Clamp((int)Math.Round(x0 * width), 0, width);
        int px1 = Math.Clamp((int)Math.Round(x1 * width), 0, width);
        int py0 = Math.Clamp((int)Math.Round(y0 * height), 0, height);
        int py1 = Math.Clamp((int)Math.Round(y1 * height), 0, height);
        long n = 0, sum = 0;
        byte max = 0;
        for (int y = py0; y < py1; y++)
        {
            int row = y * width * 4;
            for (int x = px0; x < px1; x++)
            {
                byte a = pixels[row + x * 4 + 3];
                sum += a;
                if (a > max) max = a;
                n++;
            }
        }
        return n == 0 ? (0, 0) : (sum / (double)n / 255.0, max / 255.0);
    }

    /// <summary>
    /// 引擎這一輪寫出來的裁切輸出檔（在來源檔旁邊找）。回 null＝找不到，由呼叫端說出來。    ///
    /// ⚠ 這**不是**第二份命名規則：命名規則仍然只有 <see cref="NamingPolicy"/> 一份，這裡只是
    /// 「跑完之後在資料夾裡找那一個新檔案」。之所以不自己組：「組出正確的名字」正是會與產品
    /// 不一致的那種第二份實作（撞名加「 (1)」、輸出資料夾回退…）；而**這裡若找錯檔案，
    /// 下面量到的尺寸就會是錯的**——所以它寧可找不到並說出來。
    /// </summary>
    private static string? LatestCropOutputOf(Evidence evidence, string source)
    {
        string full = Path.GetFullPath(source);
        string folder = Path.GetDirectoryName(full) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(full);
        string extension = Path.GetExtension(full);

        // 產品的規則：同主檔名 ＋ 同副檔名，撞名才加「 (n)」。取**編號最小**的那一個，
        // 否則會回一個舊批次留下的檔案（那會讓「這次有沒有產出」變成一個說不準的答案）。
        string? best = null;
        int bestIndex = int.MaxValue;
        for (int n = 0; n <= 9; n++)
        {
            string candidate = n == 0 ? Path.Combine(folder, stem + extension)
                                      : Path.Combine(folder, $"{stem} ({n}){extension}");
            if (string.Equals(Path.GetFullPath(candidate), full, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(LongPath.Prepare(candidate))) continue;
            if (n >= bestIndex) continue;
            bestIndex = n;
            best = candidate;
        }
        if (best == null)
            evidence.Line($"crop output: no output file was found next to {Path.GetFileName(source)} " +
                          "(the run reported success but nothing new is in that folder)");
        return best;
    }

    /// <summary>用產品的解碼器量一張圖的像素尺寸（不含方向、不含縮放）。量不到回 (0,0) 並說出來。</summary>
    private static (int Width, int Height) MeasureImage(string path)
    {
        try
        {
            var (frame, _) = ConversionEngine.DecodeFirstFrame(path);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    /// <summary>來源檔的像素尺寸（診斷日誌用；量不到就寫 `?x?`）。</summary>
    private static string ImageSizeOf(string path)
    {
        (int w, int h) = MeasureImage(path);
        return w > 0 && h > 0 ? $"{w}x{h}" : "?x?";
    }

    /// <summary>檔案內容的 SHA256（十六進位大寫）。診斷用，不進產品路徑。</summary>
    private static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(LongPath.Prepare(path));
        return System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
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
        // 工具清單**永遠算**（很便宜），而且要**先算**：決定顯示哪一層需要它。
        // 顯示哪一層由 Shift 決定——不能只在 Shift 按著時才算，否則「先顯示格式層、
        // 之後才按 Shift」會找不到工具可切（PendingTools 會是空的）。
        IReadOnlyList<ToolDescriptor> tools = verdict.Status == PayloadStatus.Ready
            ? new ToolCatalog().ToolsFor(verdict)
            : Array.Empty<ToolDescriptor>();
        PendingTools = tools;

        // ⚠ **「可以轉成圖片」與「有工具適用」是兩個判定**（2026-10-07 切片 B）。
        //
        // `Classify` 現在會讓「有工具適用」的格式（PDF）也到 Ready，但**不給它第一層格式扇形**：
        // `TargetsFor("PDF")` 回空清單（PDF 不是可轉出的目標）。所以這一區的規則是：
        //   ①有可轉的目標格式      -> 既有行為：格式層，`notice` 為 null
        //   ②沒有可轉的目標但有工具 -> **工具層**：輪盤上只出現工具扇形（merge-pdf），
        //      第一層沒有東西可選，所以不出現「不支援的格式」（那句話現在是不對的：
        //      這一種格式**有**一個做得成的動作）。提示改成「沒有可轉換的目標格式」——
        //      它描述的是**格式層**為什麼空，而不是「這批檔案沒用」。
        //   ③Mixed／連目錄都不認識的 Unsupported -> 既有拒絕訊息，工具清單為空（規格 §4.3）。
        string? notice = verdict.Status switch
        {
            PayloadStatus.Mixed => "混合選取，請選同類別",       // 規格 §4.3 的原文
            PayloadStatus.Unsupported => "不支援的格式",
            _ => null
        };

        IReadOnlyList<string> targets = notice == null
            ? catalog.TargetsFor(verdict.SourceFormat)
            : Array.Empty<string>();
        if (notice == null && targets.Count == 0)
        {
            notice = "沒有可轉換的目標格式";
            // 這一層沒有東西可選，但工具層可能有 ⇒ 工具清單**不跟著清掉**（見上面 ②）。
            // 若連工具都沒有（例如一個目錄認識、卻沒有任何工具適用的格式），
            // `tools` 本來就是空的，畫面與改動前一樣。
        }

        // 把「重畫另一層需要的輸入」存起來：Shift 切層時要憑它把原本那一層畫回來。
        _wheelSourceFormat = verdict.SourceFormat ?? "?";
        _wheelFormatTargets = targets;
        _wheelPreview = PreviewFactory(targets, catalog, paths, 1);
        _wheelNotice = notice;

        // 規格 §9.5（2026-10-05）：輸出資料夾與來源不同時，**目的地必須看得見**。
        // 「該不該顯示」與「顯示什麼字」在這裡決定（這裡同時知道設定與來源路徑），
        // 畫由 WheelPresenter 負責。destination 為 null 時，畫面與日誌都與改動前逐位元相同。
        string? outputDir = (_settings ?? new Settings()).ResolveOutputDirectory();
        presenter.Destination = DestinationFolderName(outputDir, paths);
        evidence.Line($"wheel destination: outputDir={outputDir ?? "(source folder)"} " +
                      $"sourceFolder={SourceFolderOf(paths)} " +
                      $"wheelLabel={(presenter.Destination == null
                          ? "(none: the output stays next to the source, preview unchanged)"
                          : "-> " + presenter.Destination)}");

        bool showTools = (IsShiftDown() || _forceToolLayer) && PendingTools.Count > 0;
        RenderWheelLayer(presenter, showTools, p.X, p.Y);
        sw.Stop();
        // tools= 附加在行尾（既有套件比對的是這行的前綴）。
        evidence.Line($"wheel from OLE payload: files={paths.Length} status={verdict.Status} " +
                      $"source={verdict.SourceFormat ?? "UNKNOWN"} targets={targets.Count} " +
                      $"notice={(notice ?? "-")} latency={sw.Elapsed.TotalMilliseconds:F1} ms (budget 100 ms) " +
                      $"layer={(showTools ? "tool" : "format")} tools={PendingTools.Count}");
    }

    /// <summary>
    /// 輪盤上要顯示的目的地資料夾名（規格 §9.5）。**回傳 null＝不顯示**，也就是「輸出就在來源檔旁邊」：
    /// 那是預設值（<paramref name="outputDir"/> 為 null），畫面必須與改動前逐位元相同——回歸判準。
    ///
    /// 判斷規則刻意是「**解析出來的目的地**與來源檔所在資料夾同不同」，不是「設定有沒有寫」：
    /// 使用者把自訂資料夾填成來源所在的那一個資料夾時，畫面本來就該維持乾淨（顯示「→ 來源資料夾」
    /// 只會讓他以為檔案跑到別的地方去了）。多檔時比對**每一個**來源檔的資料夾，任一不同就顯示。
    ///
    /// 只回資料夾名而不是全路徑：輪盤上塞不下路徑（規格 §9.5 原文），全路徑寫進日誌。
    /// </summary>
    private static string? DestinationFolderName(string? outputDir, IReadOnlyList<string> paths)
    {
        if (outputDir == null || paths.Count == 0) return null;

        string target = Path.TrimEndingDirectorySeparator(outputDir);
        bool sameForEveryFile = true;
        foreach (string path in paths)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir == null) return null;                 // 取不到來源資料夾：不猜，維持畫面不變
            if (!string.Equals(Path.TrimEndingDirectorySeparator(dir), target,
                               StringComparison.OrdinalIgnoreCase))
            {
                sameForEveryFile = false;                 // 至少一個來源檔不在目的地裡 → 要顯示
                break;
            }
        }
        if (sameForEveryFile) return null;                // 每一個來源檔的旁邊就是目的地

        string name = Path.GetFileName(target);
        // 根目錄（例如 `C:\`）沒有資料夾名可顯示：顯示路徑本身，總比顯示一片空白好
        return name.Length > 0 ? name : target;
    }

    /// <summary>日誌用的來源資料夾（取第一筆；`-` 代表這一批沒有檔案）。</summary>
    private static string SourceFolderOf(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "-" : (Path.GetDirectoryName(Path.GetFullPath(paths[0])) ?? "-");

    /// <summary>
    /// 真的把輪盤畫出來（**唯一一處**）。初始顯示與 Shift 切層都走這裡——兩條路各畫一次的話，
    /// 兩邊遲早會畫得不一樣，而「同一層在兩個時刻長得不一樣」是使用者一眼就會發現的事。
    /// ⚠ <c>Show</c> 會把選取**重設到第一格**：切層等於換一組選項，這是刻意的（見計畫 §10.1）。
    /// </summary>
    private static void RenderWheelLayer(WheelPresenter presenter, bool tools, int x, int y)
    {
        if (tools)
        {
            IReadOnlyList<ToolDescriptor> list = PendingTools;
            presenter.Show(x, y, _wheelSourceFormat, list.Select(t => t.Label).ToArray(), 0,
                           preview: null, notice: null,
                           mode: WheelPresenter.WheelMode.Tool,
                           caption: i => i < list.Count ? list[i].Description : null,
                           icons: list.Select(t => t.Icon).ToArray());
            return;
        }

        presenter.Show(x, y, _wheelSourceFormat, _wheelFormatTargets, 0, _wheelPreview, _wheelNotice);
    }

    /// <summary>
    /// Shift 的**即時**層切換（規格 §9.4，2026-10-05 使用者裁定）。輪盤顯示期間按 Shift 換成
    /// 工具層、放開切回格式層。舊設計把「按著 Ctrl」綁在顯示那一刻，那個時間窗口很容易漏掉，
    /// 而且漏掉時畫面上沒有任何跡象——使用者只會以為功能壞了。
    ///
    /// 只在**真的換層**時才重畫：Shift 按住期間系統會重複送 keydown，不這樣擋會一直重建圖層。
    /// </summary>
    private static void SwapWheelLayer(bool shiftDown, WheelPresenter presenter, Evidence evidence)
    {
        bool showTools = (shiftDown || _forceToolLayer) && PendingTools.Count > 0;
        bool currentlyTools = presenter.Mode == WheelPresenter.WheelMode.Tool;
        if (showTools == currentlyTools) return;

        Native.GetCursorPos(out Native.POINT p);
        RenderWheelLayer(presenter, showTools, p.X, p.Y);
        evidence.Line($"wheel layer switch: {(currentlyTools ? "tool" : "format")} -> " +
                      $"{(showTools ? "tool" : "format")} (shift {(shiftDown ? "down" : "up")}, " +
                      $"selection reset to first)");
    }

    /// <summary>Shift 是否按著（第二層的即時切換鍵）。與保險 #5 的 Ctrl＋Alt＋Shift 不衝突。</summary>
    private static bool IsShiftDown() =>
        (Native.GetAsyncKeyState((int)Native.VK_SHIFT) & 0x8000) != 0;

    /// <summary>
    /// 診斷用：假裝 Shift 按著（給自動驗收用）。產品路徑**不讀這個旗標**——
    /// 它只在 <c>--wheel-tools</c> 時才為 true，讓無頭測試能走到第二層而不必合成鍵盤輸入。
    /// </summary>
    private static bool _forceToolLayer;

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

        // M3：第二層（工具輪盤）的提交。工具的「目標」是**工具 id**，不是格式——所以走 op，
        // targetFormat 留空（工具自己決定輸出格式）。id 由顯示時那一份清單推導，
        // 不在這裡硬寫任何字串。
        string? toolOp = null;
        if (presenter.Mode == WheelPresenter.WheelMode.Tool)
        {
            int index = presenter.Selected;
            toolOp = index >= 0 && index < PendingTools.Count ? PendingTools[index].Id : null;
            evidence.Line($"ENTER: tool layer -> index={index} op={(toolOp ?? "UNRESOLVED")} " +
                          $"tools=[{string.Join(",", PendingTools.Select(t => t.Id))}]");
            if (toolOp == null)
            {
                // 認不出工具就**不執行**（規格 §4.3 的拒絕語意，輪盤留在畫面上等 Esc）：
                // 寧可什麼都不做，也不要猜一個工具跑——猜錯就是「按下去了但做的是別的事」。
                evidence.Line("ENTER: tool layer refused (selected index does not map to a tool)");
                return false;
            }
        }

        evidence.Line($"ENTER: commit -> {(toolOp ?? target)}, files={paths.Length}, " +
                      $"wheelCenter=({centerX},{centerY}) (real conversion via worker, spec 9.1)");

        // M3 第四顆扇形（裁切）：**它需要一個只有使用者才知道的輸入**——框在哪裡。
        // 所以這一條不可以像其他三顆一樣直接送工作，而要先問。
        //
        // 兩條路，都是同一份權威（`--crop-rect` 的矩形與視窗拖出來的矩形都是 CropSpec）：
        //   ① 有 `--crop-rect`（診斷）：直接用那個矩形提交，**不開視窗**。這是無滑鼠的接縫，
        //      也是 `--wheel-payload --wheel-tools --wheel-tool crop` 能跑完的唯一方式。
        //   ② 沒有（正式路徑）：開裁切視窗，讓使用者框；確認之後由**主執行緒**接手起 worker
        //      （視窗那條是 WPF 執行緒，視覺層與進度藥丸都屬於主執行緒）。
        if (toolOp == WorkerProtocol.OpCrop)
        {
            string cliRect = _options.CropRect;
            string cliRatio = _options.CropRatio;
            // ⚠ 先宣告再 out：`out` 變數宣告在 if 的條件裡時，編譯器無法證明「另一個分支有被賦值」，
            // 而下面那條「--crop-rect 給了卻不能用」的拒絕正是走在另一個分支上（CS0165）。
            CropSpec fromCli = CropSpec.Free(0, 0, 1, 1);
            string cliError = "";
            bool rectFromCli = cliRect.Length > 0 && CropSpec.TryParse(cliRect, out fromCli, out cliError);
            if (rectFromCli)
            {
                if (cliRatio.Length > 0 && !CropRatios.IsCanonical(cliRatio))
                {
                    evidence.Line($"ENTER: crop refused (--crop-ratio '{cliRatio}' is not one of " +
                                  $"{string.Join("/", CropRatios.All.Select(p => p.Id))})");
                    return false;
                }
                CropSpec spec = fromCli with { RatioId = CropRatios.CanonicalId(cliRatio) };
                evidence.Line($"ENTER: crop uses --crop-rect {spec.Describe()} (diagnostic seam: " +
                              "no window, no mouse)");
                guard.OnUserResolved("enter");      // 收起輪盤
                if (runner == null)
                {
                    evidence.Line("ENTER: no conversion runner in this mode (diagnostic path)");
                    return true;
                }
                StartCrop(evidence, runner, paths, spec, centerX, centerY,
                          _settings ?? new Settings());
                PendingPaths = Array.Empty<string>();
                PendingTools = Array.Empty<ToolDescriptor>();
                return true;
            }
            if (cliRect.Length > 0)
            {
                evidence.Line($"ENTER: crop refused (--crop-rect '{cliRect}' is unusable: {cliError})");
                return false;
            }

            evidence.Line($"ENTER: crop needs a rectangle -> opening the crop window for " +
                          $"{paths.Length} file(s) (the commit will happen when the user confirms)");
            guard.OnUserResolved("enter");      // 收起輪盤
            string[] batch = paths;
            CropUi.Open(batch, spec =>
            {
                // ⚠ 這裡是 **WPF UI 執行緒**。起 worker 會碰視覺層與進度藥丸（都屬於主執行緒），
                // 所以只排隊，由既有的週期回呼（見 DrainPendingCrops）在主執行緒上接手。
                PendingCrops.Enqueue((batch, spec));
                evidence.Line($"crop ui: the user confirmed {spec.Describe()} -> queued for the main " +
                              "thread (the worker starts there)");
            }, evidence.Line);
            PendingPaths = Array.Empty<string>();
            PendingTools = Array.Empty<ToolDescriptor>();
            // **回 false**：這一刻還沒有任何東西被轉檔，說 true 會讓 `--wheel-payload` 那一條
            // 診斷去等一個永遠不會開始的工作（然後回報一個看起來成功的 0）。
            return false;
        }

        // M3 第六顆扇形（合併 PDF，切片 B 的第二刀）：它與裁切那一顆**形狀相同**——都有一個
        // 「只有使用者才知道的輸入」。裁切的那一個是框在哪裡；合併的這一個是**檔案的前後順序**。
        //
        // 這一顆**不需要**任何新的核心：`ConversionEngine.MergePdf` 的排序語意本來就是
        // 「陣列順序＝頁序」（切片 A 已經把它做成一個原子操作），所以這一刀唯一要做的事是
        // **把視窗排出來的順序送進去**。也就是說這一區沒有第二份排序規則——
        // 視窗的 `MergeWindow.Order` 就是送進 `paths` 的那個陣列。
        //
        // 兩條路，都是同一份權威（`--merge-pdf a;b;c` 的順序與視窗拖出來的順序都是「一組有序路徑」）：
        //   ① 有 `--merge-pdf`（診斷）：直接用那個順序提交，**不開視窗**。這是無滑鼠的接縫，
        //      也是 `--wheel-payload --wheel-tools --wheel-tool merge-pdf` 能跑完的唯一方式
        //      （與 `--crop-rect` 同一個理由）。
        //   ② 沒有（正式路徑）：開排序視窗，讓使用者排；確認之後由**主執行緒**接手起 worker
        //      （視窗那條是 WPF 執行緒，視覺層與進度藥丸都屬於主執行緒）。
        if (toolOp == WorkerProtocol.OpMergePdf)
        {
            string cliOrder = _options.MergePdf ?? "";
            if (cliOrder.Length > 0)
            {
                string[] ordered = cliOrder.Split(';', StringSplitOptions.RemoveEmptyEntries);
                // ⚠ 這裡的拒絕條件與引擎的**前置檢查**是兩件事，刻意不在這裡重複引擎的判準：
                //    這裡只擋「連送都不該送」的請求（少於 2 個檔、或路徑不存在），
                //    其餘（格式不對、壞檔、加密）一律交給引擎，由 fail-fast 指名是哪個檔、為什麼。
                //    在這裡自己判一次會變成第二份規則，而兩份不一致時使用者拿到的是這裡的訊息。
                if (ordered.Length < 2)
                {
                    evidence.Line($"ENTER: merge refused (--merge-pdf needs at least 2 files, got " +
                                  $"{ordered.Length}: '{cliOrder}')");
                    return false;
                }
                string missing = "";
                foreach (string candidate in ordered)
                    if (!AtomicWriter.Exists(candidate)) { missing = candidate; break; }
                if (missing.Length > 0)
                {
                    evidence.Line($"ENTER: merge refused (--merge-pdf lists a file that does not exist: " +
                                  $"'{missing}')");
                    return false;
                }

                evidence.Line($"ENTER: merge uses --merge-pdf order " +
                              $"[{string.Join(",", ordered.Select(Path.GetFileName))}] " +
                              "(diagnostic seam: no window, no mouse)");
                guard.OnUserResolved("enter");      // 收起輪盤
                if (runner == null)
                {
                    evidence.Line("ENTER: no conversion runner in this mode (diagnostic path)");
                    return true;
                }
                StartMerge(evidence, runner, ordered, centerX, centerY, _settings ?? new Settings());
                PendingPaths = Array.Empty<string>();
                PendingTools = Array.Empty<ToolDescriptor>();
                return true;
            }

            evidence.Line($"ENTER: merge needs an order -> opening the merge window for " +
                          $"{paths.Length} file(s) (the commit will happen when the user confirms)");
            guard.OnUserResolved("enter");      // 收起輪盤
            string[] batch = paths;
            MergeUi.Open(batch, (ordered, pageSize) =>
            {
                // ⚠ 這裡是 **WPF UI 執行緒**。起 worker 會碰視覺層與進度藥丸（都屬於主執行緒），
                // 所以只排隊，由既有的週期回呼（見 DrainPendingMerges）在主執行緒上接手。
                PendingMerges.Enqueue((ordered, pageSize));
                evidence.Line($"merge ui: the user confirmed the order " +
                              $"[{string.Join(",", ordered.Select(Path.GetFileName))}] pageSize={pageSize ?? "-"} " +
                              "-> queued for the " +
                              "main thread (the worker starts there)");
            }, evidence.Line);
            PendingPaths = Array.Empty<string>();
            PendingTools = Array.Empty<ToolDescriptor>();
            // **回 false**：同一條理由——此刻還沒有任何東西被轉檔。
            // 而且這裡比裁切那一顆更關鍵：合併的「取消」必須留下**零個新檔**，
            // 所以送出去的那一槍只能發生在使用者真的按了確認之後。
            return false;
        }

        guard.OnUserResolved("enter");      // 收起輪盤（收層是幂等的）

        // M3 第五顆扇形（讀 QR，2026-10-07 切片 2）：**它與另外四顆在結構上不同**——
        // 產物是一段文字而不是檔案，而且那一段文字要再走一條有副作用的判定
        // （http／https → 開瀏覽器；其餘 → 顯示在可複製的視窗裡）。
        //
        // 所以它：①照樣走 worker（`op=read-qr`，與另外四顆同一條路、同一份協議）；
        // ②不顯示進度藥丸（藥丸說的是「N 個檔案、成功幾個」，而這裡沒有檔案，
        //    見 ConversionRunner.Start）；③起完之後由**既有的**週期回呼抽乾
        //    （見 DrainQrResult），在那裡才做判定與副作用——那一刻已經在主執行緒上。
        if (toolOp == WorkerProtocol.OpReadQr)
        {
            if (runner == null)
            {
                evidence.Line("ENTER: no conversion runner in this mode (diagnostic path)");
                return true;
            }
            if (paths.Length == 0)
            {
                evidence.Line("ENTER: no payload paths recorded -> nothing to read");
                return true;
            }
            if (paths.Length > 1)
            {
                // 一次只讀一張：產物是「一個文字」，兩張圖會有兩個答案而畫面上只有一個視窗。
                // 明講並只做第一張，而不是靜默地挑一張（猜哪一張才是使用者要的，那是猜）。
                evidence.Line($"ENTER: read-qr takes one image at a time; using {Path.GetFileName(paths[0])} " +
                              $"of {paths.Length} (the rest are left alone)");
            }
            evidence.Line("ENTER: read-qr goes to the worker (op=read-qr); no file will be written");
            QrJobRunning = true;
            _qrSourceName = Path.GetFileName(paths[0]);
            runner.Start(new[] { paths[0] }, "", 100, centerX, centerY,
                         toolOp: WorkerProtocol.OpReadQr, outputDir: null);
            PendingPaths = Array.Empty<string>();
            PendingTools = Array.Empty<ToolDescriptor>();
            return true;
        }

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
        // 工具不問 QualityFor：旋轉固定用 100（見 ConversionEngine.Rotate90 的說明——
        // PNG／BMP／TIF 無損；JPEG 會重新壓縮，品質 100 是最接近無損的選擇）。
        // targetFormat 給空字串：工具自己決定輸出格式，格式欄位對它沒有意義。
        // 輸出資料夾**當場解析成 null／路徑**再往下傳（規格 §9.5）：worker 只認那個值，
        // 解析規則留在 Settings 這一處（worker 不是第二個擁有者）。
        string? outputDir = settings.ResolveOutputDirectory();
        evidence.Line($"ENTER: outputDir={outputDir ?? "(source folder)"}");
        // 壓縮（第三顆扇形）：等級政策由**設定頁**決定，這裡只把設定換成政策往下送
        // （政策的擁有者是 CompressionPolicy；輪盤上沒有任何等級可選——使用者 2026-10-06 裁定）。
        // 其餘 op 一律傳 null，封包因此與改動前逐位元相同。
        CompressionPolicy? compression = toolOp == WorkerProtocol.OpCompress
            ? settings.ToCompressionPolicy()
            : null;
        if (compression != null)
            evidence.Line($"ENTER: compress {compression.Describe()}");
        // 去背（第七顆扇形）：**它需要一個外部模型檔**，而「模型在哪裡」的解析只有一份規則
        // （RemoveBgModel.ResolvePath）。⚠ 正式路徑一律傳 **null**＝執行檔旁邊的 `models\`
        // （與取得腳本下載的位置相同）：`--remove-bg-model` 是**診斷接縫**，存在的理由是讓
        // 「缺模型」與「模型損壞」兩個狀態在驗收裡真的被走過，不是一個使用者設定。
        string? removeBgModel = null;
        // 邊緣精修（2026-10-08）：**正式路徑讀設定檔**（`RemoveBgRefine`），因為輪盤上沒有命令列，
        // 而使用者要能自己決定這一刀要不要開。與模型路徑同一條規矩：預設值送 null（缺席語意
        // ＝預設＝開），只有明確關掉才送 "off" 過去——那讓既有的 job 封包逐位元不變。
        string? removeBgRefine = null;
        if (toolOp == WorkerProtocol.OpRemoveBg)
        {
            string refine = EdgeRefine.CanonicalMode(settings.RemoveBgRefine);
            removeBgRefine = refine == EdgeRefine.DefaultMode ? null : refine;
            evidence.Line($"ENTER: remove-bg model resolves to " +
                          $"'{RemoveBgModel.ResolvePath(removeBgModel)}' (default beside the exe; " +
                          "PNG out, and no file at all when the mask is empty)");
            evidence.Line($"ENTER: remove-bg refine={refine} (settings; " +
                          $"r={EdgeRefine.RadiusGain:G}x the mask's upscale factor, cap " +
                          $"{EdgeRefine.RadiusMax}, eps={EdgeRefine.Epsilon:G}; " +
                          "the model runs once, the refinement is pixels only)");
        }
        runner.Start(paths,
                     toolOp == null ? target : "",
                     toolOp == null ? settings.QualityFor(target) : 100,
                     centerX, centerY,
                     toolOp: toolOp, outputDir: outputDir, compression: compression,
                     removeBgModel: removeBgModel, removeBgRefine: removeBgRefine);
        PendingPaths = Array.Empty<string>();
        PendingTools = Array.Empty<ToolDescriptor>();
        return true;
    }

    /// <summary>
    /// 起一個裁切工作（**必須在主執行緒**：它會碰視覺層與進度藥丸）。
    /// <paramref name="spec"/> 是**正規化**矩形——視窗拖出來的與 `--crop-rect` 給的是同一種東西。
    /// </summary>
    private static bool StartCrop(Evidence evidence, ConversionRunner runner, string[] paths,
                                  CropSpec spec, int centerX, int centerY, Settings settings)
    {
        if (paths.Length == 0)
        {
            evidence.Line("crop: no payload paths recorded -> nothing to crop");
            return false;
        }
        string? outputDir = settings.ResolveOutputDirectory();
        evidence.Line($"crop: start {spec.Describe()} files={paths.Length} " +
                      $"outputDir={outputDir ?? "(source folder)"}");
        // targetFormat 給空字串、quality 固定 100：與另外三個工具同一條規矩（工具自己決定輸出格式，
        // 裁切必須重新編碼，所以品質取最接近無損的那一個；理由見 ConversionEngine.Crop）。
        runner.Start(paths, "", 100, centerX, centerY,
                     toolOp: WorkerProtocol.OpCrop, outputDir: outputDir, crop: spec);
        return true;
    }

    /// <summary>
    /// 把「使用者在裁切視窗按下確認」的請求在主執行緒上接手（見 <see cref="PendingCrops"/>）。
    /// 由**既有**的視覺層週期回呼呼叫，與設定頁日誌的抽乾同一條路（<see cref="DrainSettingsUiLog"/>）——
    /// 不新增第二個計時器擁有者。
    /// </summary>
    private static void DrainPendingCrops(Evidence evidence, ConversionRunner? runner)
    {
        while (PendingCrops.TryDequeue(out (string[] Paths, CropSpec Spec) job))
        {
            if (runner == null)
            {
                evidence.Line("crop: confirmed, but this mode has no conversion runner (diagnostic path)");
                continue;
            }
            // 進度藥丸畫在**游標所在處**：使用者剛按下的就是裁切視窗的確認鈕，結果出現在同一個
            // 位置最不需要東張西望。取不到游標座標時退回主螢幕工作區的中心（絕不用 (0,0)——
            // 那會讓藥丸黏在左上角，看起來像壞掉）。
            (double anchorX, double anchorY) = CursorAnchor();
            StartCrop(evidence, runner, job.Paths, job.Spec, (int)anchorX, (int)anchorY,
                      _settings ?? new Settings());
        }
    }

    /// <summary>
    /// 起一個合併工作（**必須在主執行緒**：它會碰視覺層與進度藥丸）。
    /// <paramref name="ordered"/> 是**排好的順序**——`--merge-pdf` 給的與視窗拖出來的是同一種東西。
    ///
    /// 它與 <see cref="StartCrop"/> 的差別只有一個：合併是**多輸入、單輸出**，所以送出去的是
    /// **一組**路徑而不是「一組路徑 ＋ 一個參數」。順序就是頁序（見 ConversionEngine.MergePdf），
    /// 所以這裡**不可以**排序、去重或做任何「整理」——那會讓視窗上顯示的順序與輸出不一致。
    /// </summary>
    private static bool StartMerge(Evidence evidence, ConversionRunner runner, string[] ordered,
                                   int centerX, int centerY, Settings settings,
                                   string? pageSize = null)
    {
        if (ordered.Length == 0)
        {
            evidence.Line("merge: no payload paths recorded -> nothing to merge");
            return false;
        }
        string? outputDir = settings.ResolveOutputDirectory();
        evidence.Line($"merge: start files={ordered.Length} order=" +
                      $"[{string.Join(",", ordered.Select(Path.GetFileName))}] " +
                      $"outputDir={outputDir ?? "(source folder)"}");
        // targetFormat 給空字串、quality 固定 100：與其他工具同一條規矩（工具自己決定輸出格式；
        // 合併是**整頁物件搬移**，不重新編碼，所以品質這個參數在這一條路上不參與——見 ConversionEngine.MergePdf）。
        evidence.Line($"merge: pageSize={pageSize ?? "(none: PDFs are copied page for page)"}");
        runner.Start(ordered, "", 100, centerX, centerY,
                     toolOp: WorkerProtocol.OpMergePdf, outputDir: outputDir, pageSize: pageSize);
        return true;
    }

    /// <summary>
    /// 把「使用者在排序視窗按下確認」的請求在主執行緒上接手（見 <see cref="PendingMerges"/>）。
    /// 由**既有**的視覺層週期回呼呼叫（與 <see cref="DrainPendingCrops"/> 同一個 tick）——
    /// 不新增第二個計時器擁有者。
    /// </summary>
    private static void DrainPendingMerges(Evidence evidence, ConversionRunner? runner)
    {
        while (PendingMerges.TryDequeue(out (string[] Order, string? PageSize) pending))
        {
            string[] ordered = pending.Order;
            if (runner == null)
            {
                evidence.Line("merge: confirmed, but this mode has no conversion runner (diagnostic path)");
                continue;
            }
            // 進度藥丸畫在**游標所在處**：使用者剛按下的就是排序視窗的確認鈕，結果出現在同一個
            // 位置最不需要東張西望（與裁切同一條理由）。
            (double anchorX, double anchorY) = CursorAnchor();
            StartMerge(evidence, runner, ordered, (int)anchorX, (int)anchorY, _settings ?? new Settings(),
                       pending.PageSize);
        }
    }

    /// <summary>
    /// 進度藥丸的落點：游標位置，取不到就退回主螢幕工作區中心。**不注入任何輸入**（只讀）。
    /// </summary>
    private static (double X, double Y) CursorAnchor()
    {
        try
        {
            if (Native.GetCursorPos(out Native.POINT point))
                return (point.X, point.Y);
        }
        catch (Exception)
        {
            // 讀游標位置失敗不是錯誤，退回下面的預設值就好
        }
        // 主螢幕工作區的中心（**不是**整個虛擬桌面：多螢幕時那可能落在兩顆螢幕之間的縫上）。
        double fallbackX = System.Windows.SystemParameters.WorkArea.Left
                           + System.Windows.SystemParameters.WorkArea.Width / 2;
        double fallbackY = System.Windows.SystemParameters.WorkArea.Top
                           + System.Windows.SystemParameters.WorkArea.Height / 2;
        return (fallbackX, fallbackY);
    }

    /// <summary>最近一次拖曳／注入的檔案清單——Enter 要靠它才知道要轉哪些檔。</summary>
    private static string[] PendingPaths = Array.Empty<string>();

    /// <summary>
    /// 有一件讀 QR 的工作正在 worker 上跑（2026-10-07 切片 2）。
    ///
    /// 為什麼是一個旗標而不是「每次都去問 <see cref="ConversionRunner.Snapshot"/>」：
    /// 那一個快照屬於**上一次**工作（Idle 是「已完成」），拿它判斷「有沒有新結果」
    /// 會讓上一輪的結果被重複處理一次。這個旗標由提交處設起、由抽乾處在消費完之後放掉，
    /// 與 <see cref="PendingPaths"/> 同一種作法（顯示／提交時寫、消費後清）。
    /// </summary>
    private static volatile bool QrJobRunning;

    /// <summary>
    /// 把「讀 QR 的結果」在主執行緒上接手（見 <see cref="CommitWheel"/> 的 read-qr 那一條）。
    /// 由**既有**的視覺層週期回呼呼叫，與裁切、設定頁日誌抽乾同一條路——不新增第二個計時器。
    ///
    /// 為什麼判定與副作用都必須在這裡：①判定要開瀏覽器（在別條執行緒上叫 <c>Process.Start</c>
    /// 沒有理由，而且這一條路徑的其餘部分都已經在主執行緒）；②顯示文字要走
    /// <see cref="QrTextUi"/>，而那一個自己會把工作排到共用的 WPF 執行緒上。
    ///
    /// ⚠ worker 起不來時**一定要說出來**：那一條不顯示進度藥丸（見 ConversionRunner.Start），
    /// 所以這裡的日誌行是使用者唯一會看到的痕跡。
    /// </summary>
    private static void DrainQrResult(Evidence evidence, ConversionRunner? runner)
    {
        if (!QrJobRunning || runner == null) return;
        ConversionSnapshot snapshot = runner.Snapshot;
        if (!snapshot.Finished) return;

        QrJobRunning = false;
        string? text = snapshot.QrText;
        if (text == null)
        {
            // 兩種可能，都講清楚：worker 真的失敗（QrNotFound 之類），或 worker 根本沒起來。
            evidence.Line($"read-qr: no text came back (error='{snapshot.Error ?? "-"}' " +
                          $"failed={snapshot.Failed} line='{snapshot.Line}') -- nothing is shown and " +
                          "no browser is opened");
            return;
        }

        string sourceName = _qrSourceName.Length > 0 ? _qrSourceName : "?";
        var routed = QrTextRouter.Route(text, sourceName, evidence.Line);
        evidence.Line($"read-qr: routed kind={QrTextClassifier.KindLabel(routed.Kind)} " +
                      $"chars={text.Length} opened={routed.Opened} note='{routed.Note}'");
        _qrSourceName = "";
    }

    /// <summary>
    /// 這一輪讀 QR 的來源檔名（文字視窗的標題列要它）。與 <see cref="PendingPaths"/> 同一種作法：
    /// 提交時寫、抽乾時清。刻意不從快照反推——快照上的 <c>Current</c> 是進度訊息的一部分，
    /// 拿排版欄位當資料用正是本專案一直在防的那件事。
    /// </summary>
    private static string _qrSourceName = "";

    /// <summary>
    /// 顯示第二層時那一份工具清單（Enter 要靠它把「選中的索引」換成工具 id）。
    /// 與 <see cref="PendingPaths"/> 同一種作法、同一個生命週期：顯示時寫、提交後清。
    /// 不在 <see cref="CommitWheel"/> 裡重新推導——那裡拿不到分類用的目錄，
    /// 而且重新推導等於假設兩次分類結果一致（那就是第二個擁有者）。
    /// </summary>
    private static IReadOnlyList<ToolDescriptor> PendingTools = Array.Empty<ToolDescriptor>();

    /// <summary>
    /// 顯示輪盤當下的情境。第二層不是「顯示時算一次」就固定了——Shift 是**即時切層**
    /// （規格 §9.4，2026-10-05 裁定），所以切回來的時候要能重新畫出原本那一層，
    /// 而重畫需要當初的輸入：來源格式、格式目標、檔名預覽、拒絕訊息。
    /// 與 <see cref="PendingPaths"/> 同一種作法（顯示時寫、收起後不再用）。
    /// </summary>
    private static string _wheelSourceFormat = "?";
    private static IReadOnlyList<string> _wheelFormatTargets = Array.Empty<string>();
    private static Func<int, string?>? _wheelPreview;
    private static string? _wheelNotice;

    /// <summary>啟動時載入的設定（規格 §8）；null 代表還沒載入。</summary>
    private static Settings? _settings;

    /// <summary>
    /// 這個行程的命令列選項（<see cref="RunCore"/> 一開始就寫）。只有常駐路徑會走到需要它的地方
    /// ——目前是 <see cref="CommitWheel"/> 的裁切那一條（它要讀 <c>--crop-rect</c> 這個診斷接縫）。
    /// 與 <see cref="_settings"/> 同一種作法：進程層級只有一份，不在每個呼叫點重新解析 argv。
    /// </summary>
    private static Options _options = new();

    /// <summary>
    /// 裁切視窗確認之後、等著由**主執行緒**起 worker 的工作（見 <see cref="CommitWheel"/>）。
    ///
    /// 為什麼要過這一手：確認鈕是在 **WPF UI 執行緒**上被按下的，而 <see cref="ConversionRunner"/> 會
    /// 碰視覺層與進度藥丸——那些都是主執行緒的視窗。從別的執行緒去動它們只會隨機損壞。
    /// 抽乾點是**既有**的視覺層週期回呼（<see cref="DrainPendingCrops"/>），不新增第二個計時器。
    /// </summary>
    private static readonly ConcurrentQueue<(string[] Paths, CropSpec Spec)> PendingCrops = new();

    /// <summary>
    /// 排序視窗確認之後、等著由**主執行緒**起 worker 的工作（切片 B）。
    ///
    /// 與 <see cref="PendingCrops"/> **同一條理由、同一個形狀**，差別只在內容：裁切帶回的是一個
    /// 矩形（<see cref="CropSpec"/>），合併帶回的是一**組有序路徑**——因為對合併來說「順序」
    /// 就是那個參數。抽乾點同樣是**既有**的視覺層週期回呼（<see cref="DrainPendingMerges"/>），
    /// 不新增第二個計時器。
    ///
    /// ⚠ 這一條佇列是「取消不產出」那條契約的關鍵：使用者按取消時**沒有任何東西會被排進來**，
    ///   所以主執行緒永遠不會收到一槍，輸出目錄也就不可能有新檔（見 CommitWheel 的說明）。
    /// </summary>
    // 佇列裡帶的是「使用者對這一次合併的**兩個**決定」：順序與頁面大小。
    // 它們必須一起旅行——分開存會產生「順序是這一版、頁面大小是上一版」的錯配。
    private static readonly ConcurrentQueue<(string[] Order, string? PageSize)> PendingMerges = new();

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
    /// 常駐路徑抽乾 <see cref="_settingsUiLog"/> 的週期（毫秒）。
    ///
    /// 200 ms 的理由：設定頁的訊息是「使用者等級」的頻率（一次開啟只有個位數行），
    /// 人眼分不出 200 ms 與 20 ms 的差別，而每一個 tick 的成本只是一個 PostMessage ＋
    /// 一次空的 TryDequeue。刻意不比 200 ms 更短——這個回呼是常駐的，不是只在轉檔期間跑。
    /// </summary>
    private const int SettingsUiLogDrainIntervalMs = 200;

    /// <summary>
    /// 把設定頁那條執行緒排過來的日誌行寫進證據檔。**唯一的抽乾實作**，而且
    /// **只可以在主執行緒呼叫**：<see cref="Evidence.Line"/> 沒有上鎖，這就是那些行
    /// 要先排隊的理由（見 <see cref="_settingsUiLog"/>）。
    ///
    /// 兩個呼叫點都在主執行緒上：設定頁按下「儲存」時的 <c>ApplyFromSettingsUi</c>，
    /// 以及常駐路徑的 VisualLayer 週期回呼。少了後者，純托盤操作（開設定頁 → 關掉，
    /// 從不存檔）一整輪下來日誌裡不會有任何 `settings ui:` 的行。
    /// </summary>
    private static void DrainSettingsUiLog(Evidence evidence)
    {
        while (_settingsUiLog.TryDequeue(out string? uiLine)) evidence.Line(uiLine);
    }

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
            CaptureTimeoutSeconds = 45, Diagnostics = true, StartWithWindows = false, Language = "en",
            // 第十個欄位（規格 §9.5，2026-10-05）：桌面這一條分支過去沒有任何自動驗證，
            // 所以由設定頁這條**真的按下儲存**的路徑量它——值進 UI 控制項、再由控制項回到檔案。
            OutputFolder = "desktop"
        };
        bool saved = SettingsUi.SelfTestSave(good, 5000, out string saveDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: save via the real Save button ok={saved} detail={saveDetail}");

        Settings bad = settings with { TriggerKey = "NotARealKey" };
        bool invalidSaveAccepted = SettingsUi.SelfTestSave(bad, 5000, out string badDetail, lines.Enqueue);
        // 命名刻意是「save accepted」而不是「refused」：SelfTestSave 回傳 false 正是**拒絕成功**，
        // 舊名字（refused）讀起來剛好相反（refused=False 看起來像「沒有拒絕」）。
        // 這裡只改命名與輸出文字，判定邏輯一行都沒動。
        evidence.Line($"settings ui selftest: an invalid value is refused (save ok={invalidSaveAccepted}) detail={badDetail}");

        // 新的欄位也要能被拒絕：相對路徑的意思是「看誰在跑、從哪裡跑」，而檔案落在哪裡不該取決於那種事。
        Settings badFolder = settings with { OutputFolder = "relative-output" };
        bool relativeFolderSaveAccepted = SettingsUi.SelfTestSave(badFolder, 5000, out string badFolderDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: a relative output folder is refused " +
                      $"(save ok={relativeFolderSaveAccepted}) detail={badFolderDetail}");

        // M3 第三顆扇形（2026-10-06）：壓縮方式。這一格是**兩個值**（模式＋上限），而且它有一條
        // 「按下去才會發現」的失效模式：選了「壓縮到」卻沒有可用的上限。所以各走一次真的按下儲存。
        //
        // ⚠ 這一格還背著一條更貴的規矩：**設定頁不准丟例外**（這個專案才剛為此失去過 UI 執行緒）。
        //   下面那個被擋下來的例子若丟了例外，SelfTestSave 會回 false 並在 detail 裡留下痕跡，
        //   但真正把它變成紅燈的是下面 ok 運算式裡的 !unusableSizeAccepted。
        // ⚠ 這兩個 case 的底必須是 good，不是 settings。SelfTestSave 是按真的「儲存」，而設定頁
        //   的 BuildFromUi 會把**所有**欄位一起送出去；若拿預設值當底，這個會被接受的儲存就會把
        //   前面 good 寫進去的那一整組值（71/Tab/45/en）蓋掉，其他套件（m1f S14.30）事後讀到
        //   的就是 85/Space——這是回歸，不是細節。
        Settings goodSize = good with
        {
            CompressionMode = CompressionPolicy.ModeTargetSize,
            CompressionTargetKb = 512
        };
        bool sizeSaved = SettingsUi.SelfTestSave(goodSize, 5000, out string sizeDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: the compression fields round-trip through the real Save button " +
                      $"(mode={goodSize.CompressionMode} targetKb={goodSize.CompressionTargetKb}) " +
                      $"ok={sizeSaved} detail={sizeDetail}");

        // 0 KB 在設定檔裡的意思是「沒有上限」，所以「壓縮到 ＋ 0」是一個自相矛盾的組合：
        // 載入時會被夾成 0（在畫面上就是一個空的文字框），存檔時必須被擋下來並指名道姓。
        Settings badSize = good with
        {
            CompressionMode = CompressionPolicy.ModeTargetSize,
            CompressionTargetKb = 0
        };
        bool unusableSizeAccepted = SettingsUi.SelfTestSave(badSize, 5000, out string badSizeDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: an unusable compression limit is refused " +
                      $"(save ok={unusableSizeAccepted}) detail={badSizeDetail}");
        // 而且畫面上要**看得見**那一行提示（見 SettingsUi.SelfTestNoticeTexts 的理由）。
        bool noticesRead = SettingsUi.SelfTestNoticeTexts(5000, out string noticeDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: the inline notice under the compression row " +
                      $"(read={noticesRead}) {noticeDetail}");
        // 這一顆是「有提示」的斷言：模式＝壓縮到、上限是空的，行內提示不可以是空的，
        // 而且那一格必須是**可以打字**的（提示說「請填」但格子是停用的，等於沒有出路）。
        bool noticeShown = noticesRead
                           && noticeDetail.Contains("compressNotice='", StringComparison.Ordinal)
                           && !noticeDetail.Contains("compressNotice=''", StringComparison.Ordinal)
                           && noticeDetail.Contains("limitBoxEnabled=True", StringComparison.Ordinal);

        Thread.Sleep(700);      // 讓 WPF 真的完成第一次繪製，而不是只把物件建起來就說「開好了」

        // 關窗走**使用者自己的**那條路：按下取消鈕（＝ SettingsWindow 自己的 Close()，
        // 右上角的 X 也是同一條）。產品的 SettingsUi.Close() 是另一條路徑，兩條都要能活——
        // 使用者的 bug 正是從「視窗自己關掉」開始的。
        bool cancelClicked = SettingsUi.SelfTestCancel(5000, out string cancelDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: closed via the window's own cancel button " +
                      $"clicked={cancelClicked} detail={cancelDetail}");
        bool closed = WaitFor(() => !SettingsUi.IsOpen, 3000);
        evidence.Line($"settings ui selftest: closed={closed} isOpen={SettingsUi.IsOpen}");

        // 「關掉之後**再開一次**」。這是使用者回報的那條路（托盤的命令確實送到了、日誌裡也沒有
        // 「already open」，但視窗再也沒出現），而原本的自測只驗到「關得掉」就結束，
        // 完全沒有覆蓋這一格。
        SettingsUi.Open(good, path, _ => lines.Enqueue("settings ui selftest: REOPEN onSaved fired"), lines.Enqueue);
        bool reopened = WaitFor(() => SettingsUi.IsOpen, 5000);
        // 而且不只問 IsOpen：一個「視窗關了、Closed 沒送到、_isOpen 卡在 true」的 bug 會讓
        // IsOpen 停在 true（於是「再開」被拒、畫面根本沒出現），只讀 IsOpen 的斷言照樣是綠的。
        string reopenDetail = "(the window was never asked: the page never reported itself as open)";
        bool windowPresent = reopened && SettingsUi.SelfTestWindowPresent(5000, out reopenDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: reopened={reopened} windowPresent={windowPresent} " +
                      $"isOpen={SettingsUi.IsOpen} detail={reopenDetail}");

        // ============ 使用者回報的兩件事，各一顆斷言（2026-10-06 加的）============
        //
        // 症狀 1：「選『輸出位置』後，要按輸入框輸入時設定面版消失」。
        //   量到的機制：輸入框**第一次**拿到鍵盤焦點時 WPF 要建 caret
        //   （TextSelection.EnsureCaret → InputLanguageSource.CurrentInputLanguage → CultureInfo(0x0404)），
        //   在 InvariantGlobalization=true 之下那裡丟 CultureNotFoundException；例外穿過 ShowDialog 的
        //   modal frame，開窗的那個 catch 於是放掉 _window——視窗物件沒了（下一次開啟只會多疊一張）。
        //   所以這裡問的是「那個視窗物件還在不在」，而不是只問 IsOpen：舊斷言只問 IsOpen，
        //   這顆 bug 全綠。走 modal frame 而不是由勾子直接呼叫，是因為例外必須**穿過 modal frame**
        //   才是同一個故障。
        var userTrace = new List<string>();
        SettingsUi.SelfTestPlayUserStepsInsideTheModalFrame("C:\\Confern-selftest\\輸出", userTrace);
        WaitFor(() => userTrace.Count >= 1, 4000);
        Thread.Sleep(600);
        string stillThereDetail = "(the window was never asked)";
        bool stillThere = SettingsUi.SelfTestWindowPresent(5000, out stillThereDetail, lines.Enqueue);
        evidence.Line($"settings ui selftest: typing in the output box does not take the page down " +
                      $"(symptom 1) stillThere={stillThere} steps={userTrace.Count} " +
                      $"stepsDone=[{string.Join(" | ", userTrace)}] detail={stillThereDetail}");

        SettingsUi.Close();     // 收尾：把視窗關掉（這裡不驗這條路徑，它上面已經被走過了）
        WaitFor(() => !SettingsUi.IsOpen, 3000);

        // 症狀 2：「之後再從托盤按『開啟設定』，還是叫不出來」。
        //   使用者的日誌就是這個形狀：第二次只有 `open requested`，沒有 `already open`、沒有
        //   `STA thread started`，之後全無動靜 ⇒ UI 執行緒已經結束，而 `if (_thread != null) return;`
        //   讓後來的每一次開啟都只把工作排進一個沒有抽乾者的佇列。
        //   這裡不必重現「他遇到的那個例外」（那個例外只是把執行緒帶走），要驗的是
        //   「致命例外之後，托盤還開不開得起來」這一格本身。故意丟一個穿出 Dispatcher.Run() 的例外。
        bool killPosted = SettingsUi.SelfTestKillUiThread(3000, out string killDetail, lines.Enqueue);
        bool fatalDelivered = WaitFor(() => SettingsUi.SelfTestUiThreadExceptionText()
            .Contains("deliberate fatal exception", StringComparison.Ordinal), 8000);
        // 驗的是「之後還開不開得起來」，不是「執行緒有沒有死」——修好之後它本來就該活著
        // （例外穿出去、同一條執行緒繼續跑）；所以問的是「有沒有一條能用的 UI 執行緒」。
        bool threadUsable = fatalDelivered && SettingsUi.SelfTestUiThreadAlive();
        evidence.Line($"settings ui selftest: a fatal exception was thrown on the UI thread (symptom 2) " +
                      $"posted={killPosted} delivered={fatalDelivered} threadUsable={threadUsable} " +
                      $"state={SettingsUi.SelfTestUiThreadState()}");
        evidence.Line($"settings ui selftest: what happened: {SettingsUi.SelfTestUiThreadExceptionText()}");

        SettingsUi.Open(good, path, _ => lines.Enqueue("settings ui selftest: REOPEN AFTER FATAL onSaved fired"),
                        lines.Enqueue);
        bool reopenedAfterFatal = WaitFor(() => SettingsUi.IsOpen, 8000);
        string afterFatalDetail = "(the window was never asked after the fatal exception)";
        // 這裡要**等**：WPF 的 Loaded 事件排在 DispatcherPriority.Loaded，比 Normal 低，
        // 不是問一次就會有答案。
        bool windowBack = false;
        if (reopenedAfterFatal)
        {
            for (int attempt = 0; attempt < 20 && !windowBack; attempt++)
            {
                windowBack = SettingsUi.SelfTestWindowPresent(3000, out afterFatalDetail, lines.Enqueue);
                if (!windowBack) Thread.Sleep(250);
            }
        }
        evidence.Line($"settings ui selftest: the tray can open the page again after a fatal exception " +
                      $"(symptom 2) reopened={reopenedAfterFatal} windowPresent={windowBack} " +
                      $"state={SettingsUi.SelfTestUiThreadState()} detail={afterFatalDetail}");

        SettingsUi.Close();     // 收尾：把視窗關掉（這裡不驗這條路徑，它上面已經被走過了）
        WaitFor(() => !SettingsUi.IsOpen, 3000);

        while (lines.TryDequeue(out string? line)) evidence.Line(line);

        bool ok = opened && singleInstance && closed && saved && !invalidSaveAccepted &&
                  !relativeFolderSaveAccepted && sizeSaved && !unusableSizeAccepted && noticeShown &&
                  reopened && windowPresent &&
                  stillThere && userTrace.Count >= 3 &&
                  killPosted && fatalDelivered && threadUsable && reopenedAfterFatal && windowBack;
        evidence.Line($"settings ui selftest done: ok={ok} (opened={opened} singleInstance={singleInstance} " +
                      $"closed={closed} saved={saved} invalidSaveAccepted={invalidSaveAccepted} " +
                      $"relativeOutputFolderSaveAccepted={relativeFolderSaveAccepted} " +
                      $"compressionFieldsSaved={sizeSaved} unusableCompressionLimitAccepted={unusableSizeAccepted} " +
                      $"compressionNoticeShown={noticeShown} " +
                      $"reopened={reopened} windowPresent={windowPresent} " +
                      $"reopened={reopened} windowPresent={windowPresent} " +
                      $"symptom1StillThere={stillThere} userStepsDone={userTrace.Count} " +
                      $"symptom2FatalDelivered={fatalDelivered} symptom2ThreadUsable={threadUsable} " +
                      $"symptom2Reopened={reopenedAfterFatal} symptom2WindowBack={windowBack})");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// M3：`--tools-list`——工具層的扇區清單（**唯一權威**是 <see cref="ToolCatalog"/>）。
    ///
    /// 它與 `--wheel-payload --wheel-tools` 不是兩種實作，而是**同一個清單的無視窗查詢**：
    /// 兩者都呼叫 <c>ToolCatalog.ToolsFor</c>。存在的理由是可及性——這一條在單一實例守衛**之前**，
    /// 所以「使用者自己開著 Confern」的時候也問得到；而「工具層有哪幾顆」正是這條產品線上最容易
    /// 被改動、也最該被釘住的一件事（多一顆＝改協議、改輪盤、改介面、改這條清單）。
    ///
    /// 回傳碼：0 有清單、3 清單是空的（那代表這個組合下輪盤的工具層什麼都不會顯示）。
    /// </summary>
    /// <summary>
    /// M3：`--tools-list`——把工具層的扇區清單印出來就結束（不必視窗、不必滑鼠、不必搶單一實例）。
    ///
    /// ⚠ **有 `--wheel-payload` 時走的是真正的分類路徑**（`FormatCatalog.Classify`），不是
    /// 「自己塞一個 Ready 進去」。這個差別在 2026-10-07 量到過：`--tools-list --wheel-source pdf`
    /// 回 6 顆工具（含 merge-pdf），但同一批 PDF 走**執行期**的 `--wheel-payload` 是
    /// <code>
    /// wheel from OLE payload: files=3 status=Unsupported source=UNKNOWN targets=0 notice=不支援的格式 tools=0
    /// </code>
    /// ——兩者不一致，而**只有後者**是使用者真的會遇到的。這一條舊行為本身沒有錯
    /// （它誠實地回答「目錄過濾查詢」），但它**單獨存在時會讓人以為執行期路徑已經被覆蓋**。
    /// 所以現在：給了酬載就用酬載（等於執行期），只給 `--wheel-source` 才是目錄查詢。
    /// </summary>
    internal static int RunToolsList(Options opt, Evidence evidence)
    {
        PayloadVerdict verdict;
        string how;
        var catalog = new FormatCatalog(new CodecRegistry());
        if (!string.IsNullOrEmpty(opt.WheelPayload))
        {
            string[] paths = opt.WheelPayload.Split(';', StringSplitOptions.RemoveEmptyEntries);
            verdict = catalog.Classify(paths);
            how = $"runtime classification of {paths.Length} payload file(s) (FormatCatalog.Classify)";
        }
        else
        {
            verdict = new PayloadVerdict(PayloadStatus.Ready,
                                         string.IsNullOrEmpty(opt.WheelSource) ? "png" : opt.WheelSource,
                                         1, CanConvert: true);
            how = "catalog query for --wheel-source (no payload: NOT the runtime path)";
        }
        IReadOnlyList<ToolDescriptor> tools = new ToolCatalog().ToolsFor(verdict);
        // status= 與 notice= 一起印：只印清單的話，看不出「這一批到底是被拒絕還是被接受」。
        string? notice = verdict.Status switch
        {
            PayloadStatus.Mixed => "混合選取，請選同類別",
            PayloadStatus.Unsupported => "不支援的格式",
            _ => null
        };
        evidence.Line($"tool list: count={tools.Count} tools=[{string.Join(",", tools.Select(t => t.Id))}] " +
                      $"(source={verdict.SourceFormat ?? "UNKNOWN"}, status={verdict.Status}, " +
                      $"notice={notice ?? "-"}, {how}, source of truth: ToolCatalog)");
        for (int i = 0; i < tools.Count; i++)
        {
            ToolDescriptor t = tools[i];
            evidence.Line($"tool list: {i} id={t.Id} label='{t.Label}' desc='{t.Description}' icon={t.Icon}");
        }
        if (tools.Count == 0)
        {
            evidence.Line("tool list: FAILED the tool layer would be empty (the wheel would show nothing)");
            return 3;
        }
        evidence.Line("tool list done: ok=True");
        return 0;
    }

    /// <summary>
    /// M3：`--crop-ui-selftest` / `--crop-ui-dump &lt;png&gt;`——裁切視窗的生命週期與互動自測。
    ///
    /// 這一條是裁切功能**唯一**能在「不碰使用者滑鼠」的前提下被驗到的入口（2026-10-06 的規則：
    /// 預設跑套件絕不注入合成輸入）。所以它必須把使用者真的會做的每一件事都走一遍，而且走的
    /// **都是真物件**：真的視窗、真的按鈕（RaiseEvent 打 Click）、滑鼠事件呼叫的那三個拖曳方法。
    ///
    /// 依序驗（每一格的成敗都印出來，不只印總結）：
    ///   ① 開得起來、而且**真的在螢幕上**（IsLoaded ＋ hwnd，不只是「有一個物件」）；
    ///   ② 設定頁與裁切視窗**可以同時開著**（兩者共用同一條 WPF 執行緒與同一個 Application，
    ///      這正是「一個 AppDomain 只准有一個 Application」那個坑的實測）；
    ///   ③ 程式化拖曳（＝滑鼠會走的那三個方法）真的改變了矩形，而且逐格被記錄下來；
    ///   ④ 每一個比例鈕都按得到、按下去之後矩形真的符合那個比例（**含容差**，見下面的數字）；
    ///   ⑤ 退化輸入（空的矩形、整張圖）**當場被拒絕並顯示提示**，不會帶著壞矩形去寫檔；
    ///   ⑥ 確認 → 帶回正規化矩形、視窗真的關掉；
    ///   ⑦ 關掉之後**可以再開**（單一實例的反面）；
    ///   ⑧ 取消 → 不帶回任何矩形；
    ///   ⑨ `--crop-ui-dump`：離屏合成一張 PNG（不是螢幕擷取），而且視窗自己會拒絕交出空白圖。
    ///
    /// 回傳碼：0 全過、1 有任何一格沒過。
    /// </summary>
    internal static int RunCropUiSelfTest(Options opt, Evidence evidence)
    {
        var lines = new ConcurrentQueue<string>();
        bool dumpOnly = !opt.CropUiSelfTest && !string.IsNullOrEmpty(opt.CropUiDump);

        string? source = opt.CropUiSource;
        if (string.IsNullOrEmpty(source) || !File.Exists(LongPath.Prepare(source)))
        {
            evidence.Line($"crop ui selftest: FAILED to open: no usable --crop-source " +
                          $"(got '{source ?? "(none)"}'; it must be an existing image file)");
            return 1;
        }
        string[] batch = { source };

        // `--crop-ui-dump` 自己先開一次（只為了畫圖），不跑整條自測。
        if (dumpOnly)
        {
            CropUi.Open(batch, _ => { }, lines.Enqueue);
            bool up = WaitFor(() => CropUi.IsOpen, 8000);
            if (!up)
            {
                while (lines.TryDequeue(out string? early)) evidence.Line(early);
                evidence.Line("crop ui dump: FAILED to open the window");
                return 1;
            }
            Thread.Sleep(500);      // 沒畫完就截會得到半張圖，所以先讓它畫完
            if (!string.IsNullOrEmpty(opt.CropRect))
            {
                if (CropSpec.TryParse(opt.CropRect, out CropSpec dumpSpec, out string dumpParse))
                {
                    CropUi.SelfTestSetRect(dumpSpec.X, dumpSpec.Y, dumpSpec.Width, dumpSpec.Height,
                                           5000, out string setDetail, lines.Enqueue);
                    evidence.Line($"crop ui dump: {setDetail}");
                }
                else
                {
                    evidence.Line($"crop ui dump: --crop-rect was ignored ({dumpParse})");
                }
            }
            bool dumped = CropUi.DumpFrame(opt.CropUiDump, 8000, out string dumpDetail, lines.Enqueue);
            CropUi.Close();
            WaitFor(() => !CropUi.IsOpen, 3000);
            while (lines.TryDequeue(out string? line)) evidence.Line(line);
            evidence.Line($"crop ui dump: ok={dumped} detail={dumpDetail} path={opt.CropUiDump}");
            return dumped ? 0 : 1;
        }

        // ① 開得起來，而且真的在螢幕上
        CropSpec? confirmed = null;
        CropUi.Open(batch, spec => confirmed = spec, lines.Enqueue);
        bool opened = WaitFor(() => CropUi.IsOpen, 8000);
        bool windowPresent = false;
        string presentDetail = "(never asked)";
        for (int attempt = 0; attempt < 20 && !windowPresent; attempt++)
        {
            windowPresent = CropUi.SelfTestWindowPresent(3000, out presentDetail, lines.Enqueue);
            if (!windowPresent) Thread.Sleep(250);
        }
        evidence.Line($"crop ui selftest: opened={opened} windowPresent={windowPresent} " +
                      $"state={CropUi.SelfTestState()}");
        evidence.Line($"crop ui selftest: window detail: {presentDetail}");
        if (!opened || !windowPresent)
        {
            while (lines.TryDequeue(out string? early)) evidence.Line(early);
            evidence.Line($"crop ui selftest done: ok=False (opened={opened} windowPresent={windowPresent})");
            return 1;
        }

        // ② 已經開著時**拒絕**第二次開啟（單一實例）
        CropUi.Open(batch, _ => confirmed = CropSpec.Free(0, 0, 0, 0), lines.Enqueue);
        bool singleInstance = CropUi.IsOpen;
        evidence.Line($"crop ui selftest: a second open while the window is up was " +
                      $"{(singleInstance ? "ignored (single instance)" : "ACCEPTED (two windows!)")}");

        // ③-0 在**剛開好的視窗**上拖曳（矩形＝整張圖）。這是使用者開窗後第一個會做的動作，
        // 而它原本是**壞的**：矩形＝整張圖時 BeginDrag 命中 Move，而 Move 是可證明的 no-op
        // （ContinueDrag 的分支與收尾的 Constrain 都把位置夾在 [0, 影像寬 − 矩形寬]，上界＝0），
        // 所以「在照片上拖曳」什麼都不會發生。實測原始輸出（修正前，512x384／3000x2250／3000x4000
        // 三張圖一致）：
        //   begin from=(429.7,260) mode=Move note='' rect=0,0,512,384 | move to=(603,390) rect=0,0,512,384
        //   | end rect=0,0,512,384
        // 修正後在矩形**內**起手會改成 New（見 CropWindow.BeginDrag 的論證），而這些斷言釘住它。
        //
        // ⚠ 期望值一律由**視窗自己**推導，這一格裡沒有一個夾具數字：
        //   · 起點／終點的影像座標＝CropUi.SelfTestStageToImage（走真正的 ToImage）；
        //   · 畫面座標＝SelfTestStageMapping 的 scale／imageLeft／imageTop；
        //   · 「錨點」與「對角」是由**產品回報的模式與矩形**（DragRun）反推的，
        //     自測不自己算一次 min/max 那一套（那是第二份規則）。
        // 只斷言「接縫跑過了」正是這個專案最怕的假綠燈：那一格在修正前也是綠的。
        bool NewDragProducesExpectedBox(int fromImageX, int fromImageY, int toImageX, int toImageY,
                                        out CropWindow.DragRun nRun, out string nDetail)
        {
            nRun = default;
            nDetail = "(not run)";
            (double sc, double lf, double tp) = CropUi.SelfTestStageMapping();
            double fx = lf + fromImageX * sc, fy = tp + fromImageY * sc;
            double tx2 = lf + toImageX * sc, ty2 = tp + toImageY * sc;
            if (!CropUi.SelfTestDrag(fx, fy, tx2, ty2, 5000, out nRun, out string trace, lines.Enqueue))
                return false;
            (double axD, double ayD) = CropUi.SelfTestStageToImage(fx, fy);
            (double bxD, double byD) = CropUi.SelfTestStageToImage(tx2, ty2);
            int ax = (int)Math.Round(axD, MidpointRounding.AwayFromZero);
            int ay = (int)Math.Round(ayD, MidpointRounding.AwayFromZero);
            int bx = (int)Math.Round(bxD, MidpointRounding.AwayFromZero);
            int by = (int)Math.Round(byD, MidpointRounding.AwayFromZero);
            // 「錨點」與「對角」只能從**產品回報的矩形**反推：兩端點各自是矩形的一個角，
            // 起點是錨點，另一個角是終點。四個角都試，命中哪一個就代表產品選了哪一種展開方式。
            System.Windows.Int32Rect r = nRun.AfterEnd;
            (int cx, int cy) = r.X == ax && r.Y == ay ? (r.X + r.Width, r.Y + r.Height)
                            : r.X + r.Width == ax && r.Y == ay ? (r.X, r.Y + r.Height)
                            : r.X == ax && r.Y + r.Height == ay ? (r.X + r.Width, r.Y)
                            : (r.X, r.Y);
            bool anchorIsStart = r.X == ax || r.X + r.Width == ax;
            bool anchorIsStartY = r.Y == ay || r.Y + r.Height == ay;
            bool oppositeIsEnd = cx == bx && cy == by;
            bool differs = r.X != nRun.Before.X || r.Y != nRun.Before.Y ||
                           r.Width != nRun.Before.Width || r.Height != nRun.Before.Height;
            (int iw, int ih) = CropUi.SelfTestImageSizePx();
            bool notFullFrame = !(r.Width >= iw && r.Height >= ih);
            bool ok = nRun.Mode == "New" && differs && notFullFrame && r.Width > 0 && r.Height > 0 &&
                      anchorIsStart && anchorIsStartY && oppositeIsEnd;
            nDetail = $"pixels ({fromImageX},{fromImageY})->({toImageX},{toImageY}) " +
                      $"stage ({fx:0.##},{fy:0.##})->({tx2:0.##},{ty2:0.##}) via scale={sc:0.######} " +
                      $"imageLeft={lf} imageTop={tp} startPixel={ax},{ay} endPixel={bx},{by} " +
                      $"before={Px(nRun.Before)} mid={Px(nRun.AfterMove)} end={Px(r)} " +
                      $"anchorIsStart={anchorIsStart && anchorIsStartY} oppositeIsEnd={oppositeIsEnd} " +
                      $"differs={differs} notFullFrame={notFullFrame} ok={ok} [{trace}]";
            return ok;
        }

        // ②b 設定頁與裁切視窗共用同一條 WPF 執行緒：兩個視窗同時開著互不干擾。
        string settingsPath = string.IsNullOrEmpty(opt.SettingsFile)
            ? Path.Combine(Path.GetTempPath(), ProductInfo.Slug + "-crop-selftest.json")
            : opt.SettingsFile;
        Settings settings = SettingsStore.Load(File.Exists(settingsPath) ? settingsPath : null, lines.Enqueue);
        SettingsUi.Open(settings, settingsPath, _ => { }, lines.Enqueue);
        bool settingsOpen = WaitFor(() => SettingsUi.IsOpen, 8000);
        Thread.Sleep(300);
        bool cropStillThere = CropUi.SelfTestWindowPresent(3000, out string coexistDetail, lines.Enqueue);
        evidence.Line($"crop ui selftest: the settings page and the crop window share one WPF thread: " +
                      $"settingsOpen={settingsOpen} cropWindowStillPresent={cropStillThere} " +
                      $"state={CropUi.SelfTestState()}");
        SettingsUi.Close();
        WaitFor(() => !SettingsUi.IsOpen, 3000);

        // ③ 程式化拖曳（滑鼠事件呼叫的就是這三個方法）。
        //
        // ⚠ 這一格原本是**假的綠燈**，而拖曳正是裁切的核心互動。自測跑到這裡時矩形還是**整張圖**
        // （例如 3000x2250），BeginDrag 因此命中 mode=Move，而 ContinueDrag 的位移被
        // Math.Clamp(r.X + dx, 0, max(0, W - r.Width)) 夾成 0——矩形根本沒有移動：
        //   begin from=(200,150) mode=Move note='' rect=0,0,3000,2250 |
        //   move to=(520,380) rect=0,0,3000,2250 | end rect=0,0,3000,2250
        // 而舊的 dragged 只斷言「這條接縫跑過了」。512x384 的小圖同樣不動（0,0,512,384 一直是
        // 0,0,512,384），所以那一格在大小圖上都是綠的，卻什麼都沒證明。
        //
        // 修法照 ④ 比例鈕那一格的紀律（見下面那一段的說明）：**先給它一個已知、而且不貼齊夾邊界
        // 的矩形**，再拖。否則會出現「矩形本來就是整張圖，所以拖不拖都對」那種假綠燈。
        //
        // 座標一律用**影像像素**表達（拖曳的數學就寫在像素上，見 CropWindow 的說明），換算成
        // 畫面座標用的是**視窗自己報的**縮放與偏移（CropUi.SelfTestStageMapping），不是自測
        // 自己再算一次 min(860/W, 520/H)——那是第二份規則，會紅在測試的算式上。
        //
        // 期望值可以**精確**比對（容差 0 px）：位移 dx = round(image_to.x − image_from.x)，
        // 而兩端都是由同一組 (_scale, _imageLeft) 從整數像素反算出來的（浮點誤差 ~1e-13），
        // 所以取整不會有半個像素的不確定性。這與比例那一格用 1 % 容差是不同的理由
        // （那裡的最後一步是邊界取整），不是隨便挑的數字。
        CropUi.SelfTestSelectRatio(CropRatios.DefaultId, 5000, out _, lines.Enqueue);   // 前置條件：自由框
        (int imageWpx, int imageHpx) = CropUi.SelfTestImageSizePx();

        // ③-0 執行那三格（宣告放在 ②b 之前：它用不到後面才宣告的 SetBaseline／DragPixels／Px，
        // 而且它必須在「開窗之後、任何東西動過矩形之前」——見上面那一段的理由）。
        bool seedBase = CropUi.SelfTestSetRect(0, 0, 1, 1, 5000, out _, lines.Enqueue);
        bool seedFull = CropUi.SelfTestRectPx() is var seedRect &&
                        seedRect.Width >= imageWpx && seedRect.Height >= imageHpx;
        evidence.Line($"crop ui selftest: fresh-window seed rect={CropUi.SelfTestRectPx()} " +
                      $"image={imageWpx}x{imageHpx} fullFrame={seedFull} set={seedBase}");

        // ③-0a 從影像中心附近往**右下**拖：必須是新框，錨點在起點，往右下撐開。
        int nbFromX = Math.Max(1, imageWpx / 6), nbFromY = Math.Max(1, imageHpx / 6);
        int nbToX = Math.Min(imageWpx - 1, imageWpx / 2), nbToY = Math.Min(imageHpx - 1, imageHpx / 2);
        bool newDownOk = seedFull && seedBase;
        CropWindow.DragRun newDown = default;
        string newDownDetail = "(not run)";
        if (newDownOk)
            newDownOk = NewDragProducesExpectedBox(nbFromX, nbFromY, nbToX, nbToY,
                                                   out newDown, out newDownDetail);
        evidence.Line($"crop ui selftest: NEW box drag from inside the photo towards the bottom-right: " +
                      $"mode={newDown.Mode} before={newDown.Before.X},{newDown.Before.Y}," +
                      $"{newDown.Before.Width},{newDown.Before.Height} afterBegin={newDown.AfterBegin.X}," +
                      $"{newDown.AfterBegin.Y},{newDown.AfterBegin.Width},{newDown.AfterBegin.Height} " +
                      $"afterEnd={newDown.AfterEnd.X},{newDown.AfterEnd.Y},{newDown.AfterEnd.Width}," +
                      $"{newDown.AfterEnd.Height} ok={newDownOk} ({newDownDetail})");

        // ③-0b 反方向（終點在起點的**左上方**）：方向不敏感——錨點仍然在起點，往反方向撐開。
        bool seedBase2 = CropUi.SelfTestSetRect(0, 0, 1, 1, 5000, out _, lines.Enqueue);
        CropWindow.DragRun newUp = default;
        string newUpDetail = "(not run)";
        bool newUpOk = seedBase2 &&
                       NewDragProducesExpectedBox(nbToX, nbToY, nbFromX, nbFromY,
                                                  out newUp, out newUpDetail);
        evidence.Line($"crop ui selftest: NEW box drag from inside the photo towards the top-left (reverse " +
                      $"direction): mode={newUp.Mode} before={newUp.Before.X},{newUp.Before.Y}," +
                      $"{newUp.Before.Width},{newUp.Before.Height} afterBegin={newUp.AfterBegin.X}," +
                      $"{newUp.AfterBegin.Y},{newUp.AfterBegin.Width},{newUp.AfterBegin.Height} " +
                      $"afterEnd={newUp.AfterEnd.X},{newUp.AfterEnd.Y},{newUp.AfterEnd.Width}," +
                      $"{newUp.AfterEnd.Height} ok={newUpOk} ({newUpDetail})");

        // ③-0c 從照片**旁邊的空白區**拖：仍然碰得到新框（這是修正前唯一碰得到 New 的地方，
        // 也是「不拿走既有行為」的那一格）。期望的**大小**同樣由視窗自己推導：起點在影像外面
        // （ToImage 會夾成 0），終點是一個真正的影像像素。
        bool newMarginOk = false;
        string newMarginDetail = "(no blank margin; the photo fills the stage)";
        (double mScale, double mLeft, double mTop) = CropUi.SelfTestStageMapping();
        if (mLeft >= 3 && CropUi.SelfTestSetRect(0, 0, 1, 1, 5000, out _, lines.Enqueue))
        {
            double mFromX = mLeft / 2.0, mFromY = mTop + imageHpx / 2.0 * mScale;
            double mToX = mLeft + Math.Max(2, imageWpx / 20) * mScale;
            double mToY = mTop + (imageHpx / 2 + Math.Max(2, imageHpx / 20)) * mScale;
            (double mAxD, double mAyD) = CropUi.SelfTestStageToImage(mFromX, mFromY);
            (double mBxD, double mByD) = CropUi.SelfTestStageToImage(mToX, mToY);
            int mAx = (int)Math.Round(mAxD, MidpointRounding.AwayFromZero);
            int mAy = (int)Math.Round(mAyD, MidpointRounding.AwayFromZero);
            int mBx = (int)Math.Round(mBxD, MidpointRounding.AwayFromZero);
            int mBy = (int)Math.Round(mByD, MidpointRounding.AwayFromZero);
            bool mRan = CropUi.SelfTestDrag(mFromX, mFromY, mToX, mToY, 5000,
                                            out CropWindow.DragRun mRun, out string mTrace, lines.Enqueue);
            System.Windows.Int32Rect mr = mRun.AfterEnd;
            bool mExpected = mr.X == mAx && mr.Y == mAy && mr.Width == mBx - mAx && mr.Height == mBy - mAy;
            newMarginOk = mRan && mRun.Mode == "New" && mExpected && mBx > mAx && mBy > mAy;
            newMarginDetail = $"stage ({mFromX:0.##},{mFromY:0.##})->({mToX:0.##},{mToY:0.##}) " +
                              $"startPixel={mAx},{mAy} endPixel={mBx},{mBy} before={mRun.Before.X}," +
                              $"{mRun.Before.Y},{mRun.Before.Width},{mRun.Before.Height} end={mr.X},{mr.Y}," +
                              $"{mr.Width},{mr.Height} mode={mRun.Mode} expected={mAx},{mAy},{mBx - mAx}," +
                              $"{mBy - mAy} ok={newMarginOk} [{mTrace}]";
        }
        evidence.Line($"crop ui selftest: NEW box drag starting in the blank margin beside the photo: " +
                      $"ok={newMarginOk} ({newMarginDetail})");
        bool newDragOk = newDownOk && newUpOk && newMarginOk;

        // 自測拖曳用的三個小工具。座標全部是**影像像素**。
        //   ① SetBaseline：給視窗一個已知、置中、**不貼齊夾邊界**的矩形，並讀回視窗自己報的像素值。
        //   ② DragPixels：把影像像素換成畫面座標（用視窗自己的縮放），餵進真正的那三個方法。
        //   ③ Px：只為了讓每一行證據讀得出來。
        bool SetBaseline(out int bx, out int by, out int bw, out int bh)
        {
            bx = by = bw = bh = 0;
            if (!CropUi.SelfTestSetRect(0.25, 0.25, 0.5, 0.5, 5000, out _, lines.Enqueue)) return false;
            (bx, by, bw, bh) = CropUi.SelfTestRectPx();
            return bw > 0 && bh > 0;
        }
        bool DragPixels(int fromImageX, int fromImageY, int toImageX, int toImageY,
                        out CropWindow.DragRun run, out string detail)
        {
            (double scale, double left, double top) = CropUi.SelfTestStageMapping();
            double fromStageX = left + fromImageX * scale, fromStageY = top + fromImageY * scale;
            double toStageX = left + toImageX * scale, toStageY = top + toImageY * scale;
            bool ran = CropUi.SelfTestDrag(fromStageX, fromStageY, toStageX, toStageY, 5000,
                                           out run, out string trace, lines.Enqueue);
            detail = $"pixels ({fromImageX},{fromImageY})->({toImageX},{toImageY}) = " +
                     $"stage ({fromStageX:0.##},{fromStageY:0.##})->({toStageX:0.##},{toStageY:0.##}) " +
                     $"via scale={scale:0.######} imageLeft={left} imageTop={top} [{trace}]";
            return ran;
        }
        string Px(System.Windows.Int32Rect r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

        // ③a 移動：從矩形**內部**一點拖到另一點。斷言它真的變了，而且變成預期的像素值。
        bool moveBase = SetBaseline(out int mvX, out int mvY, out int mvW, out int mvH);
        int mvDx = Math.Max(1, mvW / 4), mvDy = Math.Max(1, mvH / 4);
        bool moveRan = DragPixels(mvX + mvW / 3, mvY + mvH / 3, mvX + mvW / 3 + mvDx, mvY + mvH / 3 + mvDy,
                                   out CropWindow.DragRun moveRun, out string moveDetail);
        bool moveMoved = moveRan && (moveRun.AfterEnd.X != moveRun.Before.X ||
                                     moveRun.AfterEnd.Y != moveRun.Before.Y);
        bool moveExpected = moveRan && moveRun.AfterEnd.X == mvX + mvDx &&
                            moveRun.AfterEnd.Y == mvY + mvDy &&
                            moveRun.AfterEnd.Width == mvW && moveRun.AfterEnd.Height == mvH;
        bool moveMode = moveRan && moveRun.Mode == "Move";
        bool moveOk = moveBase && moveRan && moveMode && moveMoved && moveExpected;
        evidence.Line($"crop ui selftest: move drag from inside the rectangle: base={mvX},{mvY},{mvW},{mvH} " +
                      $"expected={mvX + mvDx},{mvY + mvDy},{mvW},{mvH} mid={Px(moveRun.AfterMove)} " +
                      $"end={Px(moveRun.AfterEnd)} mode={moveRun.Mode} moved={moveMoved} " +
                      $"ok={moveOk} ({moveDetail})");

        // ③a-2 移動：**貼齊左右兩邊**的矩形（很常見：直式照片按 16:9 ⇒ 框寬＝圖寬）。
        // ⚠ 來歷（2026-10-07 使用者實機回報：「想平移卻把選取清掉，變成重畫，要從頭來」）。
        //    根因：`moveCanChange` 原本寫成 `寬有餘 && 高有餘`——只要**任一邊貼齊**就是 false，
        //    於是框內拖曳被歸成 `New`（＝把整個框清掉重畫）。③a 的基準矩形置中、四邊都不貼齊，
        //    所以這個缺陷**沒有任何既有的一格抓到它**（這正是它活下來的原因）。
        bool spanBase = CropUi.SelfTestSetRect(0, 0.25, 1, 0.5, 5000, out _, lines.Enqueue);
        (int spX, int spY, int spW, int spH) = CropUi.SelfTestRectPx();
        int spDy = Math.Max(1, spH / 4);
        bool spanRan = DragPixels(spX + spW / 2, spY + spH / 2, spX + spW / 2, spY + spH / 2 + spDy,
                                  out CropWindow.DragRun spanRun, out string spanDetail);
        bool spanMoved = spanRan && spanRun.Mode == "Move" &&
                         spanRun.AfterEnd.Y == spY + spDy && spanRun.AfterEnd.X == spX &&
                         spanRun.AfterEnd.Width == spW && spanRun.AfterEnd.Height == spH;
        // 對照組：**整張圖**（兩軸都貼齊）。此時「移動」在幾何上不可能（可動範圍是 0），
        // 所以框內拖曳**必須**是「重畫」——否則使用者開窗後在圖上拖曳會完全沒反應（那是 2026-10-06
        // 修掉的舊缺陷）。這一格是 ③a-2 的另一邊：兩格一起才把 `moveCanChange` 的條件釘住，
        // 只把 `&&` 改成 `||` 而不看這一格，就會讓舊缺陷復活。
        bool wholeBase = CropUi.SelfTestSetRect(0, 0, 1, 1, 5000, out _, lines.Enqueue);
        (int whX, int whY, int whW, int whH) = CropUi.SelfTestRectPx();
        bool wholeRan = DragPixels(whX + whW / 2, whY + whH / 2, whX + whW / 4, whY + whH / 4,
                                   out CropWindow.DragRun wholeRun, out string wholeDetail);
        bool wholeStartsNew = wholeRan && wholeRun.Mode == "New";
        bool spanOk = spanBase && spanRan && spW > 0 && spH > 0 && spanMoved &&
                      wholeBase && wholeRan && whW > 0 && whH > 0 && wholeStartsNew;
        evidence.Line($"crop ui selftest: move drag on a rectangle that spans the full WIDTH " +
                      $"(one axis flush): base={spX},{spY},{spW},{spH} dy={spDy} " +
                      $"end={Px(spanRun.AfterEnd)} mode={spanRun.Mode} moved={spanMoved} | " +
                      $"whole-image reference: base={whX},{whY},{whW},{whH} mode={wholeRun.Mode} " +
                      $"startsNew={wholeStartsNew} ok={spanOk} ({spanDetail}) ({wholeDetail})");

        // 舊旗標的名字與那一行都留著（套件比對的就是那一行），但**意思變強了**：
        // 它現在斷言的是「拖曳真的把矩形搬到預期的位置」＋「貼齊一邊的矩形也搬得動」，
        // 不再只是「接縫跑過了」。
        bool dragged = moveOk && spanOk;
        evidence.Line($"crop ui selftest: programmatic drag (the three mouse handlers) ok={dragged} " +
                      $"{moveDetail}");

        // ③b 拖到**邊界**上：矩形不可以被推出影像——**拖曳中**與拖曳之後都一樣。
        // ⚠ 這一格同時看 mid 與 end。實測（2026-10-06）：ContinueDrag 收尾呼叫的 Constrain 與 Move
        // 分支自己那兩條 Math.Clamp 是**互相冗餘**的（範圍都是 [0, 影像寬 − 矩形寬]），所以把
        // 「其中一條」的上界改成 W 是**看不出來的**（整個自測仍然全綠，已實測）；兩層一起放寬
        // 才會讓矩形跑出影像，而那是這一格抓到的東西（反向驗證會紅在 clampHeldInsideImage）。
        // 右下角與左上角各拖一次：兩個方向的上／下界都要有牙齒。
        bool clampBase = SetBaseline(out int clX, out int clY, out int clW, out int clH);
        bool toFarRan = DragPixels(clX + clW / 2, clY + clH / 2, imageWpx, imageHpx,
                                   out CropWindow.DragRun toFar, out string toFarDetail);
        bool clampBase2 = SetBaseline(out int clX2, out int clY2, out int clW2, out int clH2);
        bool toOriginRan = DragPixels(clX2 + clW2 / 2, clY2 + clH2 / 2, 0, 0,
                                      out CropWindow.DragRun toOrigin, out string toOriginDetail);
        bool clampOk = clampBase && toFarRan && clampBase2 && toOriginRan &&
                       toFar.Mode == "Move" && toOrigin.Mode == "Move" &&
                       toFar.AfterMove.X == imageWpx - clW && toFar.AfterMove.Y == imageHpx - clH &&
                       toFar.AfterEnd.X == imageWpx - clW && toFar.AfterEnd.Y == imageHpx - clH &&
                       toFar.AfterEnd.Width == clW && toFar.AfterEnd.Height == clH &&
                       toOrigin.AfterMove.X == 0 && toOrigin.AfterMove.Y == 0 &&
                       toOrigin.AfterEnd.X == 0 && toOrigin.AfterEnd.Y == 0 &&
                       toOrigin.AfterEnd.Width == clW2 && toOrigin.AfterEnd.Height == clH2;
        evidence.Line($"crop ui selftest: move drag onto the far corner: base={clX},{clY},{clW},{clH} " +
                      $"expected={imageWpx - clW},{imageHpx - clH},{clW},{clH} mid={Px(toFar.AfterMove)} " +
                      $"end={Px(toFar.AfterEnd)} ok={clampOk} ({toFarDetail})");
        evidence.Line($"crop ui selftest: move drag onto the origin: base={clX2},{clY2},{clW2},{clH2} " +
                      $"expected=0,0,{clW2},{clH2} mid={Px(toOrigin.AfterMove)} end={Px(toOrigin.AfterEnd)} " +
                      $"ok={clampOk} ({toOriginDetail})");

        // ③c 拉把手：移動只是拖曳的一半，而「拉邊／拉角」是使用者最常做的動作之一。
        // 每一格都斷言**兩件事**：預期的那一邊真的動了、而且另一邊**沒有動**。
        bool resizeBase = SetBaseline(out int rsX, out int rsY, out int rsW, out int rsH);
        int rsDx = Math.Max(1, rsW / 4);
        bool rightRan = DragPixels(rsX + rsW, rsY + rsH / 2, rsX + rsW + rsDx, rsY + rsH / 2,
                                    out CropWindow.DragRun rightRun, out string rightDetail);
        bool rightOk = rightRan && rightRun.Mode == "Right" &&
                       rightRun.AfterEnd.X == rsX && rightRun.AfterEnd.Y == rsY &&
                       rightRun.AfterEnd.Width == rsW + rsDx && rightRun.AfterEnd.Height == rsH;
        evidence.Line($"crop ui selftest: drag the RIGHT edge handle outwards: base={rsX},{rsY},{rsW},{rsH} " +
                      $"expected={rsX},{rsY},{rsW + rsDx},{rsH} mid={Px(rightRun.AfterMove)} " +
                      $"end={Px(rightRun.AfterEnd)} mode={rightRun.Mode} ok={rightOk} ({rightDetail})");

        bool shrinkBase = SetBaseline(out int shX, out int shY, out int shW, out int shH);
        int shDx = Math.Max(1, shW / 4);
        bool leftRan = DragPixels(shX, shY + shH / 2, shX + shDx, shY + shH / 2,
                                   out CropWindow.DragRun leftRun, out string leftDetail);
        bool leftOk = leftRan && leftRun.Mode == "Left" &&
                      leftRun.AfterEnd.X == shX + shDx && leftRun.AfterEnd.Width == shW - shDx &&
                      leftRun.AfterEnd.X + leftRun.AfterEnd.Width == shX + shW &&   // 右緣不動
                      leftRun.AfterEnd.Y == shY && leftRun.AfterEnd.Height == shH;
        evidence.Line($"crop ui selftest: drag the LEFT edge handle inwards (shrink): base={shX},{shY},{shW},{shH} " +
                      $"expected={shX + shDx},{shY},{shW - shDx},{shH} rightEdge={shX + shW} " +
                      $"mid={Px(leftRun.AfterMove)} end={Px(leftRun.AfterEnd)} mode={leftRun.Mode} " +
                      $"ok={leftOk} ({leftDetail})");

        bool cornerBase = SetBaseline(out int coX, out int coY, out int coW, out int coH);
        int coDx = Math.Max(1, coW / 4), coDy = Math.Max(1, coH / 4);
        bool cornerRan = DragPixels(coX + coW, coY + coH, coX + coW + coDx, coY + coH + coDy,
                                     out CropWindow.DragRun cornerRun, out string cornerDetail);
        bool cornerOk = cornerRan && cornerRun.Mode == "BottomRight" &&
                        cornerRun.AfterEnd.X == coX && cornerRun.AfterEnd.Y == coY &&
                        cornerRun.AfterEnd.Width == coW + coDx && cornerRun.AfterEnd.Height == coH + coDy;
        evidence.Line($"crop ui selftest: drag the BOTTOM-RIGHT corner handle outwards: base={coX},{coY},{coW},{coH} " +
                      $"expected={coX},{coY},{coW + coDx},{coH + coDy} mid={Px(cornerRun.AfterMove)} " +
                      $"end={Px(cornerRun.AfterEnd)} mode={cornerRun.Mode} ok={cornerOk} ({cornerDetail})");

        bool resizeOk = resizeBase && rightOk && shrinkBase && leftOk && cornerBase && cornerOk;
        evidence.Line($"crop ui selftest: drag kinds: movedToExpected={moveOk} clampHeldInsideImage={clampOk} " +
                      $"handlesResize={resizeOk} " +
                      $"(resize: right-grow={rightOk} left-shrink={leftOk} corner-grow={cornerOk})");

        // ④ 每一個比例鈕都真的按得到，而且按下去之後矩形真的符合那個比例。
        //
        // ⚠ 2026-10-07 擴充（使用者：「裁切成功，目前只有直式比例，也加上橫式的」）。
        // 原本這一格只斷言 `ratio-ok=True`，而那個旗標是**產品自己說自己對不對**
        // （矩形的寬高比 vs 它自己回報的 target）——一個把 `9:16` 定義成 `16.0/9.0` 的 bug
        // 會讓產品與自己一致，這一格照樣綠。所以現在每一顆比例另外被三件事釘住：
        //   ①**方向**：9:16 ⇒ 高 > 寬；16:9 ⇒ 寬 > 高；4:3 ⇒ 寬 > 高；3:4 ⇒ 高 > 寬；1:1 ⇒ 相等；
        //   ②**數值**：產品回報的 `ratio-target` 與矩形自己的寬高比（`rect=` 的像素值）相符；
        //   ③**絕對性**：同一顆比例在直式來源與橫式來源上必須給出**同一個方向**——這是本次真正
        //     新增的性質（比例不隨影像方向翻轉），所以迴圈會跑兩輪（橫式來源＋直式來源）。
        // 期望值全部由**產品自報值**推導：方向的分類是量的（rect 的像素值），
        // 而「這顆比例該朝哪邊」由 `CropRatios` 自己的常數決定；夾具尺寸在這裡只當標籤。
        var ratioLines = new List<string>();
        var perOrientation = new List<(string Orientation, string PresetId, string Direction, bool Ok)>();
        bool ratiosOk = true;

        bool RunRatioLoop(string passLabel)
        {
            (int passW, int passH) = CropUi.SelfTestImageSizePx();
            // ⚠ 來源方向由**量到的尺寸**決定（寫死夾具尺寸正是本專案兩次假綠的成因）。
            string sourceOrientation = OrientationOfSource(passW, passH);
            foreach (CropRatioPreset preset in CropRatios.Presets)
            {
                // 先選一次（讓狀態行裡的按鈕選擇就定位），再給它一個大的自由框，再選一次：
                // 這樣「比例鈕真的改到了矩形」才看得出來（否則會出現「框本來就剛好是 1:1，
                // 所以按不按都對」那種假綠燈）。
                CropUi.SelfTestSelectRatio(preset.Id, 5000, out string ratioDetail, lines.Enqueue);
                CropUi.SelfTestSetRect(0.05, 0.05, 0.90, 0.90, 5000, out _, lines.Enqueue);
                CropUi.SelfTestSelectRatio(preset.Id, 5000, out ratioDetail, lines.Enqueue);
                string state = CropUi.SelfTestState();
                ratioLines.Add($"{preset.Id}: {state}");
                evidence.Line($"crop ui selftest: ratio '{preset.Id}' -> {state}");

                (bool hasTarget, double target) = StateNumber(state, "ratio-target");
                (bool hasActual, double actual) = StateNumber(state, "ratio-actual");
                (bool hasRect, int rectW, int rectH) = StateRectPx(state);
                string direction = hasActual ? DirectionOfRatio(actual) : "unknown";

                bool ok = true;
                string why = "";
                if (preset.Id == CropRatios.DefaultId)
                {
                    // 「自由」不該改動矩形：它沒有目標比例（target=0），而且矩形就是剛剛那個 0.9×0.9
                    // ⇒ 0.9×0.9 的框在兩種方向上都是寬的（框比圖小，兩邊都被縮到 0.9）。
                    if (hasTarget && target != 0)
                    {
                        ok = false;
                        why = $"free must not have a target ratio, got {target:0.####}";
                    }
                }
                else
                {
                    if (!hasTarget || target <= 0)
                    {
                        ok = false;
                        why = "the preset reported no target ratio";
                    }
                    else
                    {
                        // ② 數值：矩形自己的寬高比（像素）必須與回報的 target 相符。
                        // 1 % 容差：最後一步是整數取整，半個像素在幾百 px 上是 0.1 % 等級。
                        double rectRatio = hasRect && rectH > 0 ? (double)rectW / rectH : 0;
                        bool rectMatches = hasRect && rectH > 0 &&
                                           Math.Abs(rectRatio - target) / target <= 0.01;
                        // ① 方向：由 **rect 的像素值**分類（`actual` 只是同一個東西的另一種寫法，
                        // 兩個都驗，因為它們分開壞掉的樣子不同）。
                        bool actualTall = actual < 1 - 0.005, actualWide = actual > 1 + 0.005;
                        bool rectTall = rectRatio < 1 - 0.005, rectWide = rectRatio > 1 + 0.005;
                        bool directionHolds = preset.Id switch
                        {
                            CropRatios.IdTall or CropRatios.IdClassicTall =>
                                actualTall && rectTall && !actualWide && !rectWide,
                            CropRatios.IdSquare => !actualTall && !actualWide && !rectTall && !rectWide,
                            CropRatios.IdOriginal => true,      // 逐張：方向跟來源走，由下面的跨輪比較釘住
                            _ => actualWide && rectWide && !actualTall && !rectTall
                        };
                        if (!state.Contains("ratio-ok=True", StringComparison.Ordinal) ||
                            !rectMatches || !directionHolds || direction == "unknown")
                        {
                            ok = false;
                            why = $"ratio-ok={state.Contains("ratio-ok=True", StringComparison.Ordinal)} " +
                                  $"rectMatchesTarget={rectMatches} directionHolds={directionHolds} " +
                                  $"actual={actual:0.####} target={target:0.####} " +
                                  $"rect={rectW}x{rectH} direction={direction}";
                        }
                    }
                }
                perOrientation.Add((sourceOrientation, preset.Id, direction, ok));
                // 「畫面上不可以說謊」：被選中那顆比例鈕的字必須等於**目前 ratio id 的 label**
                // （期望值由產品自報的 RatioId ＋ LabelOf 推導，這裡不寫死任何字串）。
                (string[] presetLabels, _, _, string shownRatioId, _, _) = CropUi.SelfTestRatioRowState();
                string shownButtonId = CropRatios.ButtonIdOf(shownRatioId);
                int shownIndex = Array.FindIndex(CropRatios.Presets, p => p.Id == shownButtonId);
                string shownLabel = shownIndex >= 0 && presetLabels.Length == CropRatios.Presets.Length
                    ? presetLabels[shownIndex] : "(no button)";
                bool labelHonestHere = string.Equals(shownLabel, CropRatios.LabelOf(shownRatioId), StringComparison.Ordinal) &&
                                       AllLabelsAreExpected(presetLabels);
                evidence.Line($"crop ui selftest: ratio-button-label preset={preset.Id} " +
                              $"selectedRatio={shownRatioId} button={shownButtonId} " +
                              $"labelShown='{shownLabel}' labelExpected='{CropRatios.LabelOf(shownRatioId)}' " +
                              $"labels='{string.Join("|", presetLabels)}' ok={labelHonestHere}");
                if (!labelHonestHere) ratiosOk = false;
                evidence.Line($"crop ui selftest: ratio-check {preset.Id} orientation={sourceOrientation} " +
                              $"source={passW}x{passH} direction={direction} ok={ok}" +
                              (why.Length > 0 ? $" ({why})" : "") + $" ({ratioDetail})");
                if (!ok) ratiosOk = false;
            }
            return true;
        }

        // 第一輪：使用者給的來源（`--crop-source`）。夾具尺寸不寫死，方向由量到的尺寸分類。
        RunRatioLoop("landscape/source");

        // ④b **第二輪：同一顆比例、另一種方向的來源**。
        //
        // 這是本次真正要釘住的新性質：「比例是絕對的，不隨影像方向翻轉」。只有一輪來源的話，
        // 一個「依來源翻轉」的實作（例如 16:9 在直式來源上變成直的）照樣全綠——因為沒有任何一格
        // 比對過兩種來源。第二輪的來源是**來源自己轉 90 度**的副本（真檔案、真解碼），
        // 所以「直式來源」不是自測嘴上說的，是量出來的。
        string rotatedPath = "";
        string rotateDetail = "(not run)";
        bool portraitRun = false;
        try
        {
            if (!TryCreateRotatedCopy(source, out rotatedPath, out rotateDetail))
            {
                evidence.Line($"crop ui selftest: rotation pass SKIPPED ({rotateDetail})");
                ratiosOk = false;
            }
            else
            {
                evidence.Line($"crop ui selftest: rotation pass fixture: {rotateDetail}");
                CropUi.Close();
                bool closedForRotation = WaitFor(() => !CropUi.IsOpen, 5000);
                string[] rotatedBatch = { rotatedPath };
                CropUi.Open(rotatedBatch, _ => { }, lines.Enqueue);
                bool rotatedOpen = WaitFor(() => CropUi.IsOpen, 8000) && CropUi.SelfTestWindowPresent(3000, out _, lines.Enqueue);
                (int rotW, int rotH) = CropUi.SelfTestImageSizePx();
                evidence.Line($"crop ui selftest: reopen on the rotated source: " +
                              $"closedFirst={closedForRotation} opened={rotatedOpen} image={rotW}x{rotH} " +
                              $"orientation={OrientationOfSource(rotW, rotH)}");
                if (!closedForRotation || !rotatedOpen)
                {
                    ratiosOk = false;
                }
                else
                {
                    RunRatioLoop("rotated/source");
                    portraitRun = true;
                }
                // 回到使用者原本那一張：下面 ⑤ 之後的每一格（全圖拒絕、確認、再開、取消）都以
                // 「開窗時矩形＝整張圖」為前提，而那個前提只有在這裡重新開一次之後才成立。
                CropUi.Close();
                bool closedAfterRotation = WaitFor(() => !CropUi.IsOpen, 5000);
                CropUi.Open(batch, spec => confirmed = spec, lines.Enqueue);
                bool reopenedOriginal = WaitFor(() => CropUi.IsOpen, 8000) &&
                                        CropUi.SelfTestWindowPresent(3000, out _, lines.Enqueue);
                (int backW, int backH) = CropUi.SelfTestImageSizePx();
                evidence.Line($"crop ui selftest: reopened the original source after the rotation pass: " +
                              $"closed={closedAfterRotation} reopened={reopenedOriginal} " +
                              $"image={backW}x{backH}");
                if (!closedAfterRotation || !reopenedOriginal) ratiosOk = false;
            }
        }
        finally
        {
            // 自測造出來的檔案自己收拾：留下來會在使用者的暫存目錄裡慢慢累積，
            // 而且它們看起來像真正的照片（見本專案對「殘檔」的紀律）。
            if (rotatedPath.Length > 0)
            {
                try { if (File.Exists(rotatedPath)) File.Delete(rotatedPath); }
                catch (Exception ex) { lines.Enqueue($"crop ui selftest: could not delete {rotatedPath} ({ex.GetType().Name})"); }
            }
        }

        // ④c 兩個方向的一致性（跨輪比較，**只看產品自報的方向**）：
        //   ① 同一顆比例在兩種來源上必須給出**同一個方向**（1:1 一律 square）；
        //   ② 兩個直式 id 必須比它們的橫式版**相反**（9:16 是 tall 而 16:9 是 wide）——
        //      這一條抓的是「兩個 id 其實回同一個值」；
        //   ③ 兩個 id 的 target 必須**不同**（同上，但看數字）；
        //   ④ 「原比例」逐張：兩種來源上的方向必須不同（它就是同一張照片的比例）；
        //      而 `original-flipped` 在兩種來源上也必須不同（而且與 original 相反）。
        var orientationDetail = new List<string>();
        bool orientationOk = portraitRun;
        foreach (CropRatioPreset preset in CropRatios.Presets)
        {
            var pair = perOrientation.Where(p => p.PresetId == preset.Id).ToList();
            if (pair.Count != 2)
            {
                orientationOk = false;
                orientationDetail.Add($"{preset.Id}: expected two passes, got {pair.Count}");
                continue;
            }
            string a = pair[0].Direction, b = pair[1].Direction;
            string sources = $"{pair[0].Orientation}/{pair[1].Orientation}";
            // 「原比例」刻意逐張 ⇒ 方向跟來源走，在兩種來源上**就該不同**（正方形來源除外）。
            // 「自由」沒有任何目標比例 ⇒ 它沒有方向可言（方向全部來自使用者拖的那個框），
            // 所以它也不受「同一顆比例在兩種來源上同方向」這條規則約束——把它算進去會讓
            // 這一格紅在一件正確的事情上，而那種紅比不驗更糟（它教人忽略這一格）。
            bool directionIsFixed = preset.Id != CropRatios.IdFree && preset.Id != CropRatios.IdOriginal;
            bool sameDirectionOk = !directionIsFixed
                ? (pair[0].Orientation == "square" || pair[1].Orientation == "square" || a != b)
                : string.Equals(a, b, StringComparison.Ordinal);
            orientationDetail.Add($"{preset.Id}({sources}) {a}/{b} " +
                                  $"fixedDirection={directionIsFixed} " +
                                  $"same={string.Equals(a, b, StringComparison.Ordinal)} ok={sameDirectionOk}");
            if (!sameDirectionOk) orientationOk = false;
        }
        // 直式 id 與它的橫式版必須是**相反**的兩種方向。
        foreach ((string tallId, string wideId) in
                 new[] { (CropRatios.IdTall, CropRatios.IdWide),
                         (CropRatios.IdClassicTall, CropRatios.IdClassic),
                         (CropRatios.IdOriginalFlipped, CropRatios.IdOriginal) })
        {
            (bool tallHas, double tallTarget) = CropRatios.TargetRatio(tallId, 1234, 5678) is { } tt
                ? (true, tt) : (false, 0);
            (bool wideHas, double wideTarget) = CropRatios.TargetRatio(wideId, 1234, 5678) is { } wt
                ? (true, wt) : (false, 0);
            bool opposite = tallHas && wideHas && Math.Abs(tallTarget * wideTarget - 1.0) < 1e-9 &&
                            Math.Abs(tallTarget - wideTarget) > 1e-6;
            orientationDetail.Add($"pair {tallId}={tallTarget:0.######} vs {wideId}={wideTarget:0.######} " +
                                  $"reciprocalAndDistinct={opposite}");
            if (!opposite) orientationOk = false;
        }
        evidence.Line($"crop ui selftest: ratio orientation matrix (same preset, two source orientations): " +
                      string.Join(" ; ", orientationDetail));
        evidence.Line($"crop ui selftest: ratio orientation ok={orientationOk} portraitPassRan={portraitRun}");
        if (!orientationOk) ratiosOk = false;

        // ④d 方向鈕（2026-10-07 的設計）：把目前的比例在它的兩個方向之間切換。
        // 釘住四件事：①自由／1:1 **停用**（沒有方向可翻）、其餘**可用**；
        // ②切一次真的換成 counterpart（id 與方向都變，而且兩者不同）；
        // ③切兩次回到原狀（可逆）；④**畫面上不可以說謊**——被選中那顆比例鈕的字
        //   必須等於目前 ratio id 的 label（期望值由產品自報的 RatioId 推導）。
        bool directionOk = true;
        // 每一種選中的比例量到的「方向鈕標籤／寬度」與提示文字都記在這裡，迴圈結束後比對是否一致。
        var geometryByPreset = new Dictionary<string, string>(StringComparer.Ordinal);
        var geometryTips = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CropRatioPreset preset in CropRatios.Presets)
        {
            CropUi.SelfTestSelectRatio(preset.Id, 5000, out _, lines.Enqueue);
            CropUi.SelfTestSetRect(0.05, 0.05, 0.90, 0.90, 5000, out _, lines.Enqueue);
            CropUi.SelfTestSelectRatio(preset.Id, 5000, out _, lines.Enqueue);
            string before = CropUi.SelfTestState();
            (bool bHasTarget, double bTarget) = StateNumber(before, "ratio-target");
            (bool bHasActual, double bActual) = StateNumber(before, "ratio-actual");
            string? expectedCounterpart = CropRatios.Counterpart(preset.Id);
            (string[] labelsBefore, string dirTextBefore, bool dirEnabled, string ratioIdBefore, int w, int h) =
                CropUi.SelfTestRatioRowState();
            bool pressed = CropUi.SelfTestToggleDirection(5000, out string toggleDetail, lines.Enqueue);
            string after = CropUi.SelfTestState();
            (bool aHasTarget, double aTarget) = StateNumber(after, "ratio-target");
            (bool aHasActual, double aActual) = StateNumber(after, "ratio-actual");
            (string[] labelsAfter, string dirTextAfter, bool dirEnabledAfter, string ratioIdAfter, _, _) =
                CropUi.SelfTestRatioRowState();
            bool pressedAgain = CropUi.SelfTestToggleDirection(5000, out string toggleBackDetail, lines.Enqueue);
            (string[] labelsBack, _, _, string ratioIdBack, _, _) = CropUi.SelfTestRatioRowState();

            // ④「畫面上不可以說謊」：選中那顆鈕的字＝目前 id 的 label。兩個方向都驗。
            string selectedLabelAfter = CropRatios.ButtonIdOf(ratioIdAfter) is { } bia &&
                                        labelsAfter.Length == CropRatios.Presets.Length
                ? labelsAfter[Array.FindIndex(CropRatios.Presets, p => p.Id == bia)]
                : "(no button)";
            bool labelHonest = expectedCounterpart == null ||
                               (string.Equals(selectedLabelAfter, CropRatios.LabelOf(ratioIdAfter), StringComparison.Ordinal) &&
                                string.Equals(CropRatios.LabelOf(ratioIdAfter), CropRatios.LabelOf(expectedCounterpart), StringComparison.Ordinal));
            bool labelHonestBack = labelsBack.Length == CropRatios.Presets.Length &&
                                   string.Equals(labelsBack[Array.FindIndex(CropRatios.Presets,
                                                       p => p.Id == CropRatios.ButtonIdOf(ratioIdBack))],
                                                 CropRatios.LabelOf(ratioIdBack), StringComparison.Ordinal);
            // ②切一次：id 換成 counterpart，而且**兩個方向真的不同**。
            bool switched = expectedCounterpart is { } want &&
                            string.Equals(ratioIdAfter, want, StringComparison.Ordinal) &&
                            string.Equals(ratioIdBefore, preset.Id, StringComparison.Ordinal) &&
                            aHasActual && DirectionOfRatio(aActual) != DirectionOfRatio(bActual);
            bool targetsFlipped = aHasTarget && bHasTarget && Math.Abs(aTarget * bTarget - 1.0) < 1e-6;
            // ③切兩次回到原狀。
            bool reversible = pressedAgain && string.Equals(ratioIdBack, preset.Id, StringComparison.Ordinal);
            // ①停用／可用。
            bool enabledAsExpected = expectedCounterpart == null ? !dirEnabled : dirEnabled;
            // ⑤ **方向鈕的標籤**（2026-10-07 修正）。先前這裡要求「切換鈕的字含有目前形式的 label」，
            //    那正是外部視覺模型把停用時的字讀成**第六個比例選項**的原因（字裡真的有一個比例名）。
            //    現在的契約剛好相反：標籤**永遠不含任何比例名**，只說方向，而且停用時是一個常數。
            (bool rectFound, int rectW, int rectH) = StateRectPx(before);
            string rectDirection = rectW > rectH ? "wide" : "tall";
            bool labelIsVocabulary = Array.IndexOf(CropWindow.DirectionLabelVocabulary, dirTextBefore) >= 0;
            // 「不含任何比例名」用**產品自己的標籤字彙**來檢查（CropRatios.All 的每一個 label），
            // 而不是只挑幾顆按鈕：`9:16`／`原比例 90°` 這些沒有按鈕的 id 也算比例名。
            bool labelHasNoRatioName = true;
            foreach (CropRatioPreset anyPreset in CropRatios.All)
                if (dirTextBefore.Contains(anyPreset.Label, StringComparison.Ordinal) ||
                    dirTextAfter.Contains(anyPreset.Label, StringComparison.Ordinal))
                    labelHasNoRatioName = false;
            // 停用時：**短而固定的標籤**，而且它必須**不等於任何一顆比例鈕的字**——這一條直接擋掉
            // 「被讀成比例鈕」那個缺陷。原因住在 ToolTip（不佔寬度的媒介），而且一定要有。
            bool disabledLabelOk = expectedCounterpart != null ||
                                   (string.Equals(dirTextBefore, CropWindow.DirectionLabelDisabled, StringComparison.Ordinal) &&
                                    labelHasNoRatioName &&
                                    !Array.Exists(labelsBefore, l => string.Equals(l, dirTextBefore, StringComparison.Ordinal)));
            // 可用時：標籤＝`方向：` ＋ **矩形量出來的方向**（橫式／直式）。期望值來自矩形的實際像素，
            // 不是寫死的字串，所以「標籤說橫式、矩形其實是直的」會紅。
            bool enabledLabelOk = expectedCounterpart == null ||
                                  (string.Equals(dirTextBefore, CropWindow.DirectionLabelPrefix +
                                                                 (rectDirection == "wide"
                                                                      ? CropWindow.DirectionWide
                                                                      : CropWindow.DirectionTall),
                                                  StringComparison.Ordinal) &&
                                   labelHasNoRatioName);
            // ⚠ WPF **預設不對停用的控制項顯示 ToolTip**，而這顆鈕「為什麼不能按」的原因**只**放在
            //    ToolTip 裡 ⇒ 不開 `ShowOnDisabled`，那個原因就寫在使用者永遠看不到的地方，
            //    「不是靜默無效」會變成假的（2026-10-07：外部視覺模型看 dump PNG 時指出「變灰但
            //    畫面上完全沒有提示為什麼」，查證後 WPF 預設行為確實如此）。
            bool tipShownOnDisabled = CropUi.SelfTestDirectionToolTipShownWhenDisabled();
            bool dirLabelOk = labelIsVocabulary && disabledLabelOk && enabledLabelOk && tipShownOnDisabled &&
                              dirTextBefore.Length > 0 && dirTextAfter.Length > 0;
            // ⚠ 寬度用**配置值**（`Width`）而不是 `ActualWidth`：實測發現第一次排版前 `ActualWidth`
            //    會是 0（同一顆鈕在下一個預設就量到 96），那是**量測時機**而不是寬度不穩定。
            //    「與選中的預設無關」講的是這顆鈕被要求多寬，而配置值是常數、不受排版時機影響。
            //    實際配置到的寬度另外記在幾何行裡（`actual=`），讓「有沒有被排版過」看得見。
            double dirButtonWidth = CropUi.SelfTestDirectionButtonWidthConfigured();
            bool dirWidthOk = Math.Abs(dirButtonWidth - CropWindow.DirectionButtonWidth) < 0.5;
            // 每一顆預設（＝每一種選中的比例）量到的標籤與寬度都記下來，迴圈結束後比對。
            geometryByPreset[preset.Id] = $"{dirTextBefore}|w={dirButtonWidth:0.###}" +
                                          $"|actual={CropUi.SelfTestDirectionButtonWidth():0.###}";
            geometryTips[preset.Id] = CropUi.SelfTestDirectionButtonToolTip();
            bool toggleOk = enabledAsExpected && (expectedCounterpart == null
                        ? (!pressed && !pressedAgain && string.Equals(ratioIdAfter, preset.Id, StringComparison.Ordinal) &&
                           labelHonestBack)
                        : (switched && targetsFlipped && reversible && labelHonest && labelHonestBack)) &&
                      dirLabelOk && dirWidthOk && dirEnabled == dirEnabledAfter;
            evidence.Line($"crop ui selftest: direction-toggle {preset.Id} source={w}x{h} " +
                          $"before={ratioIdBefore} after={ratioIdAfter} back={ratioIdBack} " +
                          $"targetBefore={bTarget:0.########} targetAfter={aTarget:0.########} " +
                          $"targetProduct={(bHasTarget && aHasTarget ? bTarget * aTarget : 0):0.########} " +
                          $"hasTarget={(bHasTarget && aHasTarget)} " +
                          $"directionBefore={DirectionOfRatio(bActual)} directionAfter={DirectionOfRatio(aActual)} " +
                          $"labelsBefore='{string.Join("|", labelsBefore)}' labelsAfter='{string.Join("|", labelsAfter)}' " +
                          $"selectedLabel='{selectedLabelAfter}' expectedLabel='{CropRatios.LabelOf(ratioIdAfter)}' " +
                          $"dirTextBefore='{dirTextBefore}' dirTextAfter='{dirTextAfter}' " +
                          $"rectDirection={rectDirection} labelInVocabulary={labelIsVocabulary} " +
                          $"labelHasNoRatioName={labelHasNoRatioName} disabledLabelOk={disabledLabelOk} " +
                          $"enabledLabelOk={enabledLabelOk} dirLabelOk={dirLabelOk} " +
                          $"dirButtonWidth={dirButtonWidth:0.###} " +
                          $"dirButtonWidthActual={CropUi.SelfTestDirectionButtonWidth():0.###} " +
                          $"dirTipChars={CropUi.SelfTestDirectionButtonToolTip().Length} " +
                          $"tipOnDisabled={tipShownOnDisabled} " +
                          $"enabled={dirEnabled} enabledAfter={dirEnabledAfter} pressed={pressed} " +
                          $"pressedAgain={pressedAgain} switched={switched} targetsFlipped={targetsFlipped} " +
                          $"reversible={reversible} labelHonest={labelHonest} labelHonestBack={labelHonestBack} " +
                          $"ok={toggleOk} ({toggleDetail}) ({toggleBackDetail})");
            if (!toggleOk) directionOk = false;
        }

        // ④d-2 那一列的**幾何穩定性**：方向鈕的標籤與寬度必須與「選中的是哪一個預設」無關。
        // 先前停用時的字是 `{比例}（沒有方向可翻）`、啟用時是 `{比例} → {另一比例}`——兩種狀態的字長
        // 差很多，而字長會改變按鈕的自然寬，所以整列寬度會隨著選中的比例跳動（外部視覺模型獨立看
        // dump PNG 時也指出那顆鈕「比其他比例鈕寬一截」）。這一塊把「寬度與標籤都不隨預設變動」變成
        // 一個可斷言的布林值：逐一在五個預設下量，全部必須相同。
        // 值的格式是 `{標籤}|w={配置寬}|actual={實際寬}`（見下面每個預設的寫入處）。
        // ⚠ **標籤本來就該隨方向變**（`方向：橫式` / `方向：直式`），所以「幾何穩定」只比對 `|`
        //    之後的寬度部分。早先這裡拿整串比對，等於要求標籤在五個預設下都相同——那是把
        //    **正確的行為判成紅**（2026-10-07：產品行為正確、斷言寫錯，害 m3d S18.15/S18.20 假紅）。
        //    「標籤與寬度都不隨預設變動」拆成兩件事：寬度看這裡，標籤看下面的 disabledLabelIsOneValue
        //    （停用時只准有一個值）＋每個預設自己的 labelIsVocabulary／labelHasNoRatioName。
        static string GeometryPart(string value)
        {
            int bar = value.IndexOf('|');
            return bar < 0 ? value : value.Substring(bar);
        }
        var distinctGeometry = new HashSet<string>(
            geometryByPreset.Values.Select(GeometryPart), StringComparer.Ordinal);
        bool geometryStable = geometryByPreset.Count == CropRatios.Presets.Length && distinctGeometry.Count == 1;
        // 停用時的字**只能有一個值**：`free` 與 `1:1` 是不同的預設，但它們的停用標籤必須完全相同
        // （先前一個是 `自由（沒有方向可翻）`、另一個是 `1:1（沒有方向可翻）`——同一個狀態卻兩種字）。
        var disabledLabels = new HashSet<string>(StringComparer.Ordinal);
        var enabledLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (CropRatioPreset p in CropRatios.Presets)
        {
            if (!geometryByPreset.TryGetValue(p.Id, out string? geometry)) continue;
            // ⚠ 分隔符是 `|`，不是 `/`。早先這裡寫 `Split('/')`，取到的是**整串**
            //    `方向|w=96|actual=96`，於是跟 `DirectionLabelDisabled`（`方向`）永遠不相等，
            //    讓 `disabledLabelIsOneValue` 恆為 false（產品行為其實完全正確，2026-10-07）。
            string label = geometry.Split('|')[0];
            if (CropRatios.Counterpart(p.Id) == null) disabledLabels.Add(label);
            else enabledLabels.Add(label);
        }
        bool disabledLabelIsOneValue = disabledLabels.Count == 1 &&
                                       string.Equals(disabledLabels.First(), CropWindow.DirectionLabelDisabled,
                                                     StringComparison.Ordinal) &&
                                       !enabledLabels.Contains(CropWindow.DirectionLabelDisabled);
        evidence.Line($"crop ui selftest: direction-button-geometry presets={geometryByPreset.Count} " +
                      $"distinct={distinctGeometry.Count} disabledLabels={disabledLabels.Count} " +
                      $"enabledLabels={enabledLabels.Count} disabledLabelIsOneValue={disabledLabelIsOneValue} " +
                      $"geometryStable={geometryStable} " +
                      string.Join(" ; ", geometryByPreset.Select(kv => $"{kv.Key}={kv.Value}")));
        evidence.Line($"crop ui selftest: direction button geometry stable={geometryStable && disabledLabelIsOneValue}");
        if (!(geometryStable && disabledLabelIsOneValue)) directionOk = false;

        evidence.Line($"crop ui selftest: direction toggle ok={directionOk}");
        if (!directionOk) ratiosOk = false;

        // ④d-3 連續切換方向：**不可以越來越小**。
        // ⚠ 來歷（2026-10-07 使用者實機回報）：「如果連續按直式跟橫式切換的話裁切框會越來越小」。
        //    根因：ToggleDirection 走的是 SelectRatio ⇒ Constrain，而 Constrain 的語意是
        //    「在**舊框內**找最大的等比矩形」——每一步都拿上一步的結果再夾一次，於是單調縮小
        //    （600x338 → 190x338 → 190x107 → …，每次砍掉一半以上）。
        //    正確語意是「同一個構圖翻到另一邊」⇒ **保留長邊**（可逆），不是再夾一次。
        bool flipBase = CropUi.SelfTestSetRect(0, 0, 1, 1, 5000, out _, lines.Enqueue);
        bool flipWide = flipBase && CropUi.SelfTestSelectRatio(CropRatios.IdWide, 5000, out _, lines.Enqueue);
        (int fbx, int fby, int fbw, int fbh) = CropUi.SelfTestRectPx();
        var flipSizes = new List<(int W, int H)>();
        bool flipRan = flipWide && fbw > 0 && fbh > 0;
        while (flipRan && flipSizes.Count < 6)
        {
            if (!CropUi.SelfTestToggleDirection(5000, out _, lines.Enqueue)) { flipRan = false; break; }
            (int ffx, int ffy, int ffw, int ffh) = CropUi.SelfTestRectPx();
            if (ffw <= 0 || ffh <= 0) { flipRan = false; break; }
            flipSizes.Add((ffw, ffh));
        }
        // 三個條件，全部用產品自己報出來的尺寸推導（沒有寫死任何夾具數字）：
        //   ① 第一次翻轉**保留長邊**（「同一個構圖翻過去」的直接後果）；
        //   ② 六次翻轉之中，面積不得比初始小 1% 以上（擋掉單調縮小）；
        //   ③ 從第二次起必須是**嚴格的 2-循環**（兩個方向的大小固定，不再漂移）。
        // 四個條件，全部用產品自己報出來的尺寸與影像尺寸推導（沒有寫死任何夾具數字）：
        //   ① 偶數次（翻回來）必須**精確回到原來的尺寸**——這正是使用者要的「維持一樣大小」；
        //   ② 奇數次（翻過去）每次都一樣（沒有逐次漂移）；
        //   ③ 奇數次真的變成直式（w/h ≈ 9:16，容許 1px 整數捨入）
        //   ④ 翻過去拿到的是「放得下的最大框」：面積 ≥ min(原面積, 影像內最大的 9:16 面積) 的 95%
        bool flipHas6 = flipSizes.Count == 6;
        bool flipBackToBase = flipHas6 && flipSizes[1] == (fbw, fbh) &&
                              flipSizes[3] == (fbw, fbh) && flipSizes[5] == (fbw, fbh);
        bool flipOddStable = flipHas6 && flipSizes[0] == flipSizes[2] && flipSizes[2] == flipSizes[4] &&
                             flipSizes[0] != (fbw, fbh);
        //   ③ 奇數次真的變成直式：與 9:16 的差不得超過 1px（整數矩形不可能剛好等於無理數比例；
        //      實測 338x600 與 9:16 差 0.15%，那是 337.5 → 338 的進位。橫式的 600x338 會差到 3 倍以上）
        bool flipOddIsTall = flipHas6 && Math.Abs(flipSizes[0].W * 16 - flipSizes[0].H * 9) <= 16;
        int tallW = (int)Math.Floor(imageHpx * (9.0 / 16)), tallH = imageHpx;
        if (tallW > imageWpx) { tallW = imageWpx; tallH = (int)Math.Floor(imageWpx / (9.0 / 16)); }
        long largestTallArea = (long)Math.Max(1, tallW) * Math.Max(1, tallH);
        long flipBaseArea = (long)fbw * fbh;
        long flipExpectedArea = Math.Min(flipBaseArea, largestTallArea);
        bool flipOddBigEnough = flipHas6 &&
                                (long)flipSizes[0].W * flipSizes[0].H * 100 >= flipExpectedArea * 95;
        // 收尾：把視窗還原成後續階段期待的狀態（比例＝預設＝自由、矩形＝整張圖）。
        // ⚠ 一定要還原：不還原的話 ⑤a「整張圖被拒絕」會紅——因為比例還停在 9:16，
        //    Constrain 會把「整張圖」收斂成 9:16，於是那格的前提（矩形＝整張圖）不成立。
        //    實測就是這樣紅的：fullFrameRefused=False subPixelClamped=False confirmedMatches=False。
        bool flipReset = CropUi.SelfTestSelectRatio(CropRatios.DefaultId, 5000, out _, lines.Enqueue) &&
                         CropUi.SelfTestSetRect(0, 0, 1, 1, 5000, out _, lines.Enqueue);
        bool flipOk = flipRan && flipHas6 && flipBackToBase && flipOddStable &&
                      flipOddIsTall && flipOddBigEnough && flipReset;
        evidence.Line($"crop ui selftest: toggling the direction six times: base={fbw}x{fbh} " +
                      $"sizes={string.Join(" -> ", flipSizes.Select(s => $"{s.W}x{s.H}"))} " +
                      $"backToBase={flipBackToBase} oddStable={flipOddStable} oddIsTall={flipOddIsTall} " +
                      $"oddArea={flipSizes.FirstOrDefault().W * flipSizes.FirstOrDefault().H} " +
                      $"expectedArea={flipExpectedArea} (largestTall={largestTallArea}) " +
                      $"oddBigEnough={flipOddBigEnough} ok={flipOk}");
        if (!flipOk) ratiosOk = false;

        // ④e 比例列的幾何：**五顆比例鈕 ＋ 一顆方向鈕**還放得下嗎？
        // 「沒有捲軸」不足以回答它——StackPanel 不會生捲軸，它把放不下的東西**裁掉**。
        // 所以由視窗自己報「最右緣 vs 這一列被配置到的寬度」，腳本不量像素。
        (double rowWidthPx, double rowConstraintPx, double rowMaxRightPx, int rowChildren,
         double windowWidthPx, bool rowFits) = CropUi.SelfTestRatioRowMetrics();
        bool rowLayoutOk = rowFits && rowChildren == CropRatios.Presets.Length + 3 && rowWidthPx > 0;
        evidence.Line($"crop ui selftest: ratio-button-row children={rowChildren} " +
                      $"(presets={CropRatios.Presets.Length} + label + direction + hint) " +
                      $"rowWidth={rowWidthPx:0.##} constraint={rowConstraintPx:0.##} " +
                      $"maxChildRight={rowMaxRightPx:0.##} windowWidth={windowWidthPx:0.##} " +
                      $"contentWidth={windowWidthPx - 40:0.##} fits={rowFits} ok={rowLayoutOk}");
        if (!rowLayoutOk) ratiosOk = false;

        // ⑤ 退化輸入之一：整張圖 → **當場拒絕**並顯示提示（不可以帶著它去寫檔）。
        // 這一格的關鍵是「拒絕之後視窗還開著、而且沒有東西被交回去」——只看「有沒有印出拒絕」
        // 會被一個已經自己關掉的視窗騙過去。
        bool fullSet = CropUi.SelfTestSetRect(0, 0, 1, 1, 5000, out string fullDetail, lines.Enqueue);
        string fullState = CropUi.SelfTestState();
        CropUi.SelfTestConfirm(5000, out string fullConfirm, lines.Enqueue);
        bool openAfterFull = CropUi.IsOpen;
        // ⚠ 期望值必須是**這張圖自己的尺寸**，不是一組寫死的數字。這裡原本是
        // `fullState.Contains("rect=0,0,512,384")`——512x384 是當時唯一的夾具，於是整條自測
        // 只在那一個尺寸上成立：換一張 3000x2250 的大圖，矩形的像素值正確地是 0,0,3000,2250、
        // 確認鈕也正確地拒絕了全圖（confirmed 仍是 null、視窗還開著），這一格卻仍然紅。
        // 尺寸的來源只能是視窗自己（CropUi.SelfTestImageSizePx），不是夾具。
        (int fullImageW, int fullImageH) = CropUi.SelfTestImageSizePx();
        bool fullRefused = (confirmed == null) && openAfterFull &&
                           fullImageW > 0 && fullImageH > 0 &&
                           fullState.Contains($"rect=0,0,{fullImageW},{fullImageH}");
        evidence.Line($"crop ui selftest: full-frame rectangle -> set={fullSet} refused={fullRefused} " +
                      $"stillOpen={openAfterFull} ({fullDetail}) ({fullConfirm})");
        evidence.Line($"crop ui selftest: full-frame state: {fullState}");

        // ⑤b 退化輸入之二：比一個像素還細 → **夾成 1 px**（不是拒絕，也不是 0）。
        bool tinySet = CropUi.SelfTestSetRect(0.5, 0.5, 0.0001, 0.0001, 5000, out string tinyDetail, lines.Enqueue);
        string tinyState = CropUi.SelfTestState();
        bool tinyClamped = tinySet && CropUi.IsOpen && tinyState.Contains("rect=") &&
                           (tinyState.Contains("1,1 ") || tinyState.Contains(",1,1 ") ||
                            tinyState.Contains("width=1 height=1"));
        evidence.Line($"crop ui selftest: sub-pixel rectangle -> set={tinySet} clampedTo1px={tinyClamped} " +
                      $"{tinyDetail} state={tinyState}");

        // ⑥ 確認 → 帶回**剛剛畫的那個**正規化矩形、視窗真的關掉。
        // 先切回「自由」：否則目前的比例預設會在換算時再把矩形修一次，這一格就變成在斷言
        // 「比例預設的數學」而不是「確認帶回了什麼」。
        CropUi.SelfTestSelectRatio(CropRatios.DefaultId, 5000, out _, lines.Enqueue);
        bool finalSet = CropUi.SelfTestSetRect(0.125, 0.125, 0.75, 0.75, 5000, out string finalDetail, lines.Enqueue);
        evidence.Line($"crop ui selftest: final rectangle set={finalSet} {finalDetail}");
        CropUi.SelfTestConfirm(5000, out string confirmDetail, lines.Enqueue);
        bool closed = WaitFor(() => !CropUi.IsOpen, 3000);
        // ⚠ 視窗關閉與「回呼交回矩形」是兩件事，而它們的順序**不是**保證的：Closed 事件先到，
        // 交回矩形的那一行才跑。所以這裡等的是**矩形本身**，不是「視窗關了」——
        // 否則這一格會用 0 ms 的視窗去讀一個還沒被填的值，變成一個看時序綠紅的假斷言。
        WaitFor(() => confirmed != null, 3000);
        CropSpec? handedOver = confirmed;
        string confirmedText = handedOver is { } handed ? handed.Describe() : "(nothing)";
        // 交回來的必須就是畫的那個矩形（0.125/0.75 在 512x384 上是整數邊界，所以可以要求精確）。
        bool handedOverMatches = handedOver is { } h2 &&
                                 Math.Abs(h2.X - 0.125) < 0.002 && Math.Abs(h2.Y - 0.125) < 0.002 &&
                                 Math.Abs(h2.Width - 0.75) < 0.002 && Math.Abs(h2.Height - 0.75) < 0.002;
        evidence.Line($"crop ui selftest: confirmed={confirmedText} matchesWhatWasDrawn={handedOverMatches} " +
                      $"closed={closed} ({confirmDetail})");

        // ⑦ 關掉之後可以再開
        confirmed = null;
        CropUi.Open(batch, spec => confirmed = spec, lines.Enqueue);
        bool reopened = WaitFor(() => CropUi.IsOpen, 8000);
        bool reopenedPresent = false;
        string reopenDetail = "(never asked)";
        for (int attempt = 0; attempt < 20 && !reopenedPresent; attempt++)
        {
            reopenedPresent = CropUi.SelfTestWindowPresent(3000, out reopenDetail, lines.Enqueue);
            if (!reopenedPresent) Thread.Sleep(250);
        }
        evidence.Line($"crop ui selftest: the window can be opened again after a close: " +
                      $"reopened={reopened} windowPresent={reopenedPresent} state={CropUi.SelfTestState()}");

        // ⑧ 取消 → 不帶回任何矩形
        CropUi.SelfTestSetRect(0.2, 0.2, 0.5, 0.5, 5000, out _, lines.Enqueue);
        CropUi.SelfTestCancel(5000, out string cancelDetail, lines.Enqueue);
        bool closedByCancel = WaitFor(() => !CropUi.IsOpen, 3000);
        bool cancelGaveNothing = confirmed == null;
        evidence.Line($"crop ui selftest: cancel -> closed={closedByCancel} resultHandedBack={!cancelGaveNothing} " +
                      $"({cancelDetail})");

        while (lines.TryDequeue(out string? buffered)) evidence.Line(buffered);

        bool ok = opened && windowPresent && singleInstance && settingsOpen && cropStillThere
                  && dragged && clampOk && resizeOk && newDragOk && ratiosOk && fullSet && fullRefused && tinyClamped
                  && finalSet && closed && handedOverMatches
                  && closedByCancel && cancelGaveNothing
                  && reopened && reopenedPresent;
        evidence.Line($"crop ui selftest done: ok={ok} (opened={opened} windowPresent={windowPresent} " +
                      $"singleInstance={singleInstance} settingsPageCoexists={settingsOpen && cropStillThere} " +
                      $"programmaticDrag={dragged} clampedDrag={clampOk} handleDrag={resizeOk} " +
                      $"newBoxDrag={newDragOk} (down={newDownOk} reverse={newUpOk} margin={newMarginOk}) " +
                      $"ratioPresetsOk={ratiosOk} " +
                      $"fullFrameRefused={fullRefused} subPixelClamped={tinyClamped} " +
                      $"confirmedClosed={closed} confirmedMatches={handedOverMatches} " +
                      $"cancelled={closedByCancel} cancelGaveNothing={cancelGaveNothing} " +
                      $"reopened={reopened} reopenedPresent={reopenedPresent})");
        evidence.Line("crop ui selftest: ratio states: " + string.Join(" ; ", ratioLines));
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 合併排序視窗的生命週期與互動自測（切片 B）。`--merge-ui-selftest a.pdf;b.pdf;c.pdf`。
    /// 形狀比照 <see cref="RunCropUiSelfTest"/>，而且**一律走自測接縫、不合成任何滑鼠事件**。
    ///
    /// 它驗的是「視窗自己說得清楚的四件事」：
    ///   ① 開啟時**初始順序 = 來源順序**（含每一份的頁數）；
    ///   ② **拖曳排序**（含來回拖曳要還原）；
    ///   ③ **移除**（含「最後一份不能移除」）；
    ///   ④ **確認帶回的順序 = 畫面顯示的順序**、**取消不帶回任何順序**；
    ///   ⑤ 單一實例、關掉之後可以再開。
    ///
    /// ⚠ 「輸出檔的頁序 = 畫面顯示的順序」**不在這裡驗**：那需要真的起一個 worker 並讀出檔案的頁序，
    ///   屬於 `verify-m3d.ps1` 的 S20 範圍（它拿 `--merge-pdf-inspect` 逐頁量尺寸與內容雜湊）。
    ///   這一條自測只保證「帶出去的順序是對的」——兩條合起來才是完整的那句話。
    /// </summary>
    internal static int RunMergeUiSelfTest(Options opt, Evidence evidence)
    {
        var lines = new ConcurrentQueue<string>();
        bool dumpOnly = string.IsNullOrEmpty(opt.MergeUiSelfTest) && !string.IsNullOrEmpty(opt.MergeUiDump);

        string[] batch = (opt.MergeUiSelfTest ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        if (!dumpOnly && batch.Length < 2)
        {
            evidence.Line($"merge ui selftest: FAILED to open: --merge-ui-selftest needs at least 2 files " +
                          $"(got {batch.Length}: '{opt.MergeUiSelfTest ?? "(none)"}')");
            return 1;
        }
        foreach (string candidate in batch)
        {
            if (AtomicWriter.Exists(candidate)) continue;
            evidence.Line($"merge ui selftest: FAILED to open: '{candidate}' does not exist");
            return 1;
        }

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

        // ---------------------------------------------------------------- `--merge-ui-dump` 那一條
        if (dumpOnly)
        {
            MergeUi.Open(batch, (_, _) => { }, lines.Enqueue);
            bool up = WaitFor(() => MergeUi.IsOpen, 8000);
            if (!up)
            {
                while (lines.TryDequeue(out string? early)) evidence.Line(early);
                evidence.Line("merge ui dump: FAILED to open the window");
                return 1;
            }
            Thread.Sleep(500);      // 讓排版跑完，dump 才拿得到真正的畫面
            bool dumped = MergeUi.DumpFrame(opt.MergeUiDump, 8000, out string dumpDetail, lines.Enqueue);
            MergeUi.Close();
            WaitFor(() => !MergeUi.IsOpen, 3000);
            while (lines.TryDequeue(out string? line)) evidence.Line(line);
            evidence.Line($"merge ui dump: ok={dumped} detail={dumpDetail} path={opt.MergeUiDump}");
            return dumped ? 0 : 1;
        }

        // ① 開啟
        string[]? confirmed = null;
        int confirmCalls = 0;
        MergeUi.Open(batch, (order, _) => { confirmed = order; confirmCalls++; }, lines.Enqueue);
        bool opened = WaitFor(() => MergeUi.IsOpen, 8000);
        bool windowPresent = false;
        string presentDetail = "(never asked)";
        for (int attempt = 0; attempt < 20 && !windowPresent; attempt++)
        {
            windowPresent = MergeUi.SelfTestWindowPresent(3000, out presentDetail, lines.Enqueue);
            if (!windowPresent) Thread.Sleep(250);
        }
        evidence.Line($"merge ui selftest: opened={opened} windowPresent={windowPresent} " +
                      $"state={MergeUi.SelfTestState()}");
        if (!opened || !windowPresent)
        {
            while (lines.TryDequeue(out string? early)) evidence.Line(early);
            evidence.Line($"merge ui selftest done: ok=False (opened={opened} windowPresent={windowPresent})");
            return 1;
        }

        // ② 初始順序**必須等於來源順序**。期望值來自命令列那一組檔（＝呼叫端給的順序），
        //    而不是「上一次跑出來的東西」——所以這是一條真的斷言，不是回聲。
        string[] initial = MergeUi.SelfTestOrder();
        bool initialMatches = initial.Length == batch.Length;
        for (int i = 0; initialMatches && i < batch.Length; i++)
            if (!string.Equals(initial[i], batch[i], StringComparison.Ordinal)) initialMatches = false;
        evidence.Line($"merge ui selftest: initial order matches the source order={initialMatches} " +
                      $"source=[{string.Join(",", batch.Select(Path.GetFileName))}] " +
                      $"window=[{string.Join(",", initial.Select(Path.GetFileName))}]");
        evidence.Line($"merge ui selftest: rows: {MergeUi.SelfTestRowsLine()}");

        // ③ 單一實例：開著的時候第二次 Open 不可以再開一個視窗，也不可以換掉回呼。
        int callsBeforeSecondOpen = confirmCalls;
        MergeUi.Open(batch, (_, _) => { confirmCalls += 100; }, lines.Enqueue);
        bool stillOpen = MergeUi.IsOpen;
        bool singleInstance = stillOpen && confirmCalls == callsBeforeSecondOpen;
        evidence.Line($"merge ui selftest: a second open while the window is up was " +
                      $"{(stillOpen ? "ignored (single instance)" : "ACCEPTED (two windows!)")} " +
                      $"callbackReplaced={confirmCalls != callsBeforeSecondOpen}");

        // ④ 拖曳排序：把第 1 列往下拖 2 列 ⇒ 它應該落到第 3 位，其餘整體往前移。
        string[] beforeDrag = MergeUi.SelfTestOrder();
        bool dragged = MergeUi.SelfTestDrag(0, 2, 5000, out string dragDetail, lines.Enqueue);
        string[] afterDrag = MergeUi.SelfTestOrder();
        // 期望值：第 1 個搬到它落點，其餘保留相對順序（這正是「搬移」的定義，與就地交換不同）。
        //
        // ⚠ 落點**夾在陣列範圍內**，而且算式對任何 n ≥ 2 都成立：`SelfTestDrag` 的 deltaRows
        //    是「往下 2 列」，列數只有 2 時落點就是最後一列。寫死 `beforeDrag[1], beforeDrag[2]`
        //    會讓 2 個檔的請求直接丟 `IndexOutOfRangeException` 出來（**實測 2026-10-07**：
        //    `--merge-ui-selftest a.pdf;b.pdf` 的行程以 0xE0434352 收場，而它只是一個診斷指令
        //    ——診斷本身崩潰比診斷說「這一組我驗不了」還糟，因為前者看起來像產品壞了）。
        int dragTo = Math.Min(2, Math.Max(0, beforeDrag.Length - 1));
        string[] wantDrag;
        if (beforeDrag.Length >= 2)
        {
            var moved = new List<string>(beforeDrag);
            string head = moved[0];
            moved.RemoveAt(0);
            moved.Insert(Math.Min(dragTo, moved.Count), head);
            wantDrag = moved.ToArray();
        }
        else
        {
            wantDrag = beforeDrag;
        }
        bool dragMatches = afterDrag.SequenceEqual(wantDrag, StringComparer.Ordinal);
        evidence.Line($"merge ui selftest: drag movedTheOrder={dragged} matchesTheMoveSemantics={dragMatches} " +
                      $"before=[{string.Join(",", beforeDrag.Select(Path.GetFileName))}] " +
                      $"after=[{string.Join(",", afterDrag.Select(Path.GetFileName))}] " +
                      $"want=[{string.Join(",", wantDrag.Select(Path.GetFileName))}] ({dragDetail})");

        // ⚠ 這一行是**給腳本讀的**，而且它是「輸出頁序 = 畫面顯示的順序」那條斷言的**唯一**來源。
        //
        //    為什麼要有它：驗收腳本若要自己算出「第 1 列往下拖 2 列之後順序是什麼」，那個算式就
        //    變成**第二份排序規則**——它與產品不一致時，那一格會紅在測試的算式上，而不是紅在產品上
        //    （這正是本專案對 CropWindow.SelfTestStageMapping 立的同一條紀律）。所以產品的接縫
        //    直接把「拖完之後畫面上是什麼順序」講出來，腳本拿它去餵引擎、再量輸出檔的頁序。
        //    這樣「畫面 → 輸出」的中間沒有任何一端是腳本自己補的。
        string[] dragShownOrder = afterDrag;
        evidence.Line("merge ui selftest: after drag the window shows " +
                      string.Join("|", dragShownOrder.Select(Path.GetFileName)));

        // ④b 來回拖曳：拖走再拖回原處，順序必須**完全還原**（見 MergeWindow.ContinueDrag 的說明）。
        //     ⚠ 這一條抓的是「把位移累加起來」那種寫法（每次 ±1），它會讓來回拖曳回不到原位。
        bool dragReturned = MergeUi.SelfTestDragAndReturn(2, 2, 5000, out string returnDetail, lines.Enqueue);
        evidence.Line($"merge ui selftest: drag out and back restores the order={dragReturned} ({returnDetail})");

        // ⑤ 移除：按下**真正的移除鈕**，被移除的那一份必須從順序裡消失，而且畫面上的列數跟著少一。
        string[] beforeRemove = MergeUi.SelfTestOrder();
        string removedName = Path.GetFileName(beforeRemove[0]);
        bool removePressed = MergeUi.SelfTestRemoveRow(0, 5000, out string removeDetail, lines.Enqueue);
        string[] afterRemove = MergeUi.SelfTestOrder();
        bool removedGone = removePressed && afterRemove.Length == beforeRemove.Length - 1 &&
                           !afterRemove.Any(p => string.Equals(p, beforeRemove[0], StringComparison.Ordinal));
        bool othersKept = removedGone;
        for (int i = 1; othersKept && i < beforeRemove.Length; i++)
            if (!afterRemove.Contains(beforeRemove[i], StringComparer.Ordinal)) othersKept = false;
        evidence.Line($"merge ui selftest: remove pressed={removePressed} removed='{removedName}' " +
                      $"gone={removedGone} othersKept={othersKept} " +
                      $"now=[{string.Join(",", afterRemove.Select(Path.GetFileName))}] ({removeDetail})");

        // ⑤b 「最後一份不能移除」：一直按到只剩一份，再按一次必須**被拒絕並留下提示**。
        //     這一條是「0 個輸入的合併」那道防線——它必須看得見，否則使用者會拿到一個空清單。
        //     刻意用**產品自己的列數**當迴圈條件（不是寫死 3 次），所以換一組夾具也成立。
        int guardSteps = 0;
        while (MergeUi.SelfTestOrder().Length > 1 && guardSteps < 64)
        {
            MergeUi.SelfTestRemoveRow(0, 5000, out _, lines.Enqueue);
            guardSteps++;
        }
        int leftCount = MergeUi.SelfTestOrder().Length;
        string[] lastOne = MergeUi.SelfTestOrder();
        bool lastRefused = MergeUi.SelfTestRemoveRow(0, 5000, out string lastDetail, lines.Enqueue);
        string afterLastNotice = MergeUi.SelfTestState();
        bool noticeSaysWhy = afterLastNotice.Contains("至少要留一份", StringComparison.Ordinal);
        bool stillOne = MergeUi.SelfTestOrder().Length == 1;
        evidence.Line($"merge ui selftest: the last file cannot be removed: left={leftCount} " +
                      $"pressed={lastRefused} stillOne={stillOne} noticeSaysWhy={noticeSaysWhy} " +
                      $"kept='{(lastOne.Length > 0 ? Path.GetFileName(lastOne[0]) : "(none)")}' " +
                      $"({lastDetail})");

        // ⑥ 確認：帶回的順序**必須就是目前畫面上的順序**（不是原始順序、也不是另一個東西）。
        string[] atConfirm = MergeUi.SelfTestOrder();
        MergeUi.SelfTestConfirm(5000, out string confirmDetail, lines.Enqueue);
        bool closed = WaitFor(() => !MergeUi.IsOpen, 3000);
        WaitFor(() => confirmed != null, 3000);
        string[]? handed = confirmed;
        bool handedMatches = handed != null && handed.Length == atConfirm.Length;
        if (handedMatches && handed != null)
        {
            for (int i = 0; i < atConfirm.Length; i++)
                if (!string.Equals(handed[i], atConfirm[i], StringComparison.Ordinal))
                {
                    handedMatches = false;
                    break;
                }
        }
        // 「被移除的那一份不在交出的順序裡」——這是移除語意的**終點**：光看視窗自己少了幾列不夠，
        // 要確認它真的沒有被送出去。
        bool removedNotHanded = handed != null &&
                                !handed.Any(p => string.Equals(p, beforeRemove[0], StringComparison.Ordinal));
        evidence.Line($"merge ui selftest: confirmed closed={closed} handedBack={handed != null} " +
                      $"matchesWhatWasShown={handedMatches} removedNotHanded={removedNotHanded} " +
                      $"handed=[{(handed == null ? "(nothing)" : string.Join(",", handed.Select(Path.GetFileName)))}] " +
                      $"shown=[{string.Join(",", atConfirm.Select(Path.GetFileName))}] ({confirmDetail})");

        // ⑦ 關掉之後可以再開，而且**取消不帶回任何順序**
        confirmed = null;
        MergeUi.Open(batch, (order, _) => confirmed = order, lines.Enqueue);
        bool reopened = WaitFor(() => MergeUi.IsOpen, 8000);
        bool reopenedPresent = false;
        string reopenDetail = "(never asked)";
        for (int attempt = 0; attempt < 20 && !reopenedPresent; attempt++)
        {
            reopenedPresent = MergeUi.SelfTestWindowPresent(3000, out reopenDetail, lines.Enqueue);
            if (!reopenedPresent) Thread.Sleep(250);
        }
        // 再開之後的初始順序必須回到**來源順序**（新的視窗、新的開始；不是沿用上一輪排好的東西）。
        string[] reopenedOrder = MergeUi.SelfTestOrder();
        bool reopenOrderIsSource = reopenedOrder.SequenceEqual(batch, StringComparer.Ordinal);
        evidence.Line($"merge ui selftest: the window can be opened again after a close: " +
                      $"reopened={reopened} windowPresent={reopenedPresent} " +
                      $"orderBackToSource={reopenOrderIsSource} state={MergeUi.SelfTestState()}");

        MergeUi.SelfTestCancel(5000, out string cancelDetail, lines.Enqueue);
        bool closedByCancel = WaitFor(() => !MergeUi.IsOpen, 3000);
        bool cancelGaveNothing = confirmed == null;
        evidence.Line($"merge ui selftest: cancel -> closed={closedByCancel} " +
                      $"orderHandedBack={confirmed != null} ({cancelDetail})");

        while (lines.TryDequeue(out string? buffered)) evidence.Line(buffered);

        bool ok = opened && windowPresent && initialMatches && singleInstance
                  && dragged && dragMatches && dragReturned
                  && removePressed && removedGone && othersKept
                  && leftCount == 1 && stillOne && noticeSaysWhy
                  && closed && handedMatches && removedNotHanded
                  && reopened && reopenedPresent && reopenOrderIsSource
                  && closedByCancel && cancelGaveNothing;
        evidence.Line($"merge ui selftest done: ok={ok} (opened={opened} windowPresent={windowPresent} " +
                      $"initialOrderMatches={initialMatches} singleInstance={singleInstance} " +
                      $"drag={dragged} dragSemantics={dragMatches} dragReturn={dragReturned} " +
                      $"remove={removePressed} removedGone={removedGone} othersKept={othersKept} " +
                      $"lastKept={leftCount == 1 && stillOne && noticeSaysWhy} " +
                      $"confirmedClosed={closed} confirmedMatches={handedMatches} " +
                      $"removedNotHanded={removedNotHanded} " +
                      $"reopened={reopened} reopenedPresent={reopenedPresent} " +
                      $"reopenOrderIsSource={reopenOrderIsSource} " +
                      $"cancelled={closedByCancel} cancelGaveNothing={cancelGaveNothing})");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 比例列上的每一顆鈕都寫著「某一個正規比例的 label」嗎？
    ///
    /// 這一條補的是「標籤變成空字串／變成按鈕 id／變成任何不在清單裡的東西」——
    /// 只比對**被選中那一顆**的話，其餘四顆壞掉（例如被清成空白）沒有任何一格看得見。
    /// 期望值仍然只有一個來源：<see cref="CropRatios.All"/> 的 label。
    /// </summary>
    private static bool AllLabelsAreExpected(string[] labels)
    {
        if (labels.Length != CropRatios.Presets.Length) return false;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (CropRatioPreset preset in CropRatios.All) allowed.Add(preset.Label);
        foreach (string label in labels)
            if (!allowed.Contains(label)) return false;
        return true;
    }

    /// <summary>
    /// 從自測狀態行裡刮出一個 <c>name=value</c>（找不到回 <c>(false, 0)</c>）。
    /// 這是**讀產品自己報的數字**，不是自己再算一次：狀態行由 <see cref="CropWindow.SelfTestState"/>
    /// 產生，而它裡面的 <c>ratio-target</c>／<c>ratio-actual</c> 就是引擎那一格用的同一組值。
    /// 在自測裡自己算一次比例會變成第二份規則——它與產品不一致時，那一格會紅在測試的算式上。
    /// </summary>
    private static (bool Found, double Value) StateNumber(string state, string name)
    {
        // ⚠ 尾巴的 `(?![0-9A-Za-z_-])` 不是裝飾：狀態行裡同時有 `ratio-target=…` 與
        // `ratio-target-ok=…`，少了這一段界線，`ratio-target` 會**先**命中後面那一個欄位，
        // 於是每一次都讀到 0（實測就是這樣：`targetsFlipped` 全紅、而產品的值其實是對的）。
        var match = System.Text.RegularExpressions.Regex.Match(
            state, System.Text.RegularExpressions.Regex.Escape(name) + @"=(-?[0-9]+(?:\.[0-9]+)?)(?![0-9A-Za-z_-])",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success) return (false, 0);
        return (true, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 從自測狀態行裡刮出矩形（影像像素）<c>rect=x,y,w,h</c>。找不到回 <c>(false, 0, 0)</c>。
    /// 用它而不是 <see cref="CropUi.SelfTestRectPx"/> 是因為狀態行與那一組數字是**同一次讀取**
    /// （兩次讀取之間矩形可能被別的東西改動，那一格就會紅在時序上）。
    /// </summary>
    private static (bool Found, int Width, int Height) StateRectPx(string state)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            state, @"rect=(-?\d+),(-?\d+),(\d+),(\d+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success) return (false, 0, 0);
        return (true,
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 「來源是直式還是橫式」——由**量到的尺寸**分類，不是由參數決定。
    /// 這一格的全部意義是「同一顆比例在兩種來源上方向一致」，所以來源自己的方向必須是**量出來的**
    /// （寫死的來源尺寸正是本專案假綠兩次的原因）。
    /// </summary>
    private static string OrientationOfSource(int width, int height) =>
        width > 0 && height > 0 ? (width > height ? "landscape" : width < height ? "portrait" : "square")
                                : "unknown";

    /// <summary>
    /// 一個比例在畫面座標上的**方向類別**：高 &gt; 寬＝直、寬 &gt; 高＝橫、其餘＝方。
    /// 用類別而不是數字，是因為要斷言的性質是「它朝著哪一邊」；數值本身由
    /// <c>ratio-target</c>／<c>rect</c> 那兩條斷言釘住（見 ④ 的說明）。
    /// </summary>
    private static string DirectionOfRatio(double ratio)
    {
        const double epsilon = 0.005;      // 1:1 只可能來自一個被取整過的像素框，容忍半個像素的抖動
        if (ratio > 1 + epsilon) return "wide";
        if (ratio < 1 - epsilon) return "tall";
        return "square";
    }

    /// <summary>
    /// 造一張「來源轉 90 度」的副本（**只為了自測的比例迴圈**）。
    ///
    /// 為什麼要真的產生一張圖而不是「假裝尺寸換過來」：比例迴圈走的是**真視窗、真解碼**
    /// （<see cref="CropUi.Open"/> → <c>ConversionEngine.DecodeFirstFrame</c>），而視窗報的
    /// 影像尺寸來自那個解碼結果。自測若自己去改一個尺寸變數，驗到的就是那個變數，不是產品。
    /// 用 PNG（無損）且不帶 EXIF：這一格要驗的是方向，不該被重新編碼或 EXIF 的變數干擾。
    /// 輸出落在系統暫存目錄，**呼叫端負責刪掉**；不碰來源、也不碰使用者的任何設定。
    /// </summary>
    private static bool TryCreateRotatedCopy(string source, out string path, out string detail)
    {
        path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                      $"{ProductInfo.Slug}-selftest-rotated-{Guid.NewGuid():N}.png");
        detail = "(not run)";
        try
        {
            (System.Windows.Media.Imaging.BitmapSource frame, _) = ConversionEngine.DecodeFirstFrame(source);
            int orientation = ConversionEngine.ReadOrientation(frame);
            if (orientation >= 2) frame = ConversionEngine.ApplyOrientation(frame, orientation);
            int beforeW = frame.PixelWidth, beforeH = frame.PixelHeight;
            var rotated = new System.Windows.Media.Imaging.TransformedBitmap(
                frame, new System.Windows.Media.RotateTransform(90));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rotated));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(stream);
            detail = $"{System.IO.Path.GetFileName(source)} {beforeW}x{beforeH} + 90 deg -> " +
                     $"{rotated.PixelWidth}x{rotated.PixelHeight} (EXIF={orientation}) at {path}";
            return File.Exists(path);
        }
        catch (Exception ex)
        {
            detail = $"could not build the rotated copy ({ex.GetType().Name}: {ex.Message})";
            return false;
        }
    }

    /// <summary>有界等待（自測用）。回傳「時間內變成 true」。</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs)    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (condition()) return true;
            }
            catch (Exception)
            {
                return false;
            }
            Thread.Sleep(25);
        }
        try { return condition(); } catch (Exception) { return false; }
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

        // 這一行的意思是「**檔案裡現在是什麼**」，不是「我剛剛在記憶體裡組出什麼」。
        // 兩者從 2026-10-06 起真的會不一樣：`Save` 會再跑一次 `Normalized`，而壓縮政策有一條
        // **跨欄位**的規則（選了「壓縮到」卻沒有可用的上限就退回「標準」），單一欄位的
        // `TryApply` 看不到它。只差一行的話，最後一句話會跟檔案互相矛盾——那比不說還糟。
        Settings written = next.Normalized(out _);
        evidence.Line($"settings set result: {SettingsStore.Describe(written)}");
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
            // 回傳值與 ResumeRefusals 一起寫進日誌：只讀 CaptureDisabled 分不出
            // 「守衛拒絕了」與「守衛根本沒被問到」（兩者都是 true）。
            bool resumed = guard.Resume();
            detector.SetPaused(false, "tray resume");
            evidence.Line($"tray: resume attempt accepted={resumed} guardResumeRefusals={guard.ResumeRefusals}");
        }

        // 真相只有一個：記下的是兩個擁有者**實際**的狀態，不是使用者剛按下的意圖。
        // 意圖與結果不同時（緊急停用之後要求繼續），日誌裡必須看得出差別。
        _capturePaused = guard.CaptureDisabled || detector.IsDisabled;
        evidence.Line($"tray: capture paused={_capturePaused} (requested={paused}) " +
                      $"(guard.CaptureDisabled={guard.CaptureDisabled}, detector.IsDisabled={detector.IsDisabled}, " +
                      $"killSwitchLatched={guard.KillSwitchLatched}, guardResumeRefusals={guard.ResumeRefusals})");
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
               guard.CaptureDisabled && detector.IsDisabled && guard.ResumeRefusals >= 1,
               $"guard.CaptureDisabled={guard.CaptureDisabled} detector.IsDisabled={detector.IsDisabled} " +
               $"latched={guard.KillSwitchLatched} guardResumeRefusals={guard.ResumeRefusals}");
        Report("the icon tells the truth after a refused resume", tray.IsPaused,
               $"tray.IsPaused={tray.IsPaused} tooltip='{tray.Tooltip}'");

        // 「托盤的開啟設定真的開得了設定頁」。自測裡的呼叫端不會把日誌寫進 Evidence
        // （那是設定頁那條執行緒的事），所以這裡直接問 SettingsUi 的狀態。
        var sw = Stopwatch.StartNew();
        while (!SettingsUi.IsOpen && sw.ElapsedMilliseconds < 5000) Thread.Sleep(25);
        bool uiOpen = SettingsUi.IsOpen;
        Report("the open-settings command reached the settings page", uiOpen,
               $"SettingsUi.IsOpen={SettingsUi.IsOpen} after {sw.ElapsedMilliseconds} ms");

        // 關窗走**使用者自己的**那條路：按下取消鈕（＝ SettingsWindow 自己的 Close()，X 也是同一條），
        // 不是產品的 SettingsUi.Close()。原本這裡只走產品那條，於是「視窗自己關掉之後」這一格
        // 完全沒有覆蓋——而使用者的 bug 正是從那個動作開始的。
        bool cancelClicked = SettingsUi.SelfTestCancel(5000, out string cancelDetail, evidence.Line);
        var swClose = Stopwatch.StartNew();
        while (SettingsUi.IsOpen && swClose.ElapsedMilliseconds < 3000) Thread.Sleep(25);
        Report("the settings page closes again (the process can exit)",
               cancelClicked && !SettingsUi.IsOpen,
               $"cancelClicked={cancelClicked} SettingsUi.IsOpen={SettingsUi.IsOpen} " +
               $"after {swClose.ElapsedMilliseconds} ms; {cancelDetail}");

        // 再走一次托盤的「開啟設定」命令，而且斷言它**真的開起來**（不是只斷言 IsOpen=false）。
        // 這一格就是使用者回報的 bug：他的日誌四次都只有 `tray: command -> open settings`、
        // 沒有 `but the settings page is already open`，也就是要求確實送進去了，視窗卻再也沒出現。
        tray.InvokeCommand(TrayCommand.OpenSettings);
        for (int i = 0; i < 5; i++) Native.PumpMessages();
        var swReopen = Stopwatch.StartNew();
        while (!SettingsUi.IsOpen && swReopen.ElapsedMilliseconds < 5000) Thread.Sleep(25);
        // 視窗存在也是斷言的一部分：只讀 IsOpen 會被「卡在 true 的舊快照」騙過去（見勾子的註解）。
        string reopenDetail = "(the window was never asked: the page never reported itself as open)";
        bool uiReopened = SettingsUi.IsOpen &&
                          SettingsUi.SelfTestWindowPresent(5000, out reopenDetail, evidence.Line);
        Report("the open-settings command opens the page AGAIN after it was closed", uiReopened,
               $"SettingsUi.IsOpen={SettingsUi.IsOpen} after {swReopen.ElapsedMilliseconds} ms; {reopenDetail}");
        SettingsUi.Close();     // 收尾：把視窗關掉，行程才結束得掉（這條路徑上面已經不用再驗）
        var swFinal = Stopwatch.StartNew();
        while (SettingsUi.IsOpen && swFinal.ElapsedMilliseconds < 3000) Thread.Sleep(25);

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
    ///
    /// <para>
    /// <paramref name="ackPath"/> 是**選用**的：給了它，第一次 <c>Enable()</c> 之後會按住那個值，
    /// 直到外部觀察者建立該檔案（＝它真的取樣到了那個值）或 <paramref name="ackTimeoutMs"/> 到期。
    /// 沒給＝完全不等待，行為與以前一字不差。理由（以及為什麼這不是「把斷言放寬」）見
    /// <see cref="Options.AutostartAckPath"/>。
    /// </para>
    /// </summary>
    internal static int RunAutostartProbe(string subkeyPath, string ackPath, int ackTimeoutMs, Evidence evidence)
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

        // 只有外部觀察者要求時才按住這個值（見 Options.AutostartAckPath）。按住期間那個值就在機碼裡，
        // 觀察者因此一定取樣得到；它取樣到之後建立 ack 檔，這裡才繼續往下（把值改成反向對照的髒值）。
        // 超時就照常往下走並留下證據——檢查會因此紅，而且紅得穩定。
        if (ackPath.Length > 0)
        {
            // 先清掉舊的：只有**這一輪**按住期間建立的 ack 才算數（上一輪留下來的檔案會讓會合變成
            // 「還沒開始就已經完成」）。
            try { File.Delete(ackPath); } catch (IOException) { /* 清不掉就照等，最壞情況是超時 */ }
            catch (UnauthorizedAccessException) { /* 同上 */ }
            var hold = System.Diagnostics.Stopwatch.StartNew();
            bool acked = false;
            while (hold.ElapsedMilliseconds < ackTimeoutMs)
            {
                if (File.Exists(ackPath)) { acked = true; break; }
                System.Threading.Thread.Sleep(1);
            }
            if (acked)
            {
                evidence.Line($"autostart: observer acknowledged the live value after {hold.ElapsedMilliseconds}ms " +
                              $"({ackPath}); releasing it now");
            }
            else
            {
                // 刻意**不**用 Report()：這一條講的是「外面的會合沒成就」，不是產品做錯了什麼。
                // 產品有沒有把值寫對，仍然由外面那一格（S15.16）判定；這裡紅會讓探測自己回非 0，
                // 反而把「觀察者沒取樣到」誤報成「探測失敗」。留一行證據，判定權留在外面。
                evidence.Line($"autostart: the outside observer never acknowledged the live value " +
                              $"(waited {hold.ElapsedMilliseconds}ms for {ackPath}); releasing it anyway");
            }
        }

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
    /// 待辦 5 · D1：`--pill-selftest`——把 <c>WM_LBUTTONUP</c> 送進**真實的
    /// <see cref="PillWindow.WndProc"/>**，不必動滑鼠、不必有畫面。
    ///
    /// 為什麼要有這一條，既然驗收已經有「真的合成點擊」：
    /// 那兩條證明的是不同的事。合成點擊證明「使用者的手點得到」（視窗存在、位置對、沒有穿透）；
    /// 這一條證明「點擊進到 WndProc 之後走的是展開那條路」（訊息分派、回呼、重畫、矩形更新）。
    /// 前者失敗＝容器錯了，後者失敗＝接線錯了，而兩者的修法完全不同。
    ///
    /// 每一條斷言都在日誌裡帶上**量到的數字**（矩形、樣式、hover、clicks），
    /// 讓「檢查綠燈」與「事實」是可以對照的兩件事，而不是同一句話。
    /// </summary>
    internal static int RunPillSelfTest(Options opt, Evidence evidence)
    {
        int failed = 0;
        int checks = 0;
        void Report(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failed++;
            evidence.Line($"pill: {(ok ? "PASS" : "FAIL")} {name}" +
                          (detail.Length > 0 ? $"  [{detail}]" : string.Empty));
        }

        using var pill = new PillWindow(evidence);
        using var progress = new ProgressPresenter(evidence, pill);

        // 假快照：一次部分失敗的轉檔（有失敗 → 藥丸是紅的、面板有兩列）。
        var snapshot = new ConversionSnapshot(9, 10, null, true, 9, 1,
            new[] { "broken.png 無法解碼", "locked.heic 檔案被鎖定" }, null,
            new[] { @"C:\tmp\broken.png", @"C:\tmp\locked.heic" });

        // 1) 樣式：該有的有、不該有的沒有。WS_EX_TRANSPARENT 若設起來，點擊會穿過去（＝D1 的白做）。
        Report("the pill window carries the required styles and NOT WS_EX_TRANSPARENT",
               (pill.ExStyle & Native.WS_EX_LAYERED) != 0 &&
               (pill.ExStyle & Native.WS_EX_NOACTIVATE) != 0 &&
               (pill.ExStyle & Native.WS_EX_TOOLWINDOW) != 0 &&
               (pill.ExStyle & Native.WS_EX_TRANSPARENT) == 0,
               $"exstyle=0x{pill.ExStyle:X}");

        // 畫出來，並記下自己的矩形。**這是後面全部檢查的前提**：沒有矩形就沒有可點的東西，
        // 而「沒有矩形」不會拋例外，只會讓後面每一條比對都失去意義（前提失敗要當場說出來）。
        //
        // ⚠ 錨點刻意**不放原點**：藥丸在 (600,400)、視窗因此落在 (516,373)。
        //   第一版用 (0,0) 當錨點，於是「螢幕座標」與「工作區座標」兩組數字在數值上很接近，
        //   把一個座標系混用的 bug（拿 lParam 的工作區座標去比視窗的螢幕矩形）藏了起來。
        //   自測的環境必須讓這種錯誤有機會現形，否則它只會在驗收環境裡才爆。
        progress.Start(600, 400);
        progress.Update(snapshot);
        Rectangle pillRect = progress.PillRect;
        Report("the pill was really drawn and knows its own rectangle (premise for every check below)",
               pillRect.Width > 0 && pillRect.Height > 0 && pill.IsVisible &&
               pillRect.X > 0 && pillRect.Y > 0,
               $"rect={Rx(pillRect)} visible={pill.IsVisible} frames={pill.Frames}");

        // 3) 一份幾何：**視窗矩形＝回報的矩形**，不是各自為政。
        Rectangle winBefore = WindowRect(pill.Handle);
        Report("the window rectangle equals the pill rectangle the presenter reported (one geometry, not two)",
               winBefore.Width > 0 && winBefore == pillRect,
               $"window={Rx(winBefore)} reported={Rx(pillRect)}");

        // 4) 真實的 WM_LBUTTONUP → 展開。用 SendMessage（同步）＋ PumpMessages：
        //    呼叫端不在訊息迴圈裡，SendMessage 才保證「送達」與「處理完」是同一件事。
        //
        //    ⚠ lParam 是**工作區座標**（相對於視窗左上角），不是螢幕座標。這裡刻意換算過，
        //      而不是「反正送螢幕座標也會過」——那個「也會過」正是靠座標系混用在原點附近
        //      偶然成立才有的假象。
        int centerX = pillRect.X + pillRect.Width / 2;
        int centerY = pillRect.Y + pillRect.Height / 2;
        int clientX = centerX - winBefore.X, clientY = centerY - winBefore.Y;
        Native.SendMessageW(pill.Handle, Native.WM_LBUTTONUP, IntPtr.Zero, LParam(clientX, clientY));
        Native.PumpMessages();
        Rectangle expanded = progress.PillRect;
        Rectangle winExpanded = WindowRect(pill.Handle);
        Report("WM_LBUTTONUP through the real WndProc expands the list (spec 7.3)",
               progress.IsExpanded && pill.Clicks == 1,
               $"expanded={progress.IsExpanded} clicks={pill.Clicks} " +
               $"clientSent=({clientX},{clientY}) clientArea={pill.ClientRect.Width}x{pill.ClientRect.Height}");
        Report("the expanded state is BIGGER than the pill and the window grew with it",
               expanded.Height > pillRect.Height && expanded.Width >= pillRect.Width &&
               winExpanded == expanded && winExpanded.Height > winBefore.Height,
               $"pill={Rx(pillRect)} expanded={Rx(expanded)} window={Rx(winExpanded)} " +
               $"windowH {winBefore.Height}->{winExpanded.Height}");

        // 5) 展開後的畫面真的畫得出來，而且**藥丸還在裡面、面板接在它下面**（合成不是把藥丸丟掉）。
        var composite = progress.RenderExpandedComposite();
        try
        {
            // 藥丸在螢幕上的矩形，由合成結果自己推出來（合成圖的左上角 ＋ 藥丸在圖裡的位置）。
            var pillOnScreen = new Rectangle(composite.At.X + composite.PillInBitmap.X,
                                            composite.At.Y + composite.PillInBitmap.Y,
                                            composite.PillInBitmap.Width, composite.PillInBitmap.Height);
            Report("the expanded composite really has the panel below the pill (one bitmap, pill intact)",
                   composite.Bitmap.Width == expanded.Width &&
                   composite.Bitmap.Height == expanded.Height &&
                   !composite.PillInBitmap.IsEmpty &&
                   composite.PillInBitmap.Y == 0 &&
                   composite.PillInBitmap.Height < composite.Bitmap.Height &&
                   composite.PanelRowsLaidOut.Count >= 2,
                   $"bitmap={composite.Bitmap.Width}x{composite.Bitmap.Height} " +
                   $"pillInBitmap={Rx(composite.PillInBitmap)} panelRows={composite.PanelRowsLaidOut.Count}");
            // 兩份幾何（畫面的 vs 合成的）必須講同一件事：藥丸的水平中心就是輪盤中心，
            // 而展開前的藥丸仍然完整地位於合成圖的頂端。**座標必須相等，不是差不多**
            // （展開後 x 會變，那是整張圖變寬造成的，不是藥丸自己跑掉）。
            Report("the composite agrees with the screen geometry (pill centred on the anchor, same top edge)",
                   pillOnScreen.Width == pillRect.Width && pillOnScreen.Height == pillRect.Height &&
                   pillOnScreen.X + pillOnScreen.Width / 2 == centerX &&
                   pillOnScreen.Y == expanded.Y && pillOnScreen.Bottom <= expanded.Bottom,
                   $"pillOnScreen={Rx(pillOnScreen)} clicked=({centerX},{centerY}) " +
                   $"expanded={Rx(expanded)} pillBefore={Rx(pillRect)}");

            // 展開狀態的證據落地（PNG ＋ JSON ＋ 螢幕截圖）。**此刻它正顯示在畫面上**，
            // 所以截圖抓到的就是使用者看到的那一塊。
            DumpExpandedEvidence(opt, evidence, progress, composite, pill, expanded);
        }
        finally { composite.Bitmap.Dispose(); }

        // 6) 點在視窗**外**不得展開，而且**不得被計為一次點擊**。
        //    這一條同時是「點在藥丸外面不會展開」（規格明列的失敗模式）與一個更基本的事實：
        //    訊息是可以被送進來的，所以命中判定必須在視窗自己身上成立，不能只靠「滑鼠本來就點不到」。
        //
        //    ⚠ 座標要用**現在**的矩形：此刻是展開狀態，矩形比藥丸大得多，拿藥丸的左上角 -40
        //      仍然落在展開後的視窗裡（第一版就是這樣，於是斷言量到的是「展開著」而不是別的）。
        //      座標系是工作區座標，所以「外面」是負值——負值是最明確的「不在這塊矩形上」。
        Rectangle current = progress.PillRect;
        int outsideX = -40, outsideY = current.Height / 2;
        Native.SendMessageW(pill.Handle, Native.WM_LBUTTONUP, IntPtr.Zero, LParam(outsideX, outsideY));
        Native.PumpMessages();
        Report("a click reported OUTSIDE the client area neither toggles the list nor counts as a click",
               progress.IsExpanded && pill.Clicks == 1 && progress.PillRect == current &&
               !pill.HitTestClientPoint(outsideX, outsideY),
               $"outsideClient=({outsideX},{outsideY}) insideClientArea={pill.HitTestClientPoint(outsideX, outsideY)} " +
               $"expanded={progress.IsExpanded} clicks={pill.Clicks} rect={Rx(progress.PillRect)}");

        // 7) hover：進入／離開（D2）。離開的事件由系統送 WM_MOUSELEAVE，這裡直接送同一則訊息。
        //    座標同樣用**現在**的矩形中心（此刻是展開後的視窗），換算成工作區座標。
        //
        //    ⚠ 這裡量的是**訊息路徑**，不是真的把游標移過去：這個自測沒有動滑鼠，所以
        //      `WM_MOUSEMOVE` 一處理完，系統就會（因為實體游標不在視窗上）排入一則**真實的**
        //      `WM_MOUSELEAVE`。因此「進入」只能在 SendMessage 回來的當下讀，
        //      PumpMessages 之後讀到的已經是被系統離場的那一份。
        //      「游標真的停在藥丸上就不淡出」是 --pill-interactive ＋ 合成輸入那一條在驗的
        //      （那裡的 hover 由真正的滑鼠移動產生）。
        int hoverClientX = current.Width / 2, hoverClientY = current.Height / 2;
        Native.SendMessageW(pill.Handle, Native.WM_MOUSEMOVE, IntPtr.Zero, LParam(hoverClientX, hoverClientY));
        bool pillSawEnter = pill.IsHovered;
        bool presenterSawEnter = progress.IsHovered;
        Native.PumpMessages();
        bool survivedPump = pill.IsHovered;
        Native.SendMessageW(pill.Handle, Native.WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
        Native.PumpMessages();
        evidence.Line($"pill: hover probe at client ({hoverClientX},{hoverClientY}) " +
                      $"insideClientArea={pill.HitTestClientPoint(hoverClientX, hoverClientY)} " +
                      $"afterSend pill={pillSawEnter} presenter={presenterSawEnter}; " +
                      $"afterPump pill={survivedPump}; " +
                      $"afterLeave pill={pill.IsHovered} presenter={progress.IsHovered} " +
                      "(the pump clears it because the PHYSICAL cursor is not over the window)");
        Report("WM_MOUSEMOVE marks the pill hovered and WM_MOUSELEAVE clears it (D2's only mechanism)",
               pillSawEnter && presenterSawEnter && !pill.IsHovered && !progress.IsHovered,
               $"afterMove: pill={pillSawEnter} presenter={presenterSawEnter}; " +
               $"afterLeave: pill={pill.IsHovered} presenter={progress.IsHovered}");

        // 8) 再次點擊＝收合，而且視窗縮回藥丸大小（不是卡在大的）。
        Native.SendMessageW(pill.Handle, Native.WM_LBUTTONUP, IntPtr.Zero, LParam(clientX, clientY));
        Native.PumpMessages();
        Rectangle winCollapsed = WindowRect(pill.Handle);
        Report("a second click collapses again and the window shrinks back to the pill",
               !progress.IsExpanded && pill.Clicks == 2 && winCollapsed == progress.PillRect &&
               progress.PillRect.Height == pillRect.Height,
               $"expanded={progress.IsExpanded} clicks={pill.Clicks} window={Rx(winCollapsed)}");

        evidence.Line($"pill: selftest done: checks={checks} failed={failed} ok={failed == 0} " +
                      $"hwnd=0x{pill.Handle.ToInt64():X} exstyle=0x{pill.ExStyle:X}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 待辦 5 · D1＋D2：`--pill-interactive &lt;ms&gt;`——把可點的藥丸（含失敗清單）顯示出來並活著
    /// N 毫秒，讓**外部**注入真實的合成輸入，然後自然淡出。
    ///
    /// 為什麼要有這個模式：驗收要量的兩件事（「點在藥丸上會不會展開」「游標停在上面會不會
    /// 阻止淡出」）都必須在藥丸還活著的時候從外面動手。讓生命週期變成一個明確的參數，
    /// 檢查才不會變成「剛好在那幾秒內插進去」的時間競賽。
    ///
    /// 它**只提供畫面與生命週期**：展開、淡出、hover 全部走正式路徑的同一組物件與同一組規則
    /// （<see cref="PillWindow"/> ＋ <see cref="ProgressPresenter"/>）。
    /// </summary>
    internal static int RunPillInteractive(Options opt, Evidence evidence)
    {
        using var pill = new PillWindow(evidence);
        using var progress = new ProgressPresenter(evidence, pill);

        // 位置取工作區中心：外部的驗收要能在**點下去之前**確認「那個座標上真的有我們的視窗」
        // （否則它可能會點到桌面上隨便一個東西，而那種檢查會以錯誤的理由通過或失敗）。
        Rectangle work = ProgressPresenter.WorkAreaAt(0, 0);
        int x = work.Width > 0 ? work.Left + work.Width / 2 : 800;
        int y = work.Height > 0 ? work.Top + work.Height / 2 : 400;

        // 合成的失敗列。刻意**不用真實轉檔**：這個模式要驗的是容器與生命週期，
        // 真實轉檔的失敗路徑已經有 `--wheel-payload` 那條端到端在驗，兩者不該混在一起。
        var rows = new List<ConversionFailureRow>
        {
            new("corrupt.png", "無法解碼"),
            new("locked.heic", "檔案被鎖定"),
        };
        var snapshot = new ConversionSnapshot(rows.Count, rows.Count, null, true, 0, rows.Count,
            rows.Select(r => r.Text).ToArray(), null,
            rows.Select(r => @"C:\tmp\" + r.FileName).ToArray());

        progress.Start(x, y);
        // 先 Update 再（**不**）展開：藥丸從**收合**開始，這樣「展開」就只可能來自
        // 外部的真實點擊——否則驗收量到的「展開著」是這個模式自己造成的，
        // 而那種檢查會對著一個沒有點過任何東西的行程說「點擊有效」。
        progress.Update(snapshot);
        Rectangle rect = progress.PillRect;
        evidence.Line($"pill interactive: hwnd=0x{pill.Handle.ToInt64():X} exstyle=0x{pill.ExStyle:X} " +
                      $"workArea={work.X},{work.Y},{work.Width},{work.Height} anchor=({x},{y})");
        evidence.Line($"pill interactive: shown rect={Rx(rect)} expanded={progress.IsExpanded} " +
                      $"holdMs={progress.HoldFor()} fadeMs={opt.PillInteractiveMs} " +
                      $"(collapsed on purpose: any expansion from here on can only come from the outside)");
        evidence.Line($"pill interactive: clickState before any input: expanded={progress.IsExpanded} " +
                      $"windowH={WindowRect(pill.Handle).Height} clicks={pill.Clicks}");

        var sw = Stopwatch.StartNew();
        int ticks = 0;
        bool loggedExpanded = progress.IsExpanded;
        bool loggedHover = false;
        while (sw.ElapsedMilliseconds < opt.PillInteractiveMs)
        {
            while (Native.PeekMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
                ticks++;
            }
            if (progress.IsExpanded != loggedExpanded)
            {
                loggedExpanded = progress.IsExpanded;
                // 這一行是驗收的**因果證據**：它是在外部注入之後才出現的，
                // 內容包含展開狀態、視窗實際高度與點擊次數——三者必須一起變。
                evidence.Line($"pill interactive: expanded={loggedExpanded} clicks={pill.Clicks} " +
                              $"windowH={WindowRect(pill.Handle).Height} " +
                              $"at {sw.ElapsedMilliseconds} ms rect={Rx(progress.PillRect)}");
            }
            if (pill.IsHovered != loggedHover)
            {
                loggedHover = pill.IsHovered;
                evidence.Line($"pill interactive: hover={loggedHover} at {sw.ElapsedMilliseconds} ms");
            }
            Thread.Sleep(10);
        }

        evidence.Line($"pill interactive: loop done after {sw.ElapsedMilliseconds} ms ticks={ticks} " +
                      $"visible={pill.IsVisible} expanded={progress.IsExpanded} hover={pill.IsHovered} " +
                      $"clicks={pill.Clicks} frames={pill.Frames} rect={Rx(progress.PillRect)}");

        // 收尾改成「等它淡出」：這樣驗收即使沒有注入任何東西，也會看到正式路徑的自然淡出，
        // 而注入過的行程則會看到它自己的 hover 把它留在畫面上（那正是 D2 要的）。
        bool faded = PumpUntil(() => !pill.IsVisible, 20000);
        evidence.Line($"pill interactive: fadedOut={faded} visible={pill.IsVisible} " +
                      $"expanded={progress.IsExpanded} clicks={pill.Clicks} " +
                      $"total={sw.ElapsedMilliseconds} ms");
        return 0;
    }

    /// <summary>把矩形寫成人看得懂也 regex 得到的一行（驗收直接讀日誌）。</summary>
    private static string Rx(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    /// <summary>
    /// 展開狀態的三份證據：PNG（含 alpha 的版本才能真正取樣文字墨水）、JSON（交給 GDI+ 的字串），
    /// 以及**畫面上那一塊的截圖**。
    ///
    /// 為什麼截圖是必要的，既然已經有 PNG：PNG 是**我們自己畫的圖**，它證明不了那張圖
    /// 有沒有出現在螢幕上（layered window 沒被合成、被別的 topmost 蓋住、位置算錯——
    /// 這三種失效都會留下一張完美的 PNG）。截圖是系統合成的結果，那才是使用者看到的東西。
    /// 抓不到時大聲說出來，不留一個空檔案讓人以為驗過了。
    /// </summary>
    private static void DumpExpandedEvidence(Options opt, Evidence evidence, ProgressPresenter progress,
                                            ProgressPresenter.ExpandedComposite composite, PillWindow pill,
                                            Rectangle expanded)
    {
        string? dir = null;
        foreach (string p in new[] { opt.PillSelfTestDump, opt.PillSelfTestDumpRaw, opt.PillSelfTestShot })
            if (!string.IsNullOrEmpty(p)) { dir = Path.GetDirectoryName(Path.GetFullPath(p)); break; }
        if (dir == null && !string.IsNullOrEmpty(opt.PillSelfTestDumpJson))
            dir = Path.GetDirectoryName(Path.GetFullPath(opt.PillSelfTestDumpJson));
        if (dir != null)
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { evidence.Line($"pill dump: cannot create {dir}: {ex.Message}"); }
        }

        if (!string.IsNullOrEmpty(opt.PillSelfTestDump))
        {
            try
            {
                using var flat = new Bitmap(composite.Bitmap.Width, composite.Bitmap.Height,
                                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.FromArgb(255, 32, 34, 38));
                    g.DrawImageUnscaled(composite.Bitmap, 0, 0);
                }
                flat.Save(opt.PillSelfTestDump, System.Drawing.Imaging.ImageFormat.Png);
                evidence.Line($"pill dump: wrote {opt.PillSelfTestDump}");
            }
            catch (Exception ex)
            {
                evidence.Line($"pill dump: FAILED to write {opt.PillSelfTestDump}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (!string.IsNullOrEmpty(opt.PillSelfTestDumpRaw))
        {
            try
            {
                composite.Bitmap.Save(opt.PillSelfTestDumpRaw, System.Drawing.Imaging.ImageFormat.Png);
                evidence.Line($"pill dump: wrote {opt.PillSelfTestDumpRaw} (alpha preserved)");
            }
            catch (Exception ex)
            {
                evidence.Line($"pill dump: FAILED to write {opt.PillSelfTestDumpRaw}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 這一行同時是「dump 的尺寸」與「畫面幾何」的來源：驗收用它與 PNG 的實際像素比對。
        evidence.Line($"pill dump line: variant=expanded width={composite.Bitmap.Width} " +
                      $"height={composite.Bitmap.Height} rows={progress.ExpandedRowCount} " +
                      $"laidOutRows={composite.PanelRowsLaidOut.Count} " +
                      $"pillInBitmap={Rx(composite.PillInBitmap)} window={Rx(expanded)} " +
                      $"at={Rx(new Rectangle(composite.At, composite.Bitmap.Size))}");

        if (!string.IsNullOrEmpty(opt.PillSelfTestDumpJson))
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('{');
            sb.Append($"\"width\":{composite.Bitmap.Width},\"height\":{composite.Bitmap.Height},");
            sb.Append($"\"at\":[{composite.At.X},{composite.At.Y}],");
            sb.Append($"\"pillInBitmap\":[{composite.PillInBitmap.X},{composite.PillInBitmap.Y}," +
                      $"{composite.PillInBitmap.Width},{composite.PillInBitmap.Height}],");
            sb.Append("\"panelRows\":[");
            for (int i = 0; i < composite.PanelRowsLaidOut.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string row = composite.PanelRowsLaidOut[i];
                sb.Append($"{{\"index\":{i},\"laidOut\":{Json(row)},\"laidOutLength\":{row.Length}," +
                          $"\"laidOutWidth\":{MeasureTextForDump(row)}}}");
            }
            sb.Append("]}");
            try
            {
                File.WriteAllText(opt.PillSelfTestDumpJson, sb.ToString(), new System.Text.UTF8Encoding(false));
                evidence.Line($"pill dump json: wrote {opt.PillSelfTestDumpJson} " +
                              $"(panelRows={composite.PanelRowsLaidOut.Count})");
            }
            catch (Exception ex)
            {
                evidence.Line($"pill dump json: FAILED to write {opt.PillSelfTestDumpJson}: " +
                              $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(opt.PillSelfTestShot))
        {
            // 讓系統有機會把這一幀合成上去：我們剛剛才 UpdateLayeredWindow 完。
            for (int i = 0; i < 20; i++) { Native.PumpMessages(); Thread.Sleep(10); }
            EvidenceScreenGrab(evidence, opt.PillSelfTestShot, expanded, pill);
        }
    }

    /// <summary>
    /// 抓螢幕上那塊矩形。**抓不到就說抓不到**：空檔案會讓「畫面上的確有面板」變成一句謊。
    /// </summary>
    private static void EvidenceScreenGrab(Evidence evidence, string path, Rectangle rect, PillWindow pill)
    {
        try
        {
            using var bmp = new Bitmap(rect.Width, rect.Height,
                                       System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            // `hitTest=` 是「那塊矩形上現在是不是我們的視窗」——截圖若抓到別的視窗，
            // 後面的斷言會以錯誤的理由通過或失敗，所以把這個事實一起寫出來。
            int cx = rect.Left + rect.Width / 2, cy = rect.Top + rect.Height / 2;
            evidence.Line($"pill screen grab: wrote {path} rect={Rx(rect)} " +
                          $"hitTest={pill.HitTest(cx, cy)} windowVisible={pill.IsVisible}");
        }
        catch (Exception ex)
        {
            evidence.Line($"pill screen grab: FAILED for {path}: {ex.GetType().Name}: {ex.Message} " +
                          "(the on-screen pixels could NOT be sampled in this session)");
        }
    }

    /// <summary>滑鼠訊息的 lParam：x 在低 16 位、y 在高 16 位。</summary>
    private static IntPtr LParam(int x, int y) =>
        new((y & 0xFFFF) << 16 | (x & 0xFFFF));

    /// <summary>
    /// 視窗在螢幕上的實際矩形。驗收要斷言「視窗矩形＝回報的矩形」（一份幾何），
    /// 而**唯一客觀的來源是作業系統**，不是我們自己的變數。
    /// </summary>
    private static Rectangle WindowRect(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out Native.RECT r)) return Rectangle.Empty;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
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
        // 2026-10-14: 這一行原本是「宣告矩陣」的**第二份複本**，而它是唯一會說謊的那一份——
        // PDF 加入宣告矩陣之後，這裡仍然只列五個，於是這一條自我檢查 FAIL，
        // 而 offered 其實完全正確（見 CodecRegistry.DeclaredOutputs 的說明）。
        // 修法是問唯一擁有者，而不是把 PDF 也抄進來（抄進來只是把第二份複本養大）。
        IReadOnlyList<string> declared = CodecRegistry.DeclaredOutputs;
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
    /// 固定幫浦 <paramref name="ms"/> 毫秒，並在期間持續回報 <paramref name="observed"/>（給清理路徑用：
    /// 那時候已經沒有條件可等，但鉤子回呼仍然需要有人幫浦訊息才跑得起來）。
    /// </summary>
    private static bool PumpFor(int ms, Func<bool> observed)
    {
        var sw = Stopwatch.StartNew();
        bool seen = false;
        while (sw.ElapsedMilliseconds < ms)
        {
            while (Native.PeekMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
            if (observed()) seen = true;
            Thread.Sleep(5);
        }
        return seen;
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
