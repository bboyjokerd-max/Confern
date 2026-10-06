namespace Sidecar;

/// <summary>
/// UI 這一側的轉檔門面：把「起了 worker、正在轉檔」和「輪盤收起處那一行進度」接起來。
///
/// 這裡刻意不含任何轉檔邏輯——轉檔在 worker 行程（規格 §9.1），
/// 這一層只負責**起、看、顯示、收**。worker 起不來時也**要顯示失敗**（§7.2 禁止靜默失敗）。
/// </summary>
internal sealed class ConversionRunner : IDisposable
{
    private readonly string _exePath;
    private readonly ProgressPresenter _progress;
    private readonly Action<string> _log;
    private ConversionClient? _client;

    public ConversionRunner(string exePath, Action<string> log, ProgressPresenter progress)
    {
        _exePath = exePath;
        _log = log;
        _progress = progress;
    }

    public ConversionSnapshot Snapshot => _client?.Snapshot ?? ConversionSnapshot.Idle;

    /// <summary>
    /// 啟動一次工作。<paramref name="toolOp"/> 為 null 時＝格式轉換（**既有行為不變**）；
    /// 非 null 時走工具（見 <see cref="WorkerProtocol.OpRotate90"/>），此時
    /// <paramref name="targetFormat"/> 沒有意義——工具自己決定輸出格式。
    ///
    /// <paramref name="outputDir"/> 為 **null（預設）＝來源檔旁邊**，也就是這條路徑在
    /// 這個參數出現之前的行為——因此既有呼叫端（診斷路徑等）一個字都不用改。
    /// 非 null＝寫進那個資料夾（見 <see cref="Settings.ResolveOutputDirectory"/>）。
    /// </summary>
    public bool Start(string[] sourcePaths, string targetFormat, int quality, int x, int y,
                      string? toolOp = null, string? outputDir = null,
                      CompressionPolicy? compression = null)
    {
        _progress.Start(x, y);

        // **一次工作一個 client**（實測教訓，2026-10-01）：
        // client 擁有「管道 + worker」，而管道名是一次性的（Guid）。若重用同一個 client，
        // 第二次轉檔會拿到 ERROR_PIPE_BUSY（具名管道的例項數上限是 1）→ **UI 行程直接崩潰**。
        // 換一個新 client 同時也保證前一個 worker 被好好收掉，不會累積行程。
        ConversionClient? previous = _client;
        _client = new ConversionClient(_exePath, _log);
        _progress.Source = () => _client?.Snapshot ?? ConversionSnapshot.Idle;
        previous?.Dispose();

        try
        {
            // outputDir 與 toolOp 都是 string?，位置互換也能編譯——所以這兩個**一律具名**，
            // 不靠位置（見 ConversionClient.Start 的說明）。
            if (!_client.Start(sourcePaths, targetFormat, quality,
                               outputDir: outputDir, toolOp: toolOp, out string? error,
                               compression: compression))
            {
                _log($"conversion: worker did not start ({error}) -> reporting failure; UI stays usable");
                ReportStartFailure(sourcePaths.Length);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // 輪盤是常駐的，**任何**例外都不可以讓它消失：出錯就回報，程式繼續活著（§7.2）。
            _log($"conversion: start threw {ex.GetType().Name}: {ex.Message} -> reporting failure");
            ReportStartFailure(sourcePaths.Length);
            return false;
        }
    }

    private void ReportStartFailure(int fileCount) =>
        _progress.Update(new ConversionSnapshot(0, fileCount, null, true, 0, fileCount,
                                                Array.Empty<string>(), "轉檔服務無法啟動"));

    /// <summary>等整批結束（診斷與自動驗證用；正常 UI 不呼叫它）。</summary>
    public ConversionSnapshot Wait(int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !Snapshot.Finished)
        {
            Native.PumpMessages();
            Thread.Sleep(10);
        }
        return Snapshot;
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}
