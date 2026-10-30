using System.Diagnostics;

namespace Sidecar;

/// <summary>
/// 監看一個資料夾：**新**進來的圖片自動轉檔（2026-10-22）。
///
/// <para>與輪盤／命令列的關係：它不新增任何轉檔邏輯。每一張圖仍然走
/// <see cref="ConversionEngine.Convert"/>——同一條解碼→編碼→原子寫入→命名碰撞的路徑，
/// 所以「永不覆寫」「不留 .tmp-」「失敗要說出原因」對監看模式一樣成立。這裡只決定
/// **什麼時候**把哪個檔案交給引擎。</para>
///
/// <para>兩個設計決定值得寫下來（都是量測／踩過的，不是偏好）：</para>
/// <list type="number">
/// <item><b>用輪詢決定「寫完了沒」，不是靠 <see cref="FileSystemWatcher"/> 的事件。</b>
/// FSW 只告訴你「有東西動了」，它**不知道**那個檔案是不是還在被複製；而且它在緩衝區
/// 滿了會丟事件（目錄很忙時真的會漏）。輪詢的判準很直接：同一個檔案連續一段時間
/// （<see cref="WatchRules.DefaultSettleMs"/>）大小與時間戳都沒變，才交給引擎。
/// 代價是最多慢一個輪詢間隔，換來的是「不會轉到半個檔」。</item>
/// <item><b>啟動時已經在資料夾裡的檔案一律忽略。</b> 否則第一次啟動就會把整個資料夾
/// （可能是幾百張舊照片）轉一遍——那不是使用者按下去的事。會寫一行說明，不是靜悄悄。</item>
/// </list>
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    private readonly WatchFolderSpec _spec;
    private readonly ConversionEngine _engine;
    private readonly TempJournal _journal;
    private readonly Action<string> _log;
    private readonly Action<ConversionResult> _onResult;
    private readonly int _settleMs;
    private readonly int _quality;

    private readonly object _gate = new();
    private readonly Dictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已經處理完的檔案（含被我們自己寫出來的輸出）——用來擋住自我餵食。</summary>
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 每個檔案試過幾次。失敗**不是**終點：一個「安靜了 400 ms、其實還在被寫」的檔案會被判成
    /// 「寫完了」而轉出 `不支援的格式`（實測 2026-10-22：900 ms 的停頓比 settle 長，所以引擎
    /// 收到的是半個 PNG）。那一刻的判斷是合理的，但檔案之後還會變，所以它必須能再被試一次；
    /// 上限是為了讓真正壞掉的檔案停手，而不是每 400 ms 失敗一次把日誌吃光。
    /// </summary>
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxAttempts = 3;

    // 明確寫出 System.Threading：這個專案同時 using 了 System.Windows.Forms，
    // 裸寫 Timer 會變成模稜兩可（建置錯誤 CS0104，2026-10-22）。
    private System.Threading.Timer? _timer;
    private int _busy;
    private bool _disposed;

    private struct Seen
    {
        public long Length;
        public DateTime Stamp;
        public DateTime QuietSince;
    }

    /// <summary>成功轉出的張數（給呼叫端在收尾時印一行；不參與任何斷言）。</summary>
    public int Converted { get; private set; }

    /// <summary>失敗的張數。</summary>
    public int Failed { get; private set; }

    public FolderWatcher(WatchFolderSpec spec, ConversionEngine engine, TempJournal journal,
                         int quality, Action<string> log, Action<ConversionResult> onResult,
                         int settleMs = WatchRules.DefaultSettleMs)
    {
        _spec = spec;
        _engine = engine;
        _journal = journal;
        _quality = quality;
        _log = log;
        _onResult = onResult;
        _settleMs = Math.Clamp(settleMs, WatchRules.MinSettleMs, WatchRules.MaxSettleMs);
    }

    /// <summary>
    /// 開始監看。既有的檔案記成「已看過」，所以只有**之後**出現的檔案會被轉。
    /// </summary>
    public void Start()
    {
        int existing = 0;
        foreach (string file in Enumerate())
        {
            lock (_gate) { _done.Add(file); }
            existing++;
        }
        if (existing > 0)
        {
            _log($"watch: {existing} file(s) already in '{_spec.Path}' are ignored " +
                 "(only files that appear AFTER this point are converted)");
        }

        _log($"watch: polling '{_spec.Path}' every {WatchRules.PollMs} ms, settling {_settleMs} ms, " +
             $"target={_spec.Target.ToUpperInvariant()} quality={_quality}");
        _timer = new System.Threading.Timer(_ => Tick(), null, WatchRules.PollMs, WatchRules.PollMs);
    }

    private IEnumerable<string> Enumerate()
    {
        string[] files;
        try { files = Directory.GetFiles(_spec.Path); }
        catch (Exception ex)
        {
            _log($"watch: cannot read '{_spec.Path}' ({ex.GetType().Name}: {ex.Message})");
            return Array.Empty<string>();
        }
        return files;
    }

    /// <summary>
    /// 一個輪詢週期。**一次只轉一張**：轉檔本身會佔用執行緒（去背可以好幾秒），
    /// 同時開多條只會讓「安靜判定」與 CPU 互相干擾，而監看的場合本來就沒有即時性要求。
    /// 其餘的檔案下一個週期再來（它們的 QuietSince 還在，不會被忘記）。
    /// </summary>
    private void Tick()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var catalog = new CodecRegistry();
            DateTime now = DateTime.UtcNow;
            string? picked = null;
            DateTime pickedSince = DateTime.MaxValue;

            foreach (string file in Enumerate())
            {
                // 只看這一支 exe 認得的副檔名。PDF 之類的「工具專用」格式不在這裡，
                // 因為它們沒有格式轉換目標（工具不是轉檔）。
                if (catalog.FormatOfPath(file) == null) continue;

                if (IsDone(file)) continue;

                var info = new FileInfo(file);
                if (!info.Exists) continue;

                lock (_gate)
                {
                    if (!_seen.TryGetValue(file, out Seen seen)
                        || seen.Length != info.Length
                        || seen.Stamp != info.LastWriteTimeUtc)
                    {
                        // 還在變（或第一次看到）→ 重新計時，這一輪不碰它。
                        _seen[file] = new Seen
                        {
                            Length = info.Length,
                            Stamp = info.LastWriteTimeUtc,
                            QuietSince = now,
                        };
                        continue;
                    }

                    if (now - seen.QuietSince < TimeSpan.FromMilliseconds(_settleMs)) continue;

                    // 安靜夠久了：挑「最早安靜下來」的那一張，順序與到達順序一致。
                    if (seen.QuietSince < pickedSince)
                    {
                        picked = file;
                        pickedSince = seen.QuietSince;
                    }
                }
            }

            if (picked != null) Convert(picked);
        }
        catch (Exception ex)
        {
            // 監看執行緒上的例外**絕不能**往上冒：Timer 的回呼一旦拋，整個行程就再也不會
            // 被這個計時器叫醒（而且行程看起來還活著）。記一行，下一輪繼續。
            _log($"watch: tick failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private bool IsDone(string file)
    {
        lock (_gate) { return _done.Contains(file); }
    }

    private void Convert(string file)
    {
        ConversionResult result;
        try
        {
            result = _engine.Convert(file, _spec.Target.ToUpperInvariant(), _quality, _journal,
                                     _spec.OutFolder.Length > 0 ? _spec.OutFolder : null);
        }
        catch (Exception ex)
        {
            lock (_gate) { _done.Add(file); }
            Failed++;
            _log($"watch: '{Path.GetFileName(file)}' threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            _done.Add(file);
            // 輸出也要記成「已處理」：預設就是寫回同一個資料夾，不擋的話它會在下一個週期
            // 被當成新檔案再轉一次（每轉一次多一個 `(N)` 後綴，直到目錄爆掉）。
            if (result.Ok && result.OutputPath != null) _done.Add(result.OutputPath);
        }

        // 監看資料夾轉出來的檔也要出現在托盤的「最近轉檔」：使用者在那裡看不到它，就只能自己回想
        // 「剛剛丟進去的東西跑去哪了」。登記在鎖外面（store 自己有自己的鎖，而且寫檔會慢一點）。
        if (result.Ok && result.OutputPath != null && Converted >= 0)
            RecentStore.Shared(_log).Add(file, result.OutputPath, "watch");

        if (result.Ok) Converted++;
        else
        {
            Failed++;
            int attempts;
            lock (_gate)
            {
                _attempts.TryGetValue(file, out attempts);
                attempts++;
                _attempts[file] = attempts;
                // 放回「還沒處理」＝它會被重新計時，只有在安靜夠久（＝真的變過而且停下來）之後
                // 才會再試一次。達到上限就維持在 _done 裡，不再回頭。
                if (attempts < MaxAttempts)
                {
                    _done.Remove(file);
                    _seen.Remove(file);
                }
            }
            if (attempts >= MaxAttempts)
                _log($"watch: giving up on {Path.GetFileName(file)} after {attempts} attempt(s) " +
                     $"(message={result.UserMessage})");
        }
        _log(result.Ok
            ? $"watch: converted {Path.GetFileName(file)} -> {Path.GetFileName(result.OutputPath)} " +
              $"({_spec.Target.ToUpperInvariant()}) detail={result.Detail}"
            : $"watch: FAILED {Path.GetFileName(file)} message={result.UserMessage} detail={result.Detail}");
        _onResult(result);
    }

    /// <summary>
    /// 停止監看。會等手上那一張轉完（最多等到它結束），避免行程離開時留下半個檔。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;

        // 等手上的轉檔結束：_busy 會在 finally 裡歸零。上限 10 秒——超過就放掉，
        // 因為那時使用者在等關機，而引擎自己的原子寫入本來就不會留下半個輸出檔。
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _busy) != 0 && sw.ElapsedMilliseconds < 10_000)
            Thread.Sleep(20);
    }
}
