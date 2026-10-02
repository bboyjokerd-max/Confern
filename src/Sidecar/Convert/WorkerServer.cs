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
    public const int Version = 1;

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
        var sw = System.Diagnostics.Stopwatch.StartNew();

        evidence.Line($"worker job {job.jobId}: files={paths.Length} target={target} quality={quality}");
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

            ConversionResult result = engine.Convert(paths[i], target, quality, journal);
            if (result.Ok) succeeded++; else failed++;

            link.Send(new WorkerMessage
            {
                type = WorkerMessageTypes.Result,
                jobId = job.jobId,
                ok = result.Ok,
                outputPath = result.OutputPath,
                errorCode = result.Error.ToString(),
                message = result.UserMessage,
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
