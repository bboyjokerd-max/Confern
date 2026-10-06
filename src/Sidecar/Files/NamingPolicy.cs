namespace Sidecar;

/// <summary>
/// 衝突命名（規格 §5）——**唯一命名規則權威**（UI 不得自己組檔名）。
///
/// 規則：與原檔同一資料夾、同主檔名＋新副檔名；`holiday.heic` → `holiday.png`；
/// 若 `holiday.png` 已存在 → `holiday (1).png` → `holiday (2).png`…
/// **永不覆寫、永不詢問。**
/// </summary>
internal static class NamingPolicy
{
    private const int MaxAttempts = 9999;

    /// <summary>
    /// 算出一個**還不存在**的輸出路徑。
    ///
    /// ⚠ <b>這是一個提示，不是保證。</b> 它靠 `File.Exists` 判斷「這個名字自由嗎」，
    /// 而那個判斷與之後的 `File.Move` 之間有一個**跨行程的窗口**：兩個行程可以同時看到
    /// 同一個名字是自由的，然後同時寫它。真正的不覆寫保證是
    /// <see cref="AtomicWriter"/> 的 `File.Move(overwrite: false)`——它會讓**輸的那一方**
    /// 拿到 `ERROR_ALREADY_EXISTS`，而不是讓使用者少一個檔案。所以呼叫端**必須**能處理
    /// 「剛剛選的名字被別人拿走了」（見 <see cref="ConversionEngine"/> 的重試迴圈）。
    ///
    /// <paramref name="exclude"/>：已經試過、而且已經失敗的名字。
    /// 沒有它就沒有重試可言——第二次選名會**再選回同一個名字**，然後永遠失敗。
    ///
    /// <paramref name="targetDirectory"/>（規格 §9.5，2026-10-05 使用者裁定）：
    /// **null／空白＝來源檔旁邊**——這是預設，也是唯一的回退路徑。指定的資料夾不存在時
    /// **一律回退到來源旁邊**（不建立、不丟錯、不寫到別的地方去）；呼叫端負責把「回退了」
    /// 這件事寫進日誌，因為靜默的回退正是這個專案最反對的東西。
    ///
    /// 「輸出到哪裡」的決定仍然只發生在這一個方法裡（路徑與檔名的唯一權威），
    /// 別的地方不得自己組路徑。
    /// </summary>
    public static string ResolveOutputPath(string sourcePath, string targetExtension,
                                           string? targetDirectory = null,
                                           ICollection<string>? exclude = null)
    {
        string sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        string dir = sourceDir;
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            string wanted = Path.GetFullPath(targetDirectory);
            if (Directory.Exists(LongPath.Prepare(wanted))) dir = wanted;
        }

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(stem)) stem = "output";

        string first = Path.Combine(dir, $"{stem}.{targetExtension}");
        if (IsFree(first, exclude)) return first;

        for (int n = 1; n <= MaxAttempts; n++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({n}).{targetExtension}");
            if (IsFree(candidate, exclude)) return candidate;
        }

        // 到這裡代表資料夾裡已經有 9999 個同名檔——不再硬幹，丟出明確錯誤（§7.2 禁止靜默失敗）
        throw new IOException($"no free output name after {MaxAttempts} attempts: {first}");
    }

    /// <summary>
    /// 磁碟上不存在**且**這一輪還沒試過。第二個條件不能省：被別人搶走的名字在磁碟上
    /// 是存在的（對方剛寫成功），但也有「對方又刪掉了」的窗口；只靠 <see cref="File.Exists"/>
    /// 會讓重試迴圈原地打轉。
    /// </summary>
    private static bool IsFree(string path, ICollection<string>? exclude) =>
        !Exists(path) && (exclude == null || !exclude.Contains(path));

    /// <summary>Windows 檔案系統語意：不區分大小寫，且要考慮長路徑。</summary>
    private static bool Exists(string path) => File.Exists(LongPath.Prepare(path));
}
