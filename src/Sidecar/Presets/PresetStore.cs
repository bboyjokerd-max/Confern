using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidecar;

/// <summary>
/// 「這種來源格式，預設轉成什麼」——一筆設定。
///
/// <para>欄位刻意只有三個：來源格式（`PNG`）、目標格式（`WEBP`）、品質（`0`＝照設定頁，
/// 只有 <see cref="CompressionPolicy.QualityControlsSize"/> 說「有品質旋鈕」的格式才准寫）。
/// 不放路徑、不放資料夾——那些是監看清單與設定頁的事，兩份擁有者遲早會吵架。</para>
/// </summary>
/// <param name="SourceFormat">來源格式（大寫，例如 <c>PNG</c>）。</param>
/// <param name="Target">輪盤一開始要選中的目標格式（大寫）。</param>
/// <param name="Quality">0＝不指定（用設定頁的品質）；其餘為 60–100，且只有 JPG／WEBP 有意義。</param>
internal sealed record Preset(string SourceFormat, string Target, int Quality)
{
    /// <summary>日誌與清單共用的一行。</summary>
    public string Describe() =>
        Quality > 0 ? $"{SourceFormat} -> {Target} (quality {Quality})" : $"{SourceFormat} -> {Target}";
}

/// <summary>
/// 每種來源格式記住「上次／慣用的目標格式」（2026-10-23）。
///
/// <para><b>它解決什麼</b>：使用者每次都拖 HEIC，每次都要把游標移到 WEBP 那一格。輪盤本身很好用，
/// 但「我每次都是這個選擇」這件事只有使用者知道——所以它是一個**設定**，不是一個猜測。
/// 有設定時輪盤一開始就停在那顆扇形上；使用者仍然可以自己選別的（這不是鎖）。</para>
///
/// <para><b>為什麼是獨立檔案</b>（<c>%LOCALAPPDATA%\Confern\presets.json</c>）而不是塞進
/// `settings.json`：設定頁的九列與它的字串鍵都是被驗收釘住的（`m1f` 逐鍵數），而這個功能**不需要
/// 動任何畫面**。獨立檔案讓「零 UI 改動」變成可檢查的事實，而不是承諾。與 `recent.json` 同一個
/// 資料夾、同一組規矩（原子寫入、壞檔退回空清單並說原因、被別的行程改過就重讀）。</para>
///
/// <para><b>環境變數 <c>CONFERN_PRESETS</c></b> 會蓋掉檔案路徑：驗收套件會設定/清掉預設，
/// 那些動作不該動到使用者真正的偏好（與 <c>CONFERN_RECENT</c> 同一條理由與同一個手法）。</para>
/// </summary>
internal sealed class PresetStore
{
    /// <summary>上限：一個來源格式一筆，而這一版能開的來源格式只有幾個。留一點餘裕給未來。</summary>
    public const int MaxEntries = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private List<Preset> _presets;
    private bool _warned;
    private DateTime _loadedWriteUtc;
    private long _loadedLength;

    private static PresetStore? _shared;

    public PresetStore(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        _presets = Load();
        RememberFileState();
    }

    /// <summary>`%LOCALAPPDATA%\Confern\presets.json`，或 <c>CONFERN_PRESETS</c> 指定的路徑。</summary>
    public static string DefaultPath()
    {
        string? overridden = Environment.GetEnvironmentVariable("CONFERN_PRESETS");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            ProductInfo.Name, "presets.json");
    }

    /// <summary>行程共用的那一個（托盤顯示輪盤時與命令列都用它）。</summary>
    public static PresetStore Shared(Action<string>? log = null)
    {
        if (_shared != null) return _shared;
        lock (typeof(PresetStore))
        {
            _shared ??= new PresetStore(DefaultPath(), log);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONFERN_PRESETS")))
                log?.Invoke($"presets: using '{_shared.FilePath}' (CONFERN_PRESETS overrides the user file)");
            return _shared;
        }
    }

    /// <summary>測試用：把共用的那一個換掉（自測要一個乾淨的 store，不能動使用者的偏好）。</summary>
    public static void ReplaceSharedForTests(PresetStore? store)
    {
        lock (typeof(PresetStore)) { _shared = store; }
    }

    public string FilePath => _path;

    /// <summary>上一次 <see cref="Load"/> 為什麼失敗（成功時為 null）。理由與 `RecentStore` 相同。</summary>
    public string? LoadError { get; private set; }

    public int Count
    {
        get { lock (_gate) { ReloadIfChangedOnDisk(); return _presets.Count; } }
    }

    /// <summary>全部預設（依來源格式排序，讓輸出穩定可斷言）。</summary>
    public IReadOnlyList<Preset> All()
    {
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            var copy = new List<Preset>(_presets);
            copy.Sort((a, b) => string.CompareOrdinal(a.SourceFormat, b.SourceFormat));
            return copy;
        }
    }

    /// <summary>這個來源格式有沒有預設（大小寫不拘）。</summary>
    public Preset? For(string? sourceFormat)
    {
        if (string.IsNullOrWhiteSpace(sourceFormat)) return null;
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            foreach (Preset p in _presets)
                if (string.Equals(p.SourceFormat, sourceFormat, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }

    /// <summary>寫入／覆蓋一筆。回傳「有沒有真的寫進檔案」——呼叫端要能分辨「存了」與「說了」。</summary>
    public bool Set(string sourceFormat, string target, int quality = 0)
    {
        if (string.IsNullOrWhiteSpace(sourceFormat) || string.IsNullOrWhiteSpace(target)) return false;
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            var entry = new Preset(sourceFormat.Trim().ToUpperInvariant(),
                                   target.Trim().ToUpperInvariant(),
                                   Math.Max(0, quality));
            int at = _presets.FindIndex(p => string.Equals(p.SourceFormat, entry.SourceFormat,
                                                           StringComparison.OrdinalIgnoreCase));
            if (at >= 0) _presets[at] = entry;
            else
            {
                if (_presets.Count >= MaxEntries)
                {
                    _log?.Invoke($"presets: refusing to add '{entry.Describe()}' (the list is full at {MaxEntries})");
                    return false;
                }
                _presets.Add(entry);
            }
            bool saved = Save();
            _log?.Invoke($"presets: set '{entry.Describe()}' count={_presets.Count} saved={saved}");
            return saved;
        }
    }

    /// <summary>移除一個來源格式的預設。回傳「本來有沒有這一筆」。</summary>
    public bool Remove(string sourceFormat)
    {
        if (string.IsNullOrWhiteSpace(sourceFormat)) return false;
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            int removed = _presets.RemoveAll(p => string.Equals(p.SourceFormat, sourceFormat,
                                                                StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Save();
            _log?.Invoke($"presets: removed '{sourceFormat.Trim().ToUpperInvariant()}' count={_presets.Count}");
            return true;
        }
    }

    /// <summary>清空。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _presets.Clear();
            Save();
            _log?.Invoke($"presets: cleared '{_path}'");
        }
    }

    /// <summary>
    /// 這次轉檔該用哪個品質。**這是唯一一份優先序**：
    /// 命令列明打 <c>--quality</c> &gt; 這個來源格式的預設 &gt; 設定頁的品質。
    ///
    /// <para>為什麼不是「預設永遠贏」：使用者在命令列打了 `--quality 95` 就是那一次要 95，
    /// 悄悄被一份存下來的預設蓋掉是那種「看起來成功了但不是我要求的」的錯。</para>
    /// </summary>
    public int QualityFor(string? sourceFormat, string targetFormat, int fallback)
    {
        if (!CompressionPolicy.QualityControlsSize(targetFormat)) return fallback;
        Preset? preset = For(sourceFormat);
        if (preset == null) return fallback;
        if (!string.Equals(preset.Target, targetFormat, StringComparison.OrdinalIgnoreCase)) return fallback;
        return preset.Quality > 0 ? preset.Quality : fallback;
    }

    private void RememberFileState()
    {
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists) { _loadedWriteUtc = info.LastWriteTimeUtc; _loadedLength = info.Length; }
            else { _loadedWriteUtc = DateTime.MinValue; _loadedLength = -1; }
        }
        catch
        {
            _loadedWriteUtc = DateTime.MinValue;
            _loadedLength = -1;
        }
    }

    /// <summary>檔案被別的行程改過就重讀（呼叫端必須已持有 <see cref="_gate"/>）。理由見 <see cref="RecentStore"/>。</summary>
    private void ReloadIfChangedOnDisk()
    {
        try
        {
            var info = new FileInfo(_path);
            bool exists = info.Exists;
            DateTime writeUtc = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            long length = exists ? info.Length : -1;
            if (writeUtc == _loadedWriteUtc && length == _loadedLength) return;
            _presets = Load();
            RememberFileState();
            _log?.Invoke($"presets: reloaded '{_path}' (another process changed it) count={_presets.Count}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"presets: could not check '{_path}' ({ex.GetType().Name}: {ex.Message}); keeping the loaded list");
        }
    }

    private bool Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_presets, JsonOptions);
            // leaveOpen: true —— StreamWriter 預設會接手 stream，Dispose 後 AtomicWriter 的
            // Flush(flushToDisk) 會丟 ObjectDisposedException（`recent.json` 就是這樣壞過一次）。
            AtomicWriter.Write(_path, stream =>
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false),
                                                    bufferSize: 1024, leaveOpen: true);
                writer.Write(json);
            }, overwrite: true);
            RememberFileState();
            return true;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"WARNING presets: could not save '{_path}' ({ex.GetType().Name}: {ex.Message}); " +
                             "conversion is unaffected");
            }
            return false;
        }
    }

    private List<Preset> Load()
    {
        LoadError = null;
        try
        {
            if (!File.Exists(_path)) return new List<Preset>();
            var loaded = JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(_path));
            if (loaded == null)
            {
                LoadError = "the file holds no list";
                return new List<Preset>();
            }
            // 壞掉的一兩筆不該讓整組預設消失：逐筆過濾，留下讀得懂的。
            var cleaned = new List<Preset>(loaded.Count);
            foreach (Preset? p in loaded)
            {
                if (p == null) continue;
                if (string.IsNullOrWhiteSpace(p.SourceFormat)) continue;
                if (string.IsNullOrWhiteSpace(p.Target)) continue;
                cleaned.Add(new Preset(p.SourceFormat.Trim().ToUpperInvariant(),
                                       p.Target.Trim().ToUpperInvariant(),
                                       Math.Clamp(p.Quality, 0, CodecRegistry.MaxEncodeQuality)));
            }
            return cleaned.Count > MaxEntries ? cleaned.GetRange(0, MaxEntries) : cleaned;
        }
        catch (Exception ex)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
            _log?.Invoke($"presets: '{_path}' is unreadable ({LoadError}); starting empty");
            return new List<Preset>();
        }
    }
}
