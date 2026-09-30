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

    /// <summary>算出一個**還不存在**的輸出路徑。</summary>
    public static string ResolveOutputPath(string sourcePath, string targetExtension)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
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
