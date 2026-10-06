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
    /// </summary>
    public bool Start(string[] sourcePaths, string targetFormat, int quality, string? outputDir,
                      string? toolOp, out string? error, CompressionPolicy? compression = null)
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
                                                   "轉檔服務中斷", current.FailureFiles);
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
            standardWebpQuality = compression?.StandardWebpQuality ?? 0
        });
        // op= 附加在行尾：既有套件比對的是 `worker: job 1 sent (N file(s), target=..., quality=...)`。
        _log($"worker: job 1 sent ({sourcePaths.Length} file(s), target={targetFormat}, " +
             $"quality={quality}, op={op})");

        _snapshot = new ConversionSnapshot(0, sourcePaths.Length, null, false, 0, 0,
                                           Array.Empty<string>(), null);
        _sinceLastMessage.Restart();

        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "conversion-watchdog" };
        _watchdog.Start();
        return true;
    }

    private static string WorkerLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductInfo.Name, "worker.log");

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
                                               "轉檔服務版本不符，已重新啟動", failureFiles);
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
                                                   false, ok, failed, failures, null, failureFiles);
                _log($"worker result: ok={message.ok} output={message.outputPath ?? "-"} " +
                     $"error={message.errorCode} message={message.message}");
                break;

            case WorkerMessageTypes.Summary:
                _snapshot = new ConversionSnapshot(message.done, message.total, null, true,
                                                   message.okCount, message.failedCount, failures,
                                                   null, failureFiles);
                _log($"worker summary: ✓ {message.okCount} / ✗ {message.failedCount}");
                break;

            case WorkerMessageTypes.Error:
                _log($"worker error: code={message.code} detail={message.detail}");
                _snapshot = new ConversionSnapshot(done, total, null, true, ok, failed, failures,
                                                   message.code == WorkerErrorCodes.ProtocolMismatch
                                                       ? "轉檔服務版本不符" : "轉檔服務錯誤",
                                                   failureFiles);
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
                                               "轉檔逾時", current.FailureFiles);
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
