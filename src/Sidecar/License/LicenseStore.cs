using System.Text;

namespace Sidecar;

/// <summary>
/// 這台機器上啟用中的授權（`%LOCALAPPDATA%\Confern\license.txt`）。
///
/// <para><b>為什麼是一個純文字檔而不是加密的容器</b>：買家要能把它抄到另一台電腦、貼進客服信、
/// 放進自己的密碼管理器。加密只會讓「換電腦」變成一件要問我們的事，而那是我們最不想回的信。
/// 檔案裡就是那一行金鑰字串——它本身已經被簽章程護，寫在這裡的內容不被信任。</para>
///
/// <para><b>啟用＝驗簽通過才寫入</b>：寫進去的一定是驗過的（見 <see cref="TryActivate"/>），
/// 所以「檔案在」與「授權有效」在正常情況下是同一件事；但讀的時候仍然**每一次都重驗**——
/// 檔案是可以被手動改的，而那個檢查只花一次 P-256 驗章。</para>
///
/// <para><b>環境變數 <c>CONFERN_LICENSE</c></b> 會蓋掉路徑（驗收套件不能動到使用者真正的授權）。</para>
/// </summary>
internal sealed class LicenseStore
{
    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private string _key = "";
    private DateTime _loadedWriteUtc;
    private long _loadedLength;
    private bool _loaded;

    private static LicenseStore? _shared;

    public LicenseStore(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
    }

    /// <summary>`%LOCALAPPDATA%\Confern\license.txt`，或 <c>CONFERN_LICENSE</c> 指定的路徑。</summary>
    public static string DefaultPath()
    {
        string? overridden = Environment.GetEnvironmentVariable("CONFERN_LICENSE");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        return Path.Combine(ProductInfo.DataDirectory, "license.txt");
    }

    public static LicenseStore Shared(Action<string>? log = null)
    {
        if (_shared != null) return _shared;
        lock (typeof(LicenseStore))
        {
            _shared ??= new LicenseStore(DefaultPath(), log);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONFERN_LICENSE")))
                log?.Invoke($"license: using '{_shared.FilePath}' (CONFERN_LICENSE overrides the user file)");
            return _shared;
        }
    }

    /// <summary>測試用：換掉共用的那一個。</summary>
    public static void ReplaceSharedForTests(LicenseStore? store)
    {
        lock (typeof(LicenseStore)) { _shared = store; }
    }

    public string FilePath => _path;

    /// <summary>檔案裡現在那一行（沒有驗簽、沒有正規化）。命令列用它顯示「檔案裡有什麼」。</summary>
    public string RawKey
    {
        get { lock (_gate) { ReloadIfChangedOnDisk(); return _key; } }
    }

    /// <summary>
    /// 現在有效的授權；沒有、或檔案裡的東西驗不過，就是 <c>null</c>。
    /// 每次呼叫都重驗（便宜，而且檔案可能被改）。
    /// </summary>
    public LicenseInfo? Current(out string why)
    {
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            if (_key.Length == 0)
            {
                why = "no license is activated";
                return null;
            }
            if (License.TryVerify(_key, out LicenseInfo? info, out string bad))
            {
                why = "";
                return info;
            }
            why = $"the stored key is not valid: {bad}";
            return null;
        }
    }

    /// <summary>驗一份金鑰之後寫入。<paramref name="why"/> 說得出為什麼沒寫。</summary>
    public bool TryActivate(string? key, out LicenseInfo? info, out string why)
    {
        if (!License.TryVerify(key, out info, out why)) return false;
        string text = (key ?? "").Trim();
        lock (_gate)
        {
            if (!Save(text))
            {
                why = $"the key is valid, but it could not be written to '{_path}'";
                info = null;
                return false;
            }
            _key = text;
            _log?.Invoke($"license: activated {info!.Describe()} -> '{_path}'");
            why = "";
            return true;
        }
    }

    /// <summary>停用（刪掉檔案）。回傳「本來有沒有東西可刪」。</summary>
    public bool Clear(out bool deletedFile)
    {
        lock (_gate)
        {
            ReloadIfChangedOnDisk();
            bool had = _key.Length > 0;
            deletedFile = false;
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                    deletedFile = true;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"WARNING license: could not delete '{_path}' " +
                             $"({ex.GetType().Name}: {ex.Message})");
                return false;
            }
            _key = "";
            RememberFileState();
            _log?.Invoke($"license: cleared (had a key: {had}, deleted the file: {deletedFile})");
            return true;
        }
    }

    // ---------------------------------------------------------------- 檔案

    private bool Save(string text)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicWriter.Write(_path, stream =>
            {
                // 一行 + 換行：這樣 `type license.txt` 不會黏著提示字元，也不會多一個空行被當成金鑰。
                using var writer = new StreamWriter(stream, new UTF8Encoding(false),
                                                    bufferSize: 1024, leaveOpen: true);
                writer.Write(text);
                writer.Write('\n');
            }, overwrite: true);
            RememberFileState();
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"WARNING license: could not save '{_path}' ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    private void RememberFileState()
    {
        _loaded = true;
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

    private void ReloadIfChangedOnDisk()
    {
        try
        {
            var info = new FileInfo(_path);
            bool exists = info.Exists;
            DateTime writeUtc = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            long length = exists ? info.Length : -1;
            if (_loaded && writeUtc == _loadedWriteUtc && length == _loadedLength) return;
            _key = "";
            if (exists)
            {
                string text = File.ReadAllText(_path).Trim();
                // 允許檔案裡有空白與換行：使用者可能從 email 貼進來、存成兩行。
                _key = text;
            }
            RememberFileState();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"license: could not read '{_path}' ({ex.GetType().Name}: {ex.Message})");
            _key = "";
            RememberFileState();
        }
    }
}

/// <summary>
/// 「這個功能現在能不能用」——**唯一**回答這件事的地方。
///
/// <para>順序是刻意的：<b>先問授權，再問額度</b>。有授權的人不該看到任何次數限制，
/// 而額度是「還沒付錢的人的試用」，它不該是第一個問題。把兩者寫在兩個地方的話，
/// 總有一天會出現「有授權但被額度擋住」的組合，而那種 bug 只會發生在付了錢的人身上。</para>
///
/// <para>回傳的 <c>remaining</c>：有授權時是 <c>-1</c>（無限），否則是真的剩餘次數。
/// 用負數而不是 <c>int.MaxValue</c>，是為了讓日誌與命令列可以把它印成 <c>unlimited</c>
/// 而不是一個看起來像真的上限的大數字。</para>
/// </summary>
internal static class Entitlement
{
    /// <summary>去背（CUTOUT）現在能不能用。</summary>
    public static bool CutoutAllowed(Action<string>? log, out int remaining, out string why)
    {
#if CONFERN_FREE_EDITION
        // 免費版沒有這顆工具，所以答案是 false——但理由與額度無關，而且**不去讀任何檔案**
        // （免費版不該在使用者的 AppData 裡留下一個額度檔）。
        remaining = 0;
        why = $"the {ProductInfo.Edition} edition does not include the cutout tool";
        log?.Invoke($"entitlement: {TrialPolicy.Cutout} not available ({why})");
        return false;
#else
        LicenseInfo? license = LicenseStore.Shared(log).Current(out string licenseWhy);
        if (license != null)
        {
            remaining = -1;
            why = "";
            log?.Invoke($"entitlement: {TrialPolicy.Cutout} allowed by the license " +
                        $"({license.Describe()})");
            return true;
        }

        var usage = UsageStore.Shared(log);
        bool allowed = usage.CanUse(TrialPolicy.Cutout, out remaining, out string usageWhy);
        why = usageWhy.Length > 0 ? usageWhy : licenseWhy;
        return allowed;
#endif
    }

    /// <summary>
    /// 成功之後記一次。<b>回傳「有沒有真的記」</b>：有授權時不記（那個人不必被計數），
    /// 而呼叫端需要知道這件事才能印對的那一行——「花了 1 次」與「有授權所以不計數」
    /// 是兩個不同的結果，印成同一句會讓日誌說謊。
    /// </summary>
    public static bool RecordCutoutUse(Action<string>? log)
    {
#if CONFERN_FREE_EDITION
        _ = log;
        return false;
#else
        if (LicenseStore.Shared(log).Current(out _) != null) return false;
        UsageStore.Shared(log).RecordUse(TrialPolicy.Cutout);
        return true;
#endif
    }

    /// <summary>現在還剩幾次（有授權時 -1＝無限）。給「跑完之後」印一行用。</summary>
    public static int CutoutRemaining()
    {
#if CONFERN_FREE_EDITION
        return 0;
#else
        if (LicenseStore.Shared(null).Current(out _) != null) return -1;
        return UsageStore.Shared(null).Remaining(TrialPolicy.Cutout);
#endif
    }

    /// <summary>人類看得懂的一行（`remaining=-1` 印成 unlimited）。</summary>
    public static string RemainingText(int remaining) => remaining < 0 ? "unlimited" : remaining.ToString();
}
