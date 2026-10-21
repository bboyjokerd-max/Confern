using System.Diagnostics;
using System.IO.Pipes;

namespace Sidecar;

/// <summary>
/// 進度／結果的**不可變快照**：背景讀取執行緒整份換掉、UI 執行緒整份讀取，
/// 因此 UI 永遠看到一致的一組數字（不會出現「done 是新的、total 是舊的」）。
/// </summary>
internal sealed record ConversionSnapshot(int Done, int Total, string? Current, bool Finished,
                                          int Ok, int Failed, IReadOnlyList<string> Failures, string? Error,
                                          IReadOnlyList<string>? FailureFiles = null)
{
    public static readonly ConversionSnapshot Idle =
        new(0, 0, null, true, 0, 0, Array.Empty<string>(), null, Array.Empty<string>());

    /// <summary>
    /// 讀 QR 的產物（`op=read-qr` 專用，2026-10-07 切片 2 新增）。
    ///
    /// **為什麼要進快照**：<c>QrText</c> 是**唯一一個不是檔案的產物**，所以它沒有
    /// <c>OutputPath</c> 可以走——而 UI 這邊「拿到結果」的唯一通道就是這份不可變快照
    /// （見 <see cref="ConversionRunner.Snapshot"/>）。把它放在快照上，等於讓它與
    /// 「成功／失敗、進度、失敗清單」同一條路徑出去；另外開一條事件或第二個通道
    /// 就是讓第二個擁有者去回答同一件事（本專案對這種分歧的代價有紀錄）。
    ///
    /// <c>null</c> 的缺席語意＝「這個結果沒有文字」：其餘每一個 op 的行為，也與
    /// worker 端 <c>WorkerMessage.text</c> 的缺席語意一對一（<c>WhenWritingNull</c>）。
    /// </summary>
    public string? QrText { get; init; }

    /// <summary>
    /// 最後一個結果的**詳細原因**（`WorkerMessage.detail`，2026-10-07 隨工具 `merge-pdf` 進快照）。
    ///
    /// **為什麼需要它**：`Failures` 裡放的是規格 §7.1 的**使用者訊息**（`fx-broken.pdf（第 2 個檔）
    /// 無法合併這個 PDF`）——那是一句給人看的話，而它刻意不含 stage 與例外型別。診斷要斷言的是
    /// 「明確說出是哪個檔、**為什麼**」（使用者 2026-10-07 的原話），而「為什麼」＝
    /// `stage AddPage (page 1/1): NullReferenceException: ...` 這一串，它**原本只寫在 worker 的
    /// 日誌裡，過不了管道**（實測：`--merge-pdf` 的日誌看不到它）。
    /// 少了它，驗收就只能憑「訊息裡有檔名」推論失敗原因，那正是「只相信 ok=True」的另一種寫法。
    ///
    /// 缺席語意與 <see cref="QrText"/> 一對一：`null` ＝「這個結果沒有詳細原因」，
    /// 其餘每一個 op 的行為都不變。
    /// </summary>
    public string? LastDetail { get; init; }

    public bool Running => !Finished;

    /// <summary>輪盤收起處顯示的**那一行**（規格 §4.4／§7.3）。</summary>
    public string Line
    {
        get
        {
            if (Error != null) return Error;
            if (!Finished) return Total > 0 ? $"{Done}/{Total}" : "…";
            // 規則只有一份：ConversionSummary.ComposeLine。這裡曾經自己再拼一次
            // `Failed == 0 ? ✓ : ✓ N / ✗ M`，於是規格 §7.1 的失敗原因（「無法解碼」）
            // 永遠到不了畫面（2026-10-01 由人工測試抓到）。
            return ConversionSummary.ComposeLine(Ok, Failed, Failures);
        }
    }

    /// <summary>
    /// 失敗清單的列資料（規格 §7.3）——**這一條路徑（UI／管道）的單一擁有者**。
    ///
    /// 為什麼要在這裡拆一次：管道另一端送過來的是規格 §7.1 的**訊息**（`broken.png 無法解碼`），
    /// 檔名被包在字串裡。若讓繪製端自己切字串，那個「切法」就變成第二份規則，
    /// 而它一旦與 <see cref="ConversionResult.DescribeMessage"/> 的組法不一致，清單每列的檔名欄
    /// 會默默顯示成錯的東西（不會拋例外，也不會有人發現）。拆法放在
    /// <see cref="ProgressDetailRows.FromMessage"/>，與 <see cref="ConversionSummary.FailureRows"/> 共用。
    /// </summary>
    public IReadOnlyList<ConversionFailureRow> DetailRows
    {
        get
        {
            var rows = new List<ConversionFailureRow>(Failures.Count);
            for (int i = 0; i < Failures.Count; i++)
                rows.Add(ProgressDetailRows.FromMessage(
                    FailureFiles != null && i < FailureFiles.Count ? FailureFiles[i] : null, Failures[i]));
            return rows;
        }
    }
}

/// <summary>
/// UI 這一側的轉檔客戶端：建管道 → 起 worker 行程 → 送工作 → 收進度與結果。
///
/// 三件**不可以**發生的事（每一件都有對應處置）：
/// ① worker 起不來 → 回報失敗，UI 照常運作（不崩潰、不卡住）；
/// ② worker 中途死掉／不回話 → 逾時後把剩下的檔案標成失敗並把 worker 收掉；
/// ③ 協定版本不合 → 關掉重啟（版本化協定的意義就在這裡）。
/// </summary>
internal sealed class ConversionClient : IDisposable
{
    private const int ConnectTimeoutMs = 8000;
    private const int InactivityTimeoutMs = 30000;      // 規格 §9.3：單檔轉檔的等待上限

    private readonly string _exePath;
    private readonly Action<string> _log;
    private readonly string _pipeName = WorkerProtocol.NewPipeName();
    private readonly Stopwatch _sinceLastMessage = Stopwatch.StartNew();

    private NamedPipeServerStream? _pipe;
    private PipeLink? _link;
    private Process? _worker;
    private volatile ConversionSnapshot _snapshot = ConversionSnapshot.Idle;
    private volatile bool _stop;
    private Thread? _watchdog;

    public ConversionClient(string exePath, Action<string> log)
    {
        _exePath = exePath;
        _log = log;
    }

    public ConversionSnapshot Snapshot => _snapshot;

    /// <summary>
    /// 起 worker 並送出工作。回傳 false 代表連 worker 都沒起來（錯誤放在 <paramref name="error"/>）。
    ///
    /// <paramref name="toolOp"/> 為 null 時＝格式轉換（**既有行為逐位元不變**）；
    /// 非 null 時走工具（例：<see cref="WorkerProtocol.OpRotate90"/>），此時
    /// <paramref name="targetFormat"/> 沒有意義——**工具自己決定輸出格式**。
    ///
    /// <paramref name="outputDir"/> 為 **null＝寫在來源檔旁邊**（＝不指定時的行為逐位元不變）；
    /// 非 null＝寫進那個資料夾，不存在時由 <see cref="NamingPolicy"/> 回退到來源旁邊。
    ///
    /// ⚠ <paramref name="outputDir"/> 與 <paramref name="toolOp"/> **都是 `string?`**：
    /// 兩者位置互換**編譯照過**，錯的是行為（把 `rotate90` 當成資料夾路徑、把資料夾路徑當成
    /// 工具名稱）。所以呼叫端一律用**具名引數**指名這兩者，不靠位置。
    ///
    /// <paramref name="compression"/>（2026-10-06 M3 第三顆扇形）：`op=compress` 的等級政策，
    /// **其餘 op 一律傳 null**——null 時那四個欄位不會進訊息（<c>WhenWritingNull</c>），
    /// 所以既有的封包一個位元組都沒變。政策的擁有者是 <see cref="Settings.ToCompressionPolicy"/>，
    /// 這裡只搬運，不在 UI 端先算品質：真正要知道「每個檔是什麼格式」的是 worker。
    ///
    /// <paramref name="removeBgModel"/>（2026-10-08 M3 第七顆扇形）：`op=remove-bg` 的**模型檔
    /// 路徑**，**其餘 op 一律傳 null**（同一條規矩：null 不進訊息，既有封包逐位元不變）。
    /// ⚠ 呼叫端要送**絕對路徑**：相對路徑在 worker 那一側會對上**它自己的**工作目錄，
    /// 那不是 UI 解析出來的那一個（症狀是「UI 找到模型、worker 找不到」）。
    ///
    /// <paramref name="removeBgRefine"/>：`op=remove-bg` 的**邊緣精修模式**（正規值見
    /// <see cref="EdgeRefine.Modes"/>），其餘 op 一律傳 null。⚠ **預設值也要傳 null**：
    /// null 在 worker 那一側的意思正是「用預設（開）」，所以送 null 與送 "guided" 行為相同，
    /// 但只有送 null 能讓既有封包逐位元不變。只有明確關掉才送 "off"。
    /// </summary>
    public bool Start(string[] sourcePaths, string targetFormat, int quality, string? outputDir,
                      string? toolOp, out string? error, CompressionPolicy? compression = null,
                      CropSpec? crop = null, string? removeBgModel = null,
                      string? removeBgRefine = null,
                      string? pageSize = null)
    {
        error = null;
        // **非同步管道**（PipeOptions.Asynchronous）：兩端都只用非同步 I/O。
        // 先前用同步管道、一個執行緒阻塞讀、另一個執行緒寫，會互相卡死
        // （症狀：worker 的 ready 要等到行程被殺才送達，每一批都逾時）。詳見 PipeLink 的說明。
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                                          PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var startInfo = new ProcessStartInfo(_exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add(_pipeName);
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(WorkerLogPath());

        try
        {
            _worker = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            _log($"worker: could not start '{_exePath}': {error}");
            return false;
        }
        if (_worker == null)
        {
            error = "Process.Start returned null";
            return false;
        }

        // worker 連上來才送工作：連不上＝沒有可信的執行環境，回報而不是硬送
        Task connect = _pipe.WaitForConnectionAsync();
        if (!connect.Wait(ConnectTimeoutMs))
        {
            error = $"worker did not connect within {ConnectTimeoutMs} ms";
            _log($"worker: {error}");
            KillWorker();
            return false;
        }

        // 管道的一切都交給 PipeLink：一個非同步讀取任務 + 一個非同步寫入任務，每方向單一擁有者
        _link = new PipeLink(_pipe, _log);
        _link.Received += OnMessage;
        _link.Closed += () =>
        {
            if (!_snapshot.Finished)
            {
                _log($"worker: pipe closed before summary (done={_snapshot.Done}/{_snapshot.Total})");
                ConversionSnapshot current = _snapshot;
                _snapshot = new ConversionSnapshot(current.Done, current.Total, null, true,
                                                   current.Ok, current.Failed, current.Failures,
                                                   UiStrings.Get("svc.interrupted"), current.FailureFiles)
                {
                    // 管道在 summary 之前就斷了也要留住已經收到的產物（理由見 Summary 那一格）。
                    QrText = current.QrText,
                    LastDetail = current.LastDetail
                };
            }
        };
        _link.Start();
        string op = toolOp ?? WorkerProtocol.OpConvert;
        _link.Send(new WorkerMessage
        {
            type = WorkerMessageTypes.Job,
            jobId = 1,
            sourcePaths = sourcePaths,
            targetFormat = targetFormat,
            quality = quality,
            op = op,
            // 照 op 的作法：null 不進訊息（JsonIgnoreCondition.WhenWritingNull），
            // 而 worker 收到 null 的意思正是「來源檔旁邊」——缺席語意與意圖一致。
            outputDir = outputDir,
            // 壓縮政策（只有 op=compress 會填）。四個欄位一起送／一起不送：一個只有一半的
            // 政策在 worker 端會變成「模式是壓縮到、上限卻缺席」的組合，那正是要防的東西。
            compressMode = compression?.Mode,
            compressTargetBytes = compression?.TargetBytes ?? 0,
            standardJpgQuality = compression?.StandardJpgQuality ?? 0,
            standardWebpQuality = compression?.StandardWebpQuality ?? 0,
            // 裁切矩形（只有 op=crop 會填）。五個欄位一起送／一起不送，理由與壓縮那一組相同：
            // 半套的矩形（有 x、沒有 w）在 worker 端是**說不出來的請求**，寧可整個缺席讓它
            // 具名拒絕（RejectCropWithoutRect），也不要送一個殘缺的座標過去。
            // ⚠ 用 double?：值型別的 0 會被序列化進每一個封包，而 x=0 是完全合法的左緣。
            cropX = crop?.X,
            cropY = crop?.Y,
            cropW = crop?.Width,
            cropH = crop?.Height,
            cropRatio = crop?.RatioId,
            // 去背的模型路徑（只有 op=remove-bg 會填）。null＝執行檔旁邊的預設位置，
            // 也就是「沒有指定」時該有的行為——所以其餘 op 的封包一個位元組都沒變。
            removeBgModel = removeBgModel,
            // 去背的邊緣精修模式（只有 op=remove-bg 會填，而且只有明確關掉時才有值）。
            // null＝預設（開）＝這個欄位還不存在的年代的意思，其餘 op 的封包一個位元組都沒變。
            removeBgRefine = removeBgRefine,
            // 合併圖片時的頁面大小（null＝original）。它是**新欄位**，所以協議版本升到 4：
            // op=merge-pdf 當年不用升版，正是因為它一個欄位都沒加。
            pageSize = pageSize
        });
        // op= 附加在行尾：既有套件比對的是 `worker: job 1 sent (N file(s), target=..., quality=...)`。
        // 裁切矩形也只在行尾、而且只有 op=crop 時才出現（既有那一行的其餘部分逐字元不變）。
        _log($"worker: job 1 sent ({sourcePaths.Length} file(s), target={targetFormat}, " +
             $"quality={quality}, op={op})" + (crop != null ? $" crop={crop.Describe()}" : "") +
             (pageSize != null ? $" pageSize={pageSize}" : ""));

        _snapshot = new ConversionSnapshot(0, sourcePaths.Length, null, false, 0, 0,
                                           Array.Empty<string>(), null);
        _sinceLastMessage.Restart();

        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "conversion-watchdog" };
        _watchdog.Start();
        return true;
    }

    /// <summary>
    /// 這個 job 的 worker 要寫的證據檔路徑——**每一個 job 一個**。
    ///
    /// ⚠ 舊版回傳的是**所有 worker 共用**的 `%LOCALAPPDATA%\Confern\worker.log`，而
    /// <see cref="Evidence"/> 以獨占模式開啟它 ⇒ 同時存在的第二個 worker 直接開檔失敗
    /// （2026-10-06 量到：`Unhandled exception System.IO.IOException ... Evidence.cs:34`，
    /// worker 從未連上管道，UI 端整批逾時）。共用名由 worker 端以鏡射維護，見 <see cref="WorkerLog"/>。
    /// </summary>
    private string WorkerLogPath() => WorkerLog.ForJob(_pipeName);

    /// <summary>
    /// 每收到一則 worker 訊息就更新快照。跑在 PipeLink 的讀取任務上，所以
    /// ① 只碰不可變快照（整份換掉），② 與 UI 執行緒無共享可變狀態。
    /// </summary>
    private void OnMessage(WorkerMessage message)
    {
        _sinceLastMessage.Restart();
        ConversionSnapshot current = _snapshot;
        int done = current.Done, total = current.Total, ok = current.Ok, failed = current.Failed;
        List<string> failures = current.Failures.ToList();
        List<string> failureFiles = (current.FailureFiles ?? Array.Empty<string>()).ToList();

        if (message.protocolVersion != WorkerProtocol.Version)
        {
            _log($"worker: protocol mismatch (worker={message.protocolVersion}, " +
                 $"ui={WorkerProtocol.Version}) -> restarting worker");
            _snapshot = new ConversionSnapshot(done, total, null, true, ok, failed, failures,
                                               UiStrings.Get("svc.versionMismatchRestarted"), failureFiles);
            KillWorker();
            return;
        }

        switch (message.type)
        {
            case WorkerMessageTypes.Ready:
                _log($"worker: ready ({message.detail})");
                break;

            case WorkerMessageTypes.Progress:
                _snapshot = new ConversionSnapshot(message.done, message.total, message.current,
                                                   false, ok, failed, failures, null, failureFiles);
                break;

            case WorkerMessageTypes.Result:
                if (message.ok) ok++;
                else
                {
                    failed++;
                    // 檔名與訊息**分開留著**：失敗清單（§7.3）要的是兩欄，
                    // 而訊息裡的檔名是規格 §7.1 的排版，不是可以拿來當資料用的欄位。
                    if (message.message != null)
                    {
                        failures.Add(message.message);
                        failureFiles.Add(message.sourcePath ?? "");
                    }
                }
                _snapshot = new ConversionSnapshot(message.done, message.total, message.current,
                                                   false, ok, failed, failures, null, failureFiles)
                {
                    // 工具 read-qr 的產物：**原封不動**（不 trim、不截斷）。
                    // 這個欄位是產物本身而不是排版，呼叫端要斷言的是「worker 解出來的每一個字元」
                    // 有沒有原樣到達 UI；在這裡加工一次，兩端就不可能逐字元相同了。
                    QrText = message.text,
                    // 詳細原因同樣**原封不動**帶過來（見 LastDetail 的說明）：它是「為什麼失敗」的
                    // 唯一跨管道來源，在這一端加工一次，驗收要斷言的那串字就不再是 worker 說的那一串。
                    LastDetail = message.detail
                };
                _log($"worker result: ok={message.ok} output={message.outputPath ?? "-"} " +
                     $"error={message.errorCode} message={message.message}");
                break;

            case WorkerMessageTypes.Summary:
                // ⚠ `QrText` **一定要帶過來**（2026-10-07 實測踩到）：一個 job 的收尾訊息是
                //    result 之後的 summary，而 summary 會組一份**新的**快照。少了這一行，
                //    「worker 解出來的文字」會在結果訊息之後的幾毫秒內被一份沒有文字的快照蓋掉
                //    ——量到的症狀正是 `worker result: ... textChars=42` 緊接著
                //    `qr worker snapshot: ... line='✓ 1'` 而 `chars=0`。它與失敗清單同一條規矩：
                //    這一則訊息不重述那個欄位，但**不可以把它清掉**。
                _snapshot = new ConversionSnapshot(message.done, message.total, null, true,
                                                   message.okCount, message.failedCount, failures,
                                                   null, failureFiles)
                {
                    QrText = current.QrText,
                    LastDetail = current.LastDetail
                };
                _log($"worker summary: ✓ {message.okCount} / ✗ {message.failedCount}");
                break;

            case WorkerMessageTypes.Error:
                _log($"worker error: code={message.code} detail={message.detail}");
                _snapshot = new ConversionSnapshot(done, total, null, true, ok, failed, failures,
                                                   message.code == WorkerErrorCodes.ProtocolMismatch
                                                       ? UiStrings.Get("svc.versionMismatch") : UiStrings.Get("svc.error"),
                                                   failureFiles)
                {
                    QrText = current.QrText,
                    LastDetail = current.LastDetail
                };
                break;
        }
    }

    /// <summary>
    /// 沒有回音就是有問題——**不可以永遠等下去**（雲端佔位檔、被鎖住的檔案都可能吊住 worker）。
    /// </summary>
    private void WatchdogLoop()
    {
        while (!_stop)
        {
            Thread.Sleep(500);
            if (_snapshot.Finished) return;
            if (_sinceLastMessage.ElapsedMilliseconds < InactivityTimeoutMs) continue;

            _log($"worker: no message for {InactivityTimeoutMs} ms -> treating job as timed out");
            ConversionSnapshot current = _snapshot;
            _snapshot = new ConversionSnapshot(current.Done, current.Total, null, true,
                                               current.Ok, current.Failed, current.Failures,
                                               UiStrings.Get("svc.timeout"), current.FailureFiles);
            KillWorker();
            return;
        }
    }

    private void KillWorker()
    {
        try
        {
            if (_worker is { HasExited: false })
            {
                _worker.Kill(entireProcessTree: true);
                _worker.WaitForExit(2000);
                _log("worker: process killed");
            }
        }
        catch (Exception ex)
        {
            _log($"worker: kill failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop = true;
        try
        {
            if (_link != null && _pipe is { IsConnected: true })
                _link.Send(new WorkerMessage { type = WorkerMessageTypes.Shutdown });
        }
        catch { }

        // 給 worker 一點時間自己收（它會在檔案邊界看到 shutdown）
        if (_worker is { HasExited: false } && !_worker.WaitForExit(1500)) KillWorker();

        try { _link?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _worker?.Dispose();
    }
}
