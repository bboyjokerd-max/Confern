using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Sidecar;

/// <summary>
/// 具名管道的**唯一擁有者**：一個非同步讀取任務、一個非同步寫入任務，兩者互不干擾。
///
/// 為什麼要這樣寫（實測教訓，2026-10-01）：
/// 同步管道上「一個執行緒阻塞讀、另一個執行緒對同一個 handle 寫」會**互相卡死**——
/// 症狀是 worker 的 `ready` 一直到對方行程被殺掉才送達，於是每一批都逾時、一個檔都轉不出來。
/// 非同步管道混用同步讀寫也一樣會出事。
/// 因此規則寫死在這裡：**每個方向只有一個擁有者，而且全部走非同步 API**。
/// </summary>
internal sealed class PipeLink : IDisposable
{
    private readonly Stream _stream;
    private readonly Action<string> _log;
    private readonly BlockingCollection<string> _outbox = new(new ConcurrentQueue<string>());
    private readonly CancellationTokenSource _cts = new();

    private Task? _writeTask;
    private Task? _readTask;

    public PipeLink(Stream stream, Action<string> log)
    {
        _stream = stream;
        _log = log;
    }

    /// <summary>收到一則訊息（在讀取任務上被呼叫；實作要自己保證執行緒安全）。</summary>
    public event Action<WorkerMessage>? Received;

    /// <summary>管道結束（對方關閉或讀取失敗）。</summary>
    public event Action? Closed;

    public int SentCount { get; private set; }

    public void Start()
    {
        _writeTask = Task.Run(WriteLoopAsync);
        _readTask = Task.Run(ReadLoopAsync);
    }

    /// <summary>排進送出的佇列就回來（真正寫出去由寫入任務做）。</summary>
    public void Send(WorkerMessage message)
    {
        string line = WorkerProtocol.Serialize(message);
        try
        {
            _outbox.Add(line);
            SentCount++;
        }
        catch (Exception ex)
        {
            _log($"pipe: send failed ({ex.GetType().Name})");
        }
    }

    private async Task WriteLoopAsync()
    {
        var writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            foreach (string line in _outbox.GetConsumingEnumerable(_cts.Token))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log($"pipe: write loop ended: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = new StreamReader(_stream, new UTF8Encoding(false));
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                WorkerMessage? message = WorkerProtocol.Deserialize(line);
                if (message == null) { _log("pipe: undecodable line ignored"); continue; }
                Received?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            _log($"pipe: read loop ended: {ex.GetType().Name}: {ex.Message}");
        }

        try { Closed?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _outbox.CompleteAdding(); } catch { }
        try { _stream.Dispose(); } catch { }
    }
}
