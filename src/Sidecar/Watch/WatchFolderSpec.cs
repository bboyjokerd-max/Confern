namespace Sidecar;

/// <summary>
/// 一個「監看資料夾」的設定（2026-10-22，跟 Filee 比較後抄的第二樣）。
///
/// <para>存進 `settings.json` 的 <see cref="Settings.WatchFolders"/>，同時也是命令列
/// （`confern watch`）用的同一個型別——**一份資料模型，兩條入口**，不然「設定檔裡寫的」與
/// 「命令列跑起來的」會各自演化，最後只有其中一條是對的。</para>
///
/// <para>欄位刻意都是可寫的 <c>get; set;</c>（不像 <see cref="Settings"/> 那樣的 init-only）：
/// 這個型別要能被 <c>--watch-add</c>／<c>--watch-remove</c> 就地改。設定檔是給人直接編輯的
/// 文字檔，不是不可變資料結構。</para>
/// </summary>
internal sealed class WatchFolderSpec
{
    /// <summary>被監看的資料夾（絕對路徑；相對路徑在驗證時會被拒絕，因為常駐程式的 cwd 不是使用者的 cwd）。</summary>
    public string Path { get; set; } = "";

    /// <summary>新檔案要轉成什麼（<see cref="CodecRegistry.OfferedOutputs"/> 之一，大寫）。</summary>
    public string Target { get; set; } = "JPG";

    /// <summary>
    /// 品質；<c>0</c>＝**照設定檔的 <see cref="Settings.JpgQuality"/> 走**。
    ///
    /// <para>為什麼 0 不當成「品質 0」：這個欄位存在的理由是「這個資料夾要跟其他資料夾不同的
    /// 品質」，而「我沒指定」和「我要 0」（會被夾成 60）是兩件不同的事。用 0 表示「沒指定」
    /// 與 <see cref="Settings.CompressionTargetKb"/> 用 0 表示「沒上限」是同一條專案慣例。</para>
    /// </summary>
    public int Quality { get; set; }

    /// <summary>輸出資料夾；空字串＝寫進被監看的那個資料夾（＝來源檔旁邊）。</summary>
    public string OutFolder { get; set; } = "";

    /// <summary>
    /// 去重用的識別碼：同一條路徑只有一筆設定（大小寫與結尾斜線都不算不同）。
    /// 沒有它，`--watch-add` 按三次就會有三條一樣的規則同時盯著同一個資料夾。
    /// </summary>
    // [JsonIgnore]：這是算出來的，不是存下來的。少了它，設定檔裡會多一個 "Key" 欄位
    // （實測 2026-10-22 第一次 --add 就寫出來了）——一個使用者可以編輯、但編輯了也沒有用的欄位，
    // 正是「檔案裡有兩個地方講同一件事」的開端。
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => Normalize(Path);

    internal static string Normalize(string? path) =>
        (path ?? "").Trim().TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>一行說完這條規則（`--watch-list` 與日誌共用）。</summary>
    public string Describe() =>
        $"{Path} -> {Target}" +
        (Quality > 0 ? $" q={Quality}" : " q=(settings)") +
        (OutFolder.Length > 0 ? $" out={OutFolder}" : " out=(same folder)");
}

/// <summary>
/// 監看資料夾的**規則**（什麼組合可以接受、多久算「檔案寫完了」）。
/// 全部集中在這裡，因為三條入口（命令列前台跑、`--watch-add` 寫設定、常駐啟動時讀設定）
/// 都必須用同一套判準——其中兩條會寫進設定檔，一條不會，判準分岔的話就會出現
/// 「命令列拒絕的組合，卻能從設定檔啟動」。
/// </summary>
internal static class WatchRules
{
    /// <summary>
    /// 檔案要「安靜」多久才算寫完（毫秒）。預設 400 ms：
    /// 一般複製作業在磁碟上就是幾十毫秒到一兩百毫秒，而人類「拖進去」的節奏遠大於 400 ms。
    ///
    /// <para>⚠ 這是一個**取捨**，不是物理常數：太大＝慢半拍，太小＝可能轉到半個檔。
    /// 所以它可調（`--watch-settle-ms`），而且下限有守衛（<see cref="MinSettleMs"/>）——
    /// 設成 10 ms 等於關掉這個機制，那比不設還糟，因為使用者會以為它有效。</para>
    /// </summary>
    public const int DefaultSettleMs = 400;

    public const int MinSettleMs = 50;
    public const int MaxSettleMs = 10_000;

    /// <summary>掃描間隔（毫秒）。延遲上限大約＝輪詢間隔 ＋ settle，實測見 verify-watch。</summary>
    public const int PollMs = 250;

    /// <summary>
    /// 這條規則**能不能用**。<c>null</c>＝可以；否則回傳給人看的原因。
    ///
    /// 兩類拒絕：①打錯字／不存在（會轉出 0 個檔，使用者以為壞了）；
    /// ②**會自我餵食的組合**——輸出資料夾就是被監看的資料夾（或在其下），
    /// 那麼每一張轉出來的圖都會被當成新檔案再轉一次，無限長大。這一條不是潔癖：
    /// 預設的 OutFolder 是空字串＝寫回同一個資料夾，所以它一定會被踩到，除非明確擋掉。
    /// </summary>
    public static string? Reject(WatchFolderSpec spec, IReadOnlyList<string> offered)
    {
        string folder = (spec.Path ?? "").Trim();
        if (folder.Length == 0) return "no folder was given";
        if (!System.IO.Path.IsPathRooted(folder))
            return $"'{folder}' is a relative path; the resident app has a different working directory, " +
                   "so a watched folder must be absolute";
        if (!System.IO.Directory.Exists(folder)) return $"folder does not exist: {folder}";

        string target = (spec.Target ?? "").Trim().ToUpperInvariant();
        if (!offered.Contains(target, StringComparer.Ordinal))
            return $"target '{spec.Target}' is not a format this build writes; " +
                   $"offered=[{string.Join(", ", offered)}]";

        string outFolder = (spec.OutFolder ?? "").Trim();
        if (outFolder.Length > 0)
        {
            // 空字串＝寫回被監看的資料夾，那是**合法且是預設**（＝來源檔旁邊，與產品的既有語意一致）。
            // 但檔案進去之後「新檔」會出現在同一個資料夾，所以引擎必須靠自己記住「這是我寫的」來
            // 避免回頭再轉一次（見 FolderWatcher 的 _ours）。明確寫出同一個路徑則直接拒絕：
            // 那個人想要的是「輸出到別處」，而他寫的那個路徑只是打錯。
            string full = SafeFull(outFolder);
            string watch = SafeFull(folder);
            if (folder.Length > 0 && string.Equals(full, watch, StringComparison.OrdinalIgnoreCase))
                return $"output folder is the watched folder itself ({outFolder}); " +
                       "leave it empty if you want the results next to their sources";
            if (watch.Length > 0 && full.StartsWith(watch.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return $"output folder '{outFolder}' is inside the watched folder; every result would be watched again";
        }

        return null;
    }

    /// <summary>
    /// 把使用者給的東西整理成**可以存進設定檔**的形狀：絕對路徑、大寫格式、品質夾回。
    /// 空字串的輸出路徑保持空字串（那是有意義的值，不是「還沒填」）。
    /// </summary>
    public static WatchFolderSpec Canonical(WatchFolderSpec spec)
    {
        return new WatchFolderSpec
        {
            Path = SafeFull((spec.Path ?? "").Trim()),
            Target = (spec.Target ?? "").Trim().ToUpperInvariant(),
            Quality = spec.Quality <= 0
                ? 0
                : Math.Clamp(spec.Quality, CodecRegistry.MinEncodeQuality, CodecRegistry.MaxEncodeQuality),
            // 先收成一個區域變數：同一個運算式裡呼叫兩次 `spec.OutFolder` 讓編譯器無法證明
            // 它在中間沒有變成 null（CS8602，2026-10-22）。
            OutFolder = CanonicalOutput(spec.OutFolder),
        };
    }

    /// <summary>輸出路徑的正規化：空字串**保持空字串**（＝寫回被監看的資料夾，是有意義的值）。</summary>
    private static string CanonicalOutput(string? outFolder)
    {
        string trimmed = (outFolder ?? "").Trim();
        return trimmed.Length == 0 ? "" : SafeFull(trimmed);
    }

    private static string SafeFull(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path.Trim(); }
    }
}
