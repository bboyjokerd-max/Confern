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
    ///
    /// <summary>
    /// **2026-10-06 新增 `op=compress`（第三顆扇形）——同樣不升，理由逐條相同。**
    /// 另外檢查過它帶來的新欄位（<c>compressMode</c>／<c>compressTargetBytes</c>／
    /// <c>standardJpgQuality</c>／<c>standardWebpQuality</c>）：一個舊 worker **根本走不到**
    /// 讀它們的那一行，因為 `op=compress` 會在它身上變成 <see cref="WorkerServer.RejectUnknownOp"/>。
    /// 「欄位被忽略」這種靜默錯誤需要「op 被接受但欄位被丟掉」這個前提，而那個前提不成立。
    ///
    /// **2026-10-06 新增 `op=crop`（第四顆扇形）——同樣不升，理由逐條相同。**
    /// 它帶來五個新欄位（<c>cropX</c>／<c>cropY</c>／<c>cropW</c>／<c>cropH</c>／<c>cropRatio</c>），
    /// 五個都是 <c>null</c> 且 <c>WhenWritingNull</c>，所以**既有 op 的封包逐位元不變**；
    /// 而舊 worker 收到 `op=crop` 一樣走到 <see cref="WorkerServer.RejectUnknownOp"/>，
    /// 到不了讀那五個欄位的那一行。
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

    /// <summary>
    /// 工具 `compress`（壓縮，M3 第三顆扇形）。輸出格式＝來源格式，
    /// 等級由 <see cref="CompressionPolicy"/> 決定（設定頁是唯一來源），
    /// 見 <see cref="ConversionEngine.Compress"/> 與規格 §9.4。
    /// ⚠ 新增這個 op **不升 <see cref="Version"/>**，理由寫在 <see cref="Version"/> 的註解裡。
    /// </summary>
    public const string OpCompress = "compress";

    /// <summary>
    /// 工具 `crop`（裁切，M3 第四顆扇形）。輸出格式＝來源格式，矩形是一個**正規化**的
    /// 0..1 矩形（<see cref="WorkerMessage.cropX"/>…）＋一個比例預設（<see cref="WorkerMessage.cropRatio"/>），
    /// 每一張圖各自換算，見 <see cref="ConversionEngine.Crop"/> 與 <see cref="CropMath"/>。
    ///
    /// ⚠ 新增這個 op **不升 <see cref="Version"/>**，理由與前兩顆逐條相同：舊 worker 走
    /// <see cref="WorkerServer.RejectUnknownOp"/>，而且它**根本走不到**讀那五個新欄位的那一行。
    /// </summary>
    public const string OpCrop = "crop";

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

    /// <summary>
    /// 壓縮等級（2026-10-06 M3 第三顆扇形，<see cref="WorkerProtocol.OpCompress"/> 專用）。
    /// 正規值見 <see cref="CompressionPolicy.Modes"/>。
    ///
    /// **null／空字串的缺席語意 ＝ <see cref="CompressionPolicy.ModeStandard"/>**：那是
    /// 「這個欄位還不存在的年代」的封包該有的意思，也正是**這個工具出現之前的行為**
    /// （照既有的品質設定走）。與 <c>outputDir</c> 不同的是，這裡的缺席語意**與新功能的意圖
    /// 一致**（新功能的預設值就是標準），所以配對錯誤不會變成靜默做錯事——而一個不認識
    /// `op=compress` 的舊 worker 本來就會走到 <see cref="WorkerServer.RejectUnknownOp"/>。
    /// </summary>
    public string? compressMode { get; set; }

    /// <summary>
    /// 「壓縮到」的上限（**位元組**，2026-10-06）。<c>0</c>＝沒有上限。
    /// 由 UI 端的 <see cref="Settings.ToCompressionPolicy"/> 換算（設定檔存的是 KB），
    /// worker 不再自己換算一次——單位換算只有一個擁有者。
    /// </summary>
    public long? compressTargetBytes { get; set; }

    /// <summary>
    /// 標準模式要用的 JPG／WebP 品質（2026-10-06）。**與 <c>quality</c> 刻意分開**：
    /// <c>quality</c> 是「這一次工作指定的品質」（格式轉換就是用它），而這兩個是
    /// 「設定檔裡的品質設定」，壓縮工具的標準模式要的是後者，且**逐檔依格式選**
    /// （一批裡可能同時有 JPG 與 PNG，JPG 用 jpg 品質、其餘品質無意義）。
    ///
    /// ⚠ 刻意是 <c>int?</c> 而不是 <c>int</c>：值型別的 0 會被序列化進每一個封包，
    /// 那會讓**既有 op 的訊息多出兩個欄位**（`quality` 那一組是逐位元比對的）。
    /// 四個壓縮欄位一起出現、一起不出現——半套的政策比沒有政策危險。
    /// null／超出 60–100 ＝ 沒有這個值 → 用規格預設 85。
    /// </summary>
    public int? standardJpgQuality { get; set; }
    public int? standardWebpQuality { get; set; }

    /// <summary>
    /// 裁切矩形（**正規化 0..1**，2026-10-06 M3 第四顆扇形，<see cref="WorkerProtocol.OpCrop"/> 專用）。
    ///
    /// 為什麼是正規化而不是像素：使用者 2026-10-06 拍板「批次＝同一比例套用到每一張」。
    /// 像素座標只能在一個尺寸上有意義，送像素等於把「用第一張的尺寸套到每一張」寫進協議。
    /// 每一個檔各自乘上自己的尺寸（<see cref="CropMath.TryToPixels"/>）。
    ///
    /// ⚠ 五個欄位**一起出現、一起不出現**（與壓縮那一組同一個規矩）：null 一律不進訊息
    /// （<c>WhenWritingNull</c>），所以既有的 job 封包逐位元不變。其中四個是 <c>double?</c>
    /// 而不是 <c>double</c>：值型別的 0 會被序列化進**每一個**封包（<c>x=0</c> 是完全合法的矩形左緣），
    /// 那會讓既有的封包多出四個欄位——而「既有封包逐位元不變」是這一顆扇形唯一的相容性契約。
    /// </summary>
    public double? cropX { get; set; }
    public double? cropY { get; set; }
    public double? cropW { get; set; }
    public double? cropH { get; set; }

    /// <summary>
    /// 比例預設 id（2026-10-06，<see cref="WorkerProtocol.OpCrop"/> 專用）。正規值見
    /// <see cref="CropRatios"/>；**null／空／認不得＝<see cref="CropRatios.IdFree"/>（自由）**，
    /// 那是「這個欄位還不存在的年代」的封包該有的意思，也正是**不做比例修飾**的那條路。
    /// </summary>
    public string? cropRatio { get; set; }
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

        // 裁切的矩形/比例先解析一次：它同時要進 job 那一行日誌與每一張圖的分派。
        // 解析兩次會讓「認不得的比例 id」那句警告被寫兩次（而其中一次還是被丟掉的），
        // 也會讓「送出去的是什麼」與「用的是什麼」有機會不一致。
        CropSpec? cropSpec = op == WorkerProtocol.OpCrop ? CropOf(job, evidence.Line) : null;

        // ⚠ op= 只**附加在行尾**：既有套件比對的是 `worker job N: files=... target=...`，
        // 插在中間會直接弄紅它們（與 wheel shown 那行同一條規矩）。
        // 裁切的矩形同樣只追加在**尾巴**：它是這一顆扇形唯一跨管道的事實，不放進日誌就只剩
        // 「UI 說它送出去了」可以相信。
        evidence.Line($"worker job {job.jobId}: files={paths.Length} target={target} quality={quality} op={op}" +
                      (op == WorkerProtocol.OpCrop ? $" crop={cropSpec?.Describe() ?? "(none)"}" : ""));
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
                WorkerProtocol.OpCompress => engine.Compress(paths[i], PolicyOf(job, evidence.Line), journal,
                                                             job.outputDir),
                WorkerProtocol.OpCrop => cropSpec is { } spec
                    ? engine.Crop(paths[i], spec, journal, job.outputDir)
                    // 矩形整個缺席：那是同版本的 UI 送出了不該送的東西＝bug。**不猜**、也不默默
                    // 用一個全圖矩形去裁（那會變成 CropNothingToDo 的拒絕，訊息還會誤導人）。
                    : RejectCropWithoutRect(paths[i]),
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
    /// 封包上的壓縮欄位 → <see cref="CompressionPolicy"/>（<c>op=compress</c> 的唯一輸入）。
    ///
    /// ⚠ 這裡**不是第二個政策擁有者**：合法值的清單來自 <see cref="CompressionPolicy.CanonicalMode"/>，
    /// 而「什麼是合法的上限」由 UI 端（設定頁 ＋ <see cref="SettingsStore.TryApply"/>）把關。
    /// worker 只做一件防禦性的收斂：缺席／認不得的一律回標準模式並**大聲記 log**——
    /// 到得了這裡代表同版本的 UI 送出了不該送的東西，那是 bug，不該靜默。
    /// </summary>
    private static CompressionPolicy PolicyOf(WorkerMessage job, Action<string> log)
    {
        string given = job.compressMode ?? "";
        string mode = CompressionPolicy.CanonicalModeOrEmpty(given);
        if (mode.Length == 0 && given.Length > 0)
        {
            mode = CompressionPolicy.ModeStandard;
            log($"compress: unknown compressMode '{given}' -> '{mode}' " +
                $"(this worker speaks {string.Join("/", CompressionPolicy.Modes)})");
        }
        else if (mode.Length == 0)
        {
            mode = CompressionPolicy.ModeStandard;   // 缺席＝這個欄位還不存在的年代的意思
        }

        long target = job.compressTargetBytes ?? 0;
        if (target < 0)
            log($"compress: negative compressTargetBytes {target} -> 0 (no limit)");

        return new CompressionPolicy(
            mode,
            Math.Max(0, target),
            job.standardJpgQuality is >= 60 and <= 100 ? job.standardJpgQuality.Value : 85,
            job.standardWebpQuality is >= 60 and <= 100 ? job.standardWebpQuality.Value : 85);
    }

    /// <summary>
    /// 封包上的裁切欄位 → <see cref="CropSpec"/>（<c>op=crop</c> 的唯一輸入）；整個矩形缺席時回 null。
    ///
    /// ⚠ 這裡**不是第二個政策擁有者**：合法值的清單來自 <see cref="CropRatios"/>，
    /// 「什麼是合法的矩形」由 <see cref="CropMath"/> 把關。worker 只做一件防禦性的收斂：
    /// 認不得的比例 id 一律回自由裁切並**大聲記 log**——到得了這裡代表同版本的 UI 送出了
    /// 不該送的東西，那是 bug，不該靜默（與 <see cref="PolicyOf"/> 同一條規矩）。
    /// </summary>
    private static CropSpec? CropOf(WorkerMessage job, Action<string> log)
    {
        if (job.cropX is not { } x || job.cropY is not { } y
            || job.cropW is not { } w || job.cropH is not { } h)
        {
            return null;
        }

        string given = job.cropRatio ?? "";
        if (given.Length > 0 && !CropRatios.IsCanonical(given))
        {
            // 訊息列的是**合法值集合**（All，八個），不是按鈕列的五顆：`9:16`／`3:4`／
            // `original-flipped` 沒有按鈕但**合法**——它們是裁切視窗的方向鈕產生的 id。
            // 拿按鈕列來當「我聽得懂哪些」會在日誌上說出假話（明明支援卻說不支援）。
            log($"crop: unknown cropRatio '{given}' -> '{CropRatios.DefaultId}' " +
                $"(this worker speaks {string.Join("/", CropRatios.All.Select(p => p.Id))})");
        }

        // 逐值防護：NaN／Infinity 會在 CropMath 的夾制裡變成 0（夾制本身就是那份規則），
        // 但先在這裡說出來——「收到一個非數字的欄位」是另一件事，不該混進夾制的日誌裡。
        foreach ((string name, double value) in
                 new[] { ("cropX", x), ("cropY", y), ("cropW", w), ("cropH", h) })
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                log($"crop: {name}={value} is not a finite number -> treated as 0 by the clamp " +
                    "(a same-version UI should never send this)");
        }

        return new CropSpec(x, y, w, h, CropRatios.CanonicalId(given));
    }

    /// <summary>
    /// `op=crop` 但封包上沒有矩形：**具名失敗**，不猜、也不默默裁一張全圖。
    /// 訊息沿用規格 §7.1 既有的「無法讀取來源檔案」那一類嗎？不——那不是原因。
    /// 這裡用 <see cref="ConversionError.Unknown"/>（「轉檔失敗」）配上真正的細節在 detail，
    /// 因為對使用者而言這確實是「不該發生的失敗」，而維修的人看得到 detail。
    /// </summary>
    private static ConversionResult RejectCropWithoutRect(string sourcePath) =>
        new(sourcePath, null, ConversionError.Unknown,
            $"op=crop without cropX/cropY/cropW/cropH (this worker speaks {CropRatios.DefaultId} " +
            "only when all four are present; nothing is guessed here)");

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
            $"{WorkerProtocol.OpConvert}/{WorkerProtocol.OpRotate90}/{WorkerProtocol.OpStripMetadata}/" +
            $"{WorkerProtocol.OpCompress}/{WorkerProtocol.OpCrop})");

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
