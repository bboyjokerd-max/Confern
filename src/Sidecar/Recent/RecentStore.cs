using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidecar;

/// <summary>
/// 一次成功的轉檔（托盤「最近轉檔」的一列，也是 `confern recent --list` 的一行）。
///
/// 欄位刻意只有四個：**輸出檔**（使用者真正想再打開的東西）、來源檔（想知道「這是從哪來的」時
/// 唯一問得到的地方）、工具（哪一顆扇形做的，`convert`／`watch`／`rotate90`…）、時間（排序用）。
/// 不存縮圖、不存檔案大小：縮圖會讓這個檔變成第二個狀態擁有者，而大小下次開檔就知道了。
/// </summary>
internal sealed record RecentEntry(string SourcePath, string OutputPath, string Tool, DateTime At)
{
    /// <summary>選單上顯示的名字。**只有檔名**：完整路徑在選單裡看不完，而且路徑本身不是選單要回答的問題。</summary>
    [JsonIgnore]
    public string DisplayName => Path.GetFileName(OutputPath);
}

/// <summary>
/// 「最近轉檔」的儲存——`%LOCALAPPDATA%\Confern\recent.json`。
///
/// 為什麼是**另一個檔**而不是 `settings.json` 的一個鍵：
///   settings.json 是**使用者改的設定**（每一列在設定頁上都有對應的 UI，改壞了刪掉就回預設）；
///   最近轉檔是**程式自己寫的紀錄**，會一直被覆寫、最多 12 筆、刪掉完全沒有副作用。
///   兩者混在一個檔裡，等於讓「使用者手改的檔案」每次轉檔都被我們重寫一次——那是資料損壞的溫床。
///
/// 為什麼不進 worker 行程：轉檔在 worker，但**使用者看的那個行程**才是寫紀錄的人。
/// 兩個行程同時寫同一個檔，遲早會出現「上一次的紀錄把這一次蓋掉」的情形，
/// 而那種 bug 只在有兩個 worker 同時跑時才看得到。
///
/// 失敗語意（與本專案其他地方一致）：**寫不進去要大聲記錄，但不准讓轉檔失敗**。
/// 使用者要的是檔案，紀錄只是方便；為了一筆紀錄而讓一次成功的轉檔回報失敗，是本末倒置。
/// </summary>
internal sealed class RecentStore
{
    /// <summary>留幾筆。12 ＝ 一輪工作看得完的量；再多就變成「第二個檔案總管」。</summary>
    public const int MaxEntries = 12;

    /// <summary>托盤子選單最多顯示幾筆（選單比檔案長就不好按）。</summary>
    public const int MenuEntries = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private List<RecentEntry> _entries;
    private bool _warned;

    /// <summary>
    /// 上一次「這份記憶體清單和磁碟上的檔案一致」時的檔案時戳＋長度（不存在時為 null）。
    /// 用來判斷「別的行程是不是改過它」——見 <see cref="ReloadIfChangedOnDisk"/>。
    /// </summary>
    private DateTime _loadedWriteUtc;
    private long _loadedLength;

    private static RecentStore? _shared;

    public RecentStore(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        _entries = Load();
        RememberFileState();
    }

    /// <summary>把「現在磁碟上的樣子」記下來（載入或寫入之後都要呼叫）。</summary>
    private void RememberFileState()
    {
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists)
            {
                _loadedWriteUtc = info.LastWriteTimeUtc;
                _loadedLength = info.Length;
            }
            else
            {
                _loadedWriteUtc = DateTime.MinValue;
                _loadedLength = -1;
            }
        }
        catch
        {
            _loadedWriteUtc = DateTime.MinValue;
            _loadedLength = -1;
        }
    }

    /// <summary>
    /// 檔案被別的行程改過就重讀（呼叫端必須已持有 <see cref="_gate"/>）。
    ///
    /// <para>為什麼非做不可：托盤是**常駐**的行程，而 `confern convert`、`confern watch` 都可能
    /// 在它還活著的時候寫這份檔案。只在建構時讀一次的話，子選單會永遠停在啟動那一刻的內容，
    /// 而每一個行程自己都覺得自己很正常——這種「兩邊都對、合起來是錯的」最難查。</para>
    ///
    /// <para>用「時戳＋長度」而不是內容比對：每次跳選單都重讀整個檔案是可以接受的（最多 12 筆），
    /// 但沒變的時候連讀都不必。</para>
    /// </summary>
    private void ReloadIfChangedOnDisk()
    {
        try
        {
            var info = new FileInfo(_path);
            bool exists = info.Exists;
            DateTime writeUtc = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            long length = exists ? info.Length : -1;
            if (writeUtc == _loadedWriteUtc && length == _loadedLength) return;

            _entries = Load();
            RememberFileState();
            _log?.Invoke($"recent: reloaded '{_path}' (another process changed it) count={_entries.Count}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"recent: could not check '{_path}' ({ex.GetType().Name}: {ex.Message}); keeping the loaded list");
        }
    }

    /// <summary>`%LOCALAPPDATA%\Confern\recent.json`——與 settings.json／temps.json 同一個資料夾。</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.Name, "recent.json");

    /// <summary>行程共用的那一個（托盤、worker 結果、監看資料夾都寫這一個）。</summary>
    public static RecentStore Shared(Action<string>? log = null)
    {
        if (_shared != null) return _shared;
        lock (typeof(RecentStore))
        {
            _shared ??= new RecentStore(DefaultPath(), log);
            return _shared;
        }
    }

    /// <summary>測試用：把共用的那一個換掉（自測要一個乾淨的 store，不能動使用者的紀錄）。</summary>
    public static void ReplaceSharedForTests(RecentStore? store)
    {
        lock (typeof(RecentStore)) { _shared = store; }
    }

    public string FilePath => _path;

    /// <summary>
    /// 上一次 <see cref="Load"/> 為什麼失敗（成功時為 null）。
    ///
    /// <para>為什麼要留著：紀錄檔壞掉時程式**照常運作**（退回空清單），但使用者需要知道
    /// 「你的紀錄沒了、原因是這個」——尤其是 `--quiet`／子命令模式下日誌一行都不會印，
    /// 沒有這個欄位，命令列就只會安靜地說「total=0」，看起來像「清單本來就是空的」。</para>
    /// </summary>
    public string? LoadError { get; private set; }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// 最新的在前面，最多 <paramref name="limit"/> 筆，而且**已經不存在的檔案不會出現**。
    ///
    /// 為什麼要清：選單上列著一個點下去打不開的檔，比沒有這個功能更糟（§7.2 禁止靜默失敗）。
    /// 清理時**寫回檔案**並記一行日誌，否則每次開選單都會重算同一件事。
    /// </summary>
    public IReadOnlyList<RecentEntry> Entries(int limit = MaxEntries)
    {
        List<RecentEntry> snapshot;
        bool pruned = false;
        lock (_gate)
        {
            // 先看檔案有沒有被**別的行程**改過：托盤是常駐的，而 `confern convert`／監看資料夾
            // 可能是另一個行程寫的。不重讀的話，托盤子選單會一直停在「程式啟動時的那份清單」，
            // 那正是使用者眼中「這功能壞了」的樣子（而每個行程自己都覺得自己很正常）。
            ReloadIfChangedOnDisk();
            snapshot = new List<RecentEntry>(_entries);
        }

        var alive = new List<RecentEntry>(snapshot.Count);
        foreach (RecentEntry entry in snapshot)
        {
            bool exists;
            try { exists = File.Exists(LongPath.Prepare(entry.OutputPath)); }
            catch { exists = false; }
            if (exists) alive.Add(entry);
            else
            {
                pruned = true;
                _log?.Invoke($"recent: dropping missing output '{entry.OutputPath}'");
            }
        }

        if (pruned)
        {
            lock (_gate)
            {
                // 只接受「還是同一組資料」的情況：這段期間若有新的轉檔進來，就以新的為準。
                if (_entries.Count == snapshot.Count) { _entries = alive; Save(); }
                else return Entries(limit);
            }
        }

        if (limit < alive.Count) return alive.GetRange(0, Math.Max(0, limit));
        return alive;
    }

    /// <summary>
    /// 記一筆。回傳「有沒有真的寫進去」——呼叫端不需要看它，但自測要看，
    /// 因為「寫不進去卻回報成功」正是紀錄類功能最典型的謊。
    ///
    /// 去重的鍵是**輸出路徑**（不是來源）：同一張圖轉兩次 PNG 會產生 `a.png`／`a (2).png`
    /// 兩個真的存在的檔，兩筆都該留；而在同一個輸出路徑上重複登記同一件事則只留最新那一筆。
    /// </summary>
    public bool Add(string sourcePath, string outputPath, string tool)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return false;

        try
        {
            if (!File.Exists(LongPath.Prepare(outputPath)))
            {
                // 呼叫端說「成功了」卻沒有檔：不記（寧可少一筆，也不要記一個假路徑）。
                _log?.Invoke($"recent: not recording '{outputPath}' (the file does not exist)");
                return false;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"recent: not recording '{outputPath}' ({ex.GetType().Name}: {ex.Message})");
            return false;
        }

        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, new RecentEntry(sourcePath ?? string.Empty, outputPath, tool ?? "convert", DateTime.Now));
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            bool ok = Save();
            _log?.Invoke($"recent: recorded '{outputPath}' tool={tool} count={_entries.Count} saved={ok}");
            return ok;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            int had = _entries.Count;
            _entries = new List<RecentEntry>();
            bool ok = Save();
            _log?.Invoke($"recent: cleared ({had} entries removed) saved={ok}");
        }
    }

    private bool Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_entries, JsonOptions);
            // 走**同一條**原子寫入路徑，不另寫第二套：這個 repo 對「不留 .tmp-*」的保證只有一份實作。
            // overwrite: true —— 這是程式自己的檔案，覆寫是意圖（使用者的輸出檔仍然是「永不覆寫」）。
            AtomicWriter.Write(_path, stream =>
            {
                // leaveOpen: true —— StreamWriter 預設會**接手**那個 stream，Dispose 時把它關掉，
                // 於是 AtomicWriter 後面的 `stream.Flush(flushToDisk: true)` 就丟
                // ObjectDisposedException。實測日誌長這樣：
                //   WARNING recent: could not save '…\recent.json' (ObjectDisposedException:
                //   Cannot access a closed file.); conversion is unaffected
                // 症狀是「每次轉檔都說 recorded，saved=False，清單永遠是空的」。
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false),
                                                    bufferSize: 1024, leaveOpen: true);
                writer.Write(json);
            }, overwrite: true);
            // 寫完就把「磁碟上的樣子」記下來：否則下一次 Entries() 會把自己剛才的寫入
            // 誤判成「別的行程改的」而多讀一次檔（無害，但那種日誌會讓人以為有第二個寫入者）。
            RememberFileState();
            return true;
        }
        catch (Exception ex)
        {
            // 只抱怨一次：一個寫不進去的紀錄檔不值得在每一次轉檔時洗一行日誌。
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"WARNING recent: could not save '{_path}' ({ex.GetType().Name}: {ex.Message}); " +
                             "conversion is unaffected");
            }
            return false;
        }
    }

    private List<RecentEntry> Load()
    {
        LoadError = null;
        try
        {
            if (!File.Exists(_path)) return new List<RecentEntry>();
            var loaded = JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(_path));
            if (loaded == null)
            {
                LoadError = "the file holds no list";
                return new List<RecentEntry>();
            }
            // 壞掉的一兩筆不該讓整個清單消失：逐筆過濾，留下讀得懂的。
            var cleaned = new List<RecentEntry>(loaded.Count);
            foreach (RecentEntry? e in loaded)
            {
                if (e == null) continue;
                if (string.IsNullOrWhiteSpace(e.OutputPath)) continue;
                cleaned.Add(e);
            }
            return cleaned.Count > MaxEntries ? cleaned.GetRange(0, MaxEntries) : cleaned;
        }
        catch (Exception ex)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
            _log?.Invoke($"recent: '{_path}' is unreadable ({LoadError}); starting empty");
            return new List<RecentEntry>();
        }
    }

    /// <summary>
    /// 用使用者的預設程式打開一個檔（托盤選單點下去就是這件事）。
    ///
    /// <c>UseShellExecute = true</c> 是關鍵：少了它 <see cref="System.Diagnostics.Process"/> 會直接
    /// CreateProcess 那個路徑，PNG 沒有執行權限 → 靜默失敗。開不起來要**回報 false**，
    /// 因為「點了沒反應」正是托盤選單最常見的抱怨。
    /// </summary>
    public static bool OpenWithShell(string path, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(LongPath.Prepare(path)))
            {
                log?.Invoke($"recent: cannot open '{path}' (the file is gone)");
                return false;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            log?.Invoke($"recent: opened '{path}' with the default program");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"recent: could not open '{path}' ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }
}
