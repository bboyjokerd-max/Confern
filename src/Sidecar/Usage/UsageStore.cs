using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidecar;

/// <summary>
/// 試用額度：哪些功能、免費給幾次。
///
/// <para><b>只有一個地方可以改數字</b>。次數會出現在設定檔、日誌、命令列與驗收斷言裡，
/// 分散寫死的結果是「改了程式、說明還寫 10 次」——而那種不一致不會有任何錯誤訊息。</para>
/// </summary>
internal static class TrialPolicy
{
    /// <summary>去背（CUTOUT）的免費次數。</summary>
    public const int CutoutFreeUses = 10;

    /// <summary>去背的額度識別字（小寫，會寫進 `usage.json`）。</summary>
    public const string Cutout = "cutout";

    /// <summary>目前只有一個收費功能；不認得的識別字＝沒有額度（不會意外送人）。</summary>
    public static int LimitFor(string feature) => feature switch
    {
        Cutout => CutoutFreeUses,
        _ => 0
    };

    /// <summary>把識別字正規化（大小寫不拘）。不認得就回空字串。</summary>
    public static string Canonical(string? feature)
    {
        if (string.IsNullOrWhiteSpace(feature)) return "";
        string f = feature.Trim().ToLowerInvariant();
        return LimitFor(f) > 0 ? f : "";
    }

    /// <summary>全部收費功能的識別字（診斷與報告用）。</summary>
    public static IReadOnlyList<string> Features { get; } = new[] { Cutout };
}

/// <summary>
/// 試用次數的記帳本（`%LOCALAPPDATA%\Confern\usage.json`）。
///
/// <para><b>它記什麼、不記什麼</b>：只記「每個收費功能被成功用掉幾次」與第一次／最後一次的時間。
/// 不記檔案、不記路徑、不記時間軸——那不是計價需要的東西，而多記的每一個欄位都是隱私下車。</para>
///
/// <para><b>為什麼要 HMAC</b>：這是一份使用者電腦上的純文字檔，用記事本把 `3` 改成 `0` 是十秒鐘的事。
/// 簽章讓「改數字」變成「檔案整份失效」——而失效的政策是**從嚴**（見 <see cref="Tampered"/>），
/// 因為那比「改了就免費」誠實。刪掉整份檔案仍然是重來一次（本機記帳不可能擋住，
/// 也不值得為它做 DRM）；真正的門是把付費做順，不是把破解做難。</para>
///
/// <para><b>環境變數 <c>CONFERN_USAGE</c></b> 會蓋掉路徑：驗收套件必須能指向暫存檔，
/// 否則跑一次測試就會吃掉使用者真正的免費次數（與 <c>CONFERN_PRESETS</c>／<c>CONFERN_RECENT</c> 同一條理由）。</para>
/// </summary>
internal sealed class UsageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private Dictionary<string, int> _used = new(StringComparer.Ordinal);
    private DateTime _loadedWriteUtc;
    private long _loadedLength;

    private static UsageStore? _shared;

    public UsageStore(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        LoadFromDisk();
    }

    /// <summary>`%LOCALAPPDATA%\Confern\usage.json`，或 <c>CONFERN_USAGE</c> 指定的路徑。</summary>
    public static string DefaultPath()
    {
        string? overridden = Environment.GetEnvironmentVariable("CONFERN_USAGE");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        return Path.Combine(ProductInfo.DataDirectory, "usage.json");
    }

    /// <summary>行程共用的那一個（托盤選輪盤時與命令列都讀同一份）。</summary>
    public static UsageStore Shared(Action<string>? log = null)
    {
        if (_shared != null) return _shared;
        lock (typeof(UsageStore))
        {
            _shared ??= new UsageStore(DefaultPath(), log);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONFERN_USAGE")))
                log?.Invoke($"usage: using '{_shared.FilePath}' (CONFERN_USAGE overrides the user file)");
            return _shared;
        }
    }

    /// <summary>測試用：換掉共用的那一個（自測要一個乾淨的記帳本，不能吃掉使用者的次數）。</summary>
    public static void ReplaceSharedForTests(UsageStore? store)
    {
        lock (typeof(UsageStore)) { _shared = store; }
    }

    public string FilePath => _path;

    /// <summary>
    /// 記帳本被改過（HMAC 對不上）。<c>true</c> 時**所有功能都視為用完**：
    /// 改數字不該比刪檔案更容易。原因字串放在 <see cref="TamperReason"/>。
    /// </summary>
    public bool Tampered { get; private set; }

    public string? TamperReason { get; private set; }

    /// <summary>用過幾次（呼叫端必須已持有鎖，或接受一份快照）。</summary>
    public int Used(string feature)
    {
        string key = TrialPolicy.Canonical(feature);
        if (key.Length == 0) return 0;
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            return _used.TryGetValue(key, out int n) ? n : 0;
        }
    }

    /// <summary>還剩幾次。被改過＝0（從嚴）。</summary>
    public int Remaining(string feature)
    {
        string key = TrialPolicy.Canonical(feature);
        if (key.Length == 0) return 0;
        int limit = TrialPolicy.LimitFor(key);
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            if (Tampered) return 0;
            int used = _used.TryGetValue(key, out int n) ? n : 0;
            return Math.Max(0, limit - used);
        }
    }

    /// <summary>
    /// 「現在能不能用」。<paramref name="remaining"/> 回的是**這次之前**還剩幾次——
    /// 呼叫端要在真的成功之後才叫 <see cref="RecordUse"/>，否則「模型不在」也會吃掉一次額度。
    /// </summary>
    public bool CanUse(string feature, out int remaining, out string why)
    {
        string key = TrialPolicy.Canonical(feature);
        remaining = 0;
        if (key.Length == 0)
        {
            why = $"'{feature}' is not a metered feature";
            return false;
        }
        int limit = TrialPolicy.LimitFor(key);
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            if (Tampered)
            {
                why = $"the usage record was modified outside the product; " +
                      $"the {key} trial is treated as used up " +
                      $"({Used(key)}/{limit}, {TamperReason})";
                return false;
            }
            int used = _used.TryGetValue(key, out int n) ? n : 0;
            remaining = Math.Max(0, limit - used);
            if (remaining <= 0)
            {
                why = $"the {key} trial is used up ({used}/{limit})";
                return false;
            }
            why = "";
            return true;
        }
    }

    /// <summary>記一次成功的使用。回傳「有沒有真的寫進檔案」。</summary>
    public bool RecordUse(string feature)
    {
        string key = TrialPolicy.Canonical(feature);
        if (key.Length == 0) return false;
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            int used = _used.TryGetValue(key, out int n) ? n : 0;
            _used[key] = used + 1;
            bool saved = Save();
            _log?.Invoke($"usage: {key} used {_used[key]}/{TrialPolicy.LimitFor(key)}" +
                         (saved ? "" : " (NOT SAVED -- the next run will not see this)"));
            return saved;
        }
    }

    /// <summary>
    /// 診斷接縫：直接把某個功能的次數設成 <paramref name="count"/>（驗收用它把額度推到用完，
    /// 而不必真的跑 10 次去背）。寫入時照樣簽章，所以驗收不必知道金鑰。
    /// </summary>
    public bool SetUsed(string feature, int count)
    {
        string key = TrialPolicy.Canonical(feature);
        if (key.Length == 0) return false;
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            _used[key] = Math.Max(0, count);
            Tampered = false;
            TamperReason = null;
            return Save();
        }
    }

    /// <summary>一行摘要（診斷、日誌、報告共用）。</summary>
    public string Describe()
    {
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            var parts = new List<string>();
            foreach (string f in TrialPolicy.Features)
            {
                int limit = TrialPolicy.LimitFor(f);
                int used = _used.TryGetValue(f, out int n) ? n : 0;
                // ⚠ 被改過時「還剩幾次」是 0，不是 limit-used：報告的兩行必須說同一件事
                // （`--usage-report` 就是靠這條對齊的，見 Remaining）。
                int remaining = Tampered ? 0 : Math.Max(0, limit - used);
                parts.Add($"{f} used={used} limit={limit} remaining={remaining}");
            }
            return string.Join("; ", parts) + $" tampered={Tampered}" +
                   (Tampered ? $" ({TamperReason})" : "");
        }
    }

    // ---------------------------------------------------------------- 檔案

    private void LoadFromDisk()
    {
        _used = new Dictionary<string, int>(StringComparer.Ordinal);
        Tampered = false;
        TamperReason = null;
        try
        {
            if (!File.Exists(_path)) { RememberFileState(); return; }
            string text = File.ReadAllText(_path);
            UsageFile? file = null;
            try { file = JsonSerializer.Deserialize<UsageFile>(text); }
            catch (JsonException ex)
            {
                Tampered = true;
                TamperReason = $"the file is not readable JSON ({ex.GetType().Name})";
                RememberFileState();
                return;
            }
            if (file == null)
            {
                Tampered = true;
                TamperReason = "the file holds no object";
                RememberFileState();
                return;
            }
            if (file.Used != null)
            {
                foreach (var pair in file.Used)
                {
                    string key = TrialPolicy.Canonical(pair.Key);
                    if (key.Length == 0) continue;      // 不認得的鍵留著不管，但不算額度
                    _used[key] = Math.Max(0, pair.Value);
                }
            }
            string expected = ComputeMac(_used);
            if (!string.Equals(file.Mac, expected, StringComparison.OrdinalIgnoreCase))
            {
                Tampered = true;
                TamperReason = file.Mac == null
                    ? "the file carries no signature"
                    : "the signature does not match the numbers in the file";
                _log?.Invoke($"WARNING usage: '{_path}' {TamperReason}; " +
                             "the trial is treated as used up (delete the file to start over)");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"WARNING usage: could not read '{_path}' ({ex.GetType().Name}: {ex.Message}); " +
                         "treating every feature as unused");
        }
        RememberFileState();
    }

    private bool Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var file = new UsageFile
            {
                Version = 1,
                Used = new Dictionary<string, int>(_used, StringComparer.Ordinal),
                Mac = ComputeMac(_used)
            };
            string json = JsonSerializer.Serialize(file, JsonOptions);
            AtomicWriter.Write(_path, stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false),
                                                    bufferSize: 1024, leaveOpen: true);
                writer.Write(json);
            }, overwrite: true);
            RememberFileState();
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"WARNING usage: could not save '{_path}' ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
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

    /// <summary>檔案被別的行程（或使用者）改過就重讀——兩個行程各記一次時不可以互相蓋掉。</summary>
    private void ReloadIfChangedOnDisk()
    {
        try
        {
            var info = new FileInfo(_path);
            bool exists = info.Exists;
            DateTime writeUtc = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            long length = exists ? info.Length : -1;
            if (writeUtc == _loadedWriteUtc && length == _loadedLength) return;
            LoadFromDisk();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"usage: could not check '{_path}' ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ---------------------------------------------------------------- 簽章

    /// <summary>
    /// 這台機器專屬的金鑰。**推導**而不是存起來：它只防「手改 JSON」，
    /// 不是密碼學上的秘密——逆向的人一定拿得到，而我們不打算為此做 DRM。
    /// </summary>
    private static byte[] Key()
    {
        string machine = MachineId();
        return SHA256.HashData(Encoding.UTF8.GetBytes("confern-usage-v1|" + machine));
    }

    private static string MachineId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) return guid;
        }
        catch
        {
            // 讀不到就退回機器名——同一個使用者同一台機器仍然穩定，只是換機器名會失效。
        }
        return Environment.MachineName;
    }

    /// <summary>簽章涵蓋的內容：版本、排序後的「功能＝次數」。時間刻意不簽（它不是額度）。</summary>
    private static string ComputeMac(Dictionary<string, int> used)
    {
        var sb = new StringBuilder("v1");
        foreach (string feature in used.Keys.OrderBy(k => k, StringComparer.Ordinal))
            sb.Append('|').Append(feature).Append('=').Append(used[feature]);
        byte[] mac = HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(mac);
    }

    /// <summary>`usage.json` 的形狀。欄位少是刻意的：多一個欄位就多一份隱私債。</summary>
    private sealed class UsageFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("used")] public Dictionary<string, int>? Used { get; set; }
        [JsonPropertyName("mac")] public string? Mac { get; set; }
    }
}
