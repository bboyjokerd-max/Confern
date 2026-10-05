using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidecar;

/// <summary>
/// UI ↔ Worker 的協定（規格 §9.1）：**具名管道 + 版本化 JSON**，一行一則訊息。
///
/// 關鍵性質：版本不合時 UI **重啟 worker**，永不崩潰；
/// 管道斷了也不能讓 UI 卡死（worker 死了要把剩下的檔案標成失敗，不是永遠等下去）。
/// </summary>
internal static class WorkerProtocol
{
    /// <summary>
    /// 協議版本。**2026-10-05 由 1 升到 2**：新增 <c>op</c> 欄位（工作種類）。
    ///
    /// 為什麼非升不可：`op` 缺席時的語意是 `convert`（見 <see cref="WorkerMessage.op"/>），
    /// 所以一個**舊 worker** 收到帶 `op=rotate90` 的封包時，會把它當成一般的格式轉換照做——
    /// 使用者按了「轉 90 度」卻得到一個換格式的檔案，而且沒有任何錯誤。升版本讓這種配對
    /// 在連線階段就被擋掉（<see cref="ConversionClient"/> 偵測到版本不符會重啟 worker），
    /// 而不是變成一個靜默的錯誤結果。
    ///
    /// **2026-10-06 由 2 升到 3**：新增 <c>outputDir</c> 欄位（輸出資料夾）。
    ///
    /// 為什麼非升不可：`outputDir` 缺席時的語意是「來源檔旁邊」（見
    /// <see cref="WorkerMessage.outputDir"/>），而這**正好是舊版的行為**——所以一個舊 worker
    /// 收到帶資料夾的封包時不會報錯，它只會**默默把檔案寫到來源旁邊**。那正是本欄位要修掉的事：
    /// 使用者設定了輸出資料夾、按下 Enter，卻什麼都沒發生，畫面上還顯示成功。缺席語意與新功能
    /// 的意圖在這裡剛好相反，靠預設值擋不住，只能靠版本配對在連線階段拒絕。
    ///
    /// **2026-10-06 新增 `op=strip-metadata`——刻意「不」由 3 升到 4。**
    ///
    /// 理由：升版本的用途是擋掉**靜默做錯事**的配對。`op` 缺席會落到 `convert`（舊 worker 會把
    /// 旋轉當成換格式照做，而且回報成功），所以那一次非升不可；`outputDir` 的缺席語意與新功能
    /// 意圖相反，所以那一次也非升不可。**但這個新 op 不屬於那一類**：一個還不認識
    /// <c>strip-metadata</c> 的舊 worker 會走到 <see cref="WorkerServer.RejectUnknownOp"/>，
    /// 回一個具名的失敗（`unknown op 'strip-metadata' (this worker speaks convert/rotate90)`），
    /// 使用者看到的是「這個工具在這個 worker 上做不到」而不是「按了 CLEAN 卻轉了格式」。
    /// 換句話說：**未知 op 已經有明確的失敗路徑，版本閘門在這裡是多的**，
    /// 而多升一次版本會讓「舊 exe 配新 exe」的升級情境無謂地多斷一次線。
    /// </summary>
    public const int Version = 3;

    /// <summary>
    /// `WorkerMessage.op` 的合法值：這件工作**要做什麼**。
    ///
    /// 放在 <see cref="WorkerProtocol"/> 而不是 <see cref="WorkerMessageTypes"/>：
    /// 後者是「封包是哪一種」（job／progress／result…），這裡是「工作種類」——兩套詞彙。
    /// 混在一個類別裡，下一個讀的人會以為 `op` 也是一種封包類型。
    /// </summary>
    public const string OpConvert = "convert";

    /// <summary>工具 `rotate90`（順時針 90 度，M3）。輸出格式＝來源格式，見 <see cref="ConversionEngine.Rotate90"/>。</summary>
    public const string OpRotate90 = "rotate90";

    /// <summary>
    /// 工具 `strip-metadata`（去 metadata，M3 第二個工具）。輸出格式＝來源格式，
    /// 見 <see cref="ConversionEngine.StripMetadata"/> 與規格 §9.4。
    /// ⚠ 新增這個 op **不升 <see cref="Version"/>**，理由寫在 <see cref="Version"/> 的註解裡。
    /// </summary>
    public const string OpStripMetadata = "strip-metadata";

    public static string NewPipeName() => ProductInfo.Slug + "-" + Guid.NewGuid().ToString("N");

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(WorkerMessage message) => JsonSerializer.Serialize(message, Json);

    /// <summary>讀不懂的行回 null（不拋）：壞訊息不該讓任何一邊死掉。</summary>
    public static WorkerMessage? Deserialize(string line)
    {
        try { return JsonSerializer.Deserialize<WorkerMessage>(line, Json); }
        catch { return null; }
    }
}

internal static class WorkerMessageTypes
{
    public const string Ready = "ready";         // worker → UI
    public const string Job = "job";             // UI → worker
    public const string Progress = "progress";   // worker → UI（原地更新用，不是逐行洗版）
    public const string Result = "result";       // worker → UI（單檔結果）
    public const string Summary = "summary";     // worker → UI（整批結束）
    public const string Error = "error";         // 任一邊 → 另一邊
    public const string Cancel = "cancel";       // UI → worker
    public const string Shutdown = "shutdown";   // UI → worker
}

internal static class WorkerErrorCodes
{
    public const string ProtocolMismatch = "ProtocolMismatch";
    public const string JobTimeout = "JobTimeout";
    public const string WorkerGone = "WorkerGone";
    public const string WorkerStartFailed = "WorkerStartFailed";
}

/// <summary>一行一則的訊息。欄位名稱刻意與規格 §9.1 一致（`jobId`／`sourcePaths`／`targetFormat`／`quality`）。</summary>
internal sealed class WorkerMessage
{
    public int protocolVersion { get; set; } = WorkerProtocol.Version;
    public string type { get; set; } = "";
    public int jobId { get; set; }
    public string[]? sourcePaths { get; set; }
    public string? targetFormat { get; set; }
    /// <summary>
    /// 工作種類（2026-10-05 M3 新增）。<see cref="WorkerProtocol.OpConvert"/>＝格式轉換，
    /// <see cref="WorkerProtocol.OpRotate90"/>＝工具旋轉。
    ///
    /// **null／空字串的語意是 `convert`**：那是「這個欄位還不存在的年代」的封包該有的意思，
    /// 不是猜測。但配對錯誤**不能靠這個預設值擋**——一個舊 worker 會把 rotate90 當成 convert
    /// 默默做掉，所以協議版本同時升到 2（見 <see cref="WorkerProtocol.Version"/>）。
    /// </summary>
    public string? op { get; set; }
    /// <summary>
    /// 輸出資料夾（2026-10-06 M4 新增），由 <see cref="Settings.ResolveOutputDirectory"/> 解析而來。
    ///
    /// **null／空字串＝寫在來源檔旁邊**：那是「這個欄位還不存在的年代」的封包該有的意思，
    /// 也正是 <see cref="ConversionEngine"/> 的 <c>targetDirectory</c> 預設值——worker 直接把它
    /// 原封不動傳下去，不在這裡再解析一次（解析規則只有 Settings 那一份）。
    ///
    /// 與 `op` 不同的是，這裡的缺席語意**與新功能的意圖相反**（缺席＝來源旁邊，而使用者要的是
    /// 別的資料夾），所以配對錯誤不可能靠預設值擋，必須靠協議版本（見
    /// <see cref="WorkerProtocol.Version"/>）。
    /// </summary>
    public string? outputDir { get; set; }
    public int quality { get; set; }
    public int done { get; set; }
    public int total { get; set; }
    public string? current { get; set; }
    public bool ok { get; set; }
    /// <summary>整批的成功檔數（summary 用；與單檔的 <see cref="ok"/> 分開，免得型別打架）。</summary>
    public int okCount { get; set; }
    public int failedCount { get; set; }
    public string? outputPath { get; set; }
    public string? errorCode { get; set; }
    public string? message { get; set; }
    /// <summary>
    /// 單檔結果的來源檔案。`message` 是規格 §7.1 的**排版結果**（`broken.png 無法解碼`），
    /// 把檔名當資料用等於在解析自己的排版；UI 的失敗清單（§7.3）要檔名與原因兩欄，
    /// 所以檔名必須是欄位而不是字串裡的一段。
    /// </summary>
    public string? sourcePath { get; set; }
    public string? detail { get; set; }
    public string? code { get; set; }
}

/// <summary>
/// Worker 行程（規格 §9.1）：自己連回 UI 建立的具名管道，逐檔轉換，逐檔回報。
///
/// 為什麼要有獨立行程：解碼／編碼會吃 CPU 與記憶體，而且大型檔案可能卡在雲端下載。
/// UI 行程**不可以**被這些事影響（輪盤要能立刻反應、保險要能隨時收層）。
/// </summary>
internal static class WorkerServer
{
    public static int Run(string pipeName, Evidence evidence)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(10000);
        }
        catch (Exception ex)
        {
            evidence.Line($"worker: connect to pipe '{pipeName}' failed: {ex.GetType().Name}: {ex.Message}");
            return 11;
        }

        // 管道的一切都交給 PipeLink（每個方向只有一個非同步擁有者）
        using var link = new PipeLink(pipe, evidence.Line);
        var inbox = new BlockingCollection<WorkerMessage>();
        var pipeClosed = new ManualResetEventSlim(false);
        link.Received += message =>
        {
            try { inbox.Add(message); } catch { /* 已關閉 */ }
        };
        link.Closed += () => pipeClosed.Set();
        link.Start();

        var registry = new CodecRegistry();
        var engine = new ConversionEngine(registry, evidence.Line);
        var journal = new TempJournal(TempJournal.DefaultPath());
        int recovered = journal.RecoverStale();
        evidence.Line($"worker: ready protocolVersion={WorkerProtocol.Version} " +
                      $"offered=[{string.Join(",", registry.OfferedOutputs)}] " +
                      $"staleTempsRecovered={recovered} (spec 7.1)");

        link.Send(new WorkerMessage
        {
            type = WorkerMessageTypes.Ready,
            detail = $"offered=[{string.Join(",", registry.OfferedOutputs)}]"
        });

        while (true)
        {
            if (!inbox.TryTake(out WorkerMessage? message, 50))
            {
                if (pipeClosed.IsSet) { evidence.Line("worker: UI closed the pipe; exiting"); return 0; }
                continue;
            }

            if (message.protocolVersion != WorkerProtocol.Version)
            {
                evidence.Line($"worker: protocol mismatch (got {message.protocolVersion}, " +
                              $"expected {WorkerProtocol.Version}) -> refusing");
                link.Send(new WorkerMessage
                {
                    type = WorkerMessageTypes.Error,
                    code = WorkerErrorCodes.ProtocolMismatch,
                    detail = $"worker speaks {WorkerProtocol.Version}, UI sent {message.protocolVersion}"
                });
                Thread.Sleep(200);      // 讓那則錯誤真的送出去再退出
                return 12;
            }

            switch (message.type)
            {
                case WorkerMessageTypes.Job:
                    RunJob(message, link, engine, journal, evidence, inbox);
                    break;
                case WorkerMessageTypes.Cancel:
                    evidence.Line($"worker: cancel for job {message.jobId} with no active job");
                    break;
                case WorkerMessageTypes.Shutdown:
                    evidence.Line("worker: shutdown requested");
                    return 0;
                default:
                    evidence.Line($"worker: unknown message type '{message.type}' ignored");
                    break;
            }
        }
    }

    private static void RunJob(WorkerMessage job, PipeLink link, ConversionEngine engine,
                               TempJournal journal, Evidence evidence, BlockingCollection<WorkerMessage> inbox)
    {
        string[] paths = job.sourcePaths ?? Array.Empty<string>();
        string target = (job.targetFormat ?? "").ToUpperInvariant();
        int quality = job.quality <= 0 ? 85 : job.quality;
        // op 缺席＝convert：那是「這個欄位還不存在的年代」的封包該有的意思。到這裡一定同版本，
        // 因為 protocolVersion 不符的封包在連線階段就被擋掉了（見 WorkerProtocol.Version）。
        string op = string.IsNullOrEmpty(job.op) ? WorkerProtocol.OpConvert : job.op!;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ⚠ op= 只**附加在行尾**：既有套件比對的是 `worker job N: files=... target=...`，
        // 插在中間會直接弄紅它們（與 wheel shown 那行同一條規矩）。
        evidence.Line($"worker job {job.jobId}: files={paths.Length} target={target} quality={quality} op={op}");
        link.Send(new WorkerMessage
        {
            type = WorkerMessageTypes.Progress, jobId = job.jobId, done = 0, total = paths.Length,
            current = paths.Length > 0 ? Path.GetFileName(paths[0]) : null
        });

        int succeeded = 0, failed = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            // 取消在**檔案邊界**生效（一個檔案轉到一半不中斷：中斷會留下沒意義的半成品）
            if (DrainCancel(inbox, job.jobId, evidence))
            {
                evidence.Line($"worker job {job.jobId}: cancelled at file boundary {i}/{paths.Length}");
                break;
            }

            link.Send(new WorkerMessage
            {
                type = WorkerMessageTypes.Progress, jobId = job.jobId, done = i, total = paths.Length,
                current = Path.GetFileName(paths[i])
            });

            // 工作的分派就在這一處。**未知的 op 大聲失敗**：不丟例外、也絕不默默當成 convert
            // 來做（那會讓「按了工具卻得到一個格式轉換」變成靜默錯誤）。
            // outputDir 原封不動傳下去（null＝來源旁邊）：解析規則只有 Settings 那一份，
            // worker 不是第二個擁有者。資料夾不存在時的回退由 NamingPolicy 負責。
            ConversionResult result = op switch
            {
                WorkerProtocol.OpConvert => engine.Convert(paths[i], target, quality, journal,
                                                           job.outputDir),
                WorkerProtocol.OpRotate90 => engine.Rotate90(paths[i], journal, job.outputDir),
                WorkerProtocol.OpStripMetadata => engine.StripMetadata(paths[i], journal, job.outputDir),
                _ => RejectUnknownOp(paths[i], op)
            };
            if (result.Ok) succeeded++; else failed++;

            link.Send(new WorkerMessage
            {
                type = WorkerMessageTypes.Result,
                jobId = job.jobId,
                ok = result.Ok,
                outputPath = result.OutputPath,
                errorCode = result.Error.ToString(),
                message = result.UserMessage,
                sourcePath = result.SourcePath,
                detail = result.Detail,
                done = i + 1,
                total = paths.Length,
                current = Path.GetFileName(paths[i])
            });
        }

        sw.Stop();
        evidence.Line($"worker job {job.jobId} done: ok={succeeded} failed={failed} " +
                      $"in {sw.Elapsed.TotalMilliseconds:F0} ms");
        link.Send(new WorkerMessage
        {
            type = WorkerMessageTypes.Summary, jobId = job.jobId,
            okCount = succeeded, failedCount = failed,
            done = succeeded + failed, total = paths.Length
        });
    }

    /// <summary>
    /// 未知的 op：回一個**失敗的結果**（不丟例外，讓既有的失敗回報路徑接手），
    /// 使用者訊息走 <see cref="ConversionError.Unknown"/> 的規格字串（UI 不必為它多寫一條路徑），
    /// 真正的細節放在 detail 給日誌——那才是維修的人看得到的地方。
    ///
    /// **列出認識的 op 是刻意的**：這一行就是「未知 op 不會靜默變成 convert」這句話的可觀測證據，
    /// 也是不升協議版本的理由（見 <see cref="WorkerProtocol.Version"/>）。加工具時**要一起更新它**，
    /// 否則這句失敗訊息會說謊。
    /// </summary>
    private static ConversionResult RejectUnknownOp(string sourcePath, string op) =>
        new(sourcePath, null, ConversionError.Unknown,
            $"unknown op '{op}' (this worker speaks " +
            $"{WorkerProtocol.OpConvert}/{WorkerProtocol.OpRotate90}/{WorkerProtocol.OpStripMetadata})");

    private static bool DrainCancel(BlockingCollection<WorkerMessage> inbox, int jobId, Evidence evidence)
    {
        bool cancelled = false;
        while (inbox.TryTake(out WorkerMessage? pending, 0))
        {
            if (pending.type == WorkerMessageTypes.Cancel && pending.jobId == jobId) cancelled = true;
            else if (pending.type == WorkerMessageTypes.Job)
                evidence.Line($"worker: job {pending.jobId} arrived while {jobId} was running; " +
                              "queued jobs are not implemented yet (one job at a time)");
        }
        return cancelled;
    }
}
