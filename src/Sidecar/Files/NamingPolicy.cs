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
    /// <paramref name="targetDirectory"/>（規格 §9.5，2026-10-05 使用者裁定）：
    /// **null／空白＝來源檔旁邊**——這是預設，也是唯一的回退路徑。指定的資料夾不存在時
    /// **一律回退到來源旁邊**（不建立、不丟錯、不寫到別的地方去）；呼叫端負責把「回退了」
    /// 這件事寫進日誌，因為靜默的回退正是這個專案最反對的東西。
    ///
    /// 「輸出到哪裡」的決定仍然只發生在這一個方法裡（路徑與檔名的唯一權威），
    /// 別的地方不得自己組路徑。
    /// </summary>
    public static string ResolveOutputPath(string sourcePath, string targetExtension,
                                           string? targetDirectory = null)
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
        if (!Exists(first)) return first;

        for (int n = 1; n <= MaxAttempts; n++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({n}).{targetExtension}");
            if (!Exists(candidate)) return candidate;
        }

        // 到這裡代表資料夾裡已經有 9999 個同名檔——不再硬幹，丟出明確錯誤（§7.2 禁止靜默失敗）
        throw new IOException($"no free output name after {MaxAttempts} attempts: {first}");
    }

    /// <summary>Windows 檔案系統語意：不區分大小寫，且要考慮長路徑。</summary>
    private static bool Exists(string path) => File.Exists(LongPath.Prepare(path));
}
