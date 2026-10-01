namespace Sidecar;

/// <summary>轉檔失敗的分類。每一類對應規格 §7.1 的一個使用者訊息——**訊息由這一層決定，UI 不得自己編字串**。</summary>
internal enum ConversionError
{
    None,
    Unsupported,
    DecodeFailed,
    TargetUnavailable,
    SourceMissing,
    Unreadable,
    WriteDenied,
    NoSpace,
    Locked,
    Timeout,
    Unknown
}

/// <summary>單一檔案的轉檔結果。</summary>
internal sealed record ConversionResult(string SourcePath, string? OutputPath, ConversionError Error, string Detail)
{
    public bool Ok => Error == ConversionError.None;

    /// <summary>規格 §7.1 的使用者訊息（繁體中文）。</summary>
    public string UserMessage => Error switch
    {
        ConversionError.None => $"✓ {Path.GetFileName(OutputPath ?? SourcePath)}",
        ConversionError.Unsupported => "不支援的格式",
        ConversionError.DecodeFailed => $"{Path.GetFileName(SourcePath)} 無法解碼",
        ConversionError.TargetUnavailable => "此格式目前無法輸出",
        ConversionError.SourceMissing => "來源檔案已不存在",
        ConversionError.Unreadable => "無法讀取來源檔案",
        ConversionError.WriteDenied => "無法寫入此資料夾",
        ConversionError.NoSpace => "空間不足",
        ConversionError.Locked => "檔案被鎖定",
        ConversionError.Timeout => "雲端檔案下載逾時",
        _ => "轉檔失敗"
    };
}

/// <summary>整批的摘要（規格 §7.3：`✓ 8 / ✗ 2`，清單不自動彈出）。</summary>
internal sealed record ConversionSummary(IReadOnlyList<ConversionResult> Results)
{
    public int Ok => Results.Count(r => r.Ok);
    public int Failed => Results.Count - Ok;
    public bool AllOk => Failed == 0;

    /// <summary>
    /// 完成後那一行的**唯一規則**（畫面＝輪盤收起處）。兩個呼叫端共用它，
    /// 不允許任何地方再自己拼一次：
    ///   全成功   → `✓ N`
    ///   全失敗   → 規格 §7.1 的訊息（例：`broken.png 無法解碼`）——原因一定要到畫面上，
    ///              只給 `✓ 0 / ✗ 1` 等於使用者看不到原因，踩到「不得靜默失敗」的紅線
    ///   部分失敗 → 規格 §7.3 的 `✓ N / ✗ M`（點擊展開清單尚未實作）
    /// </summary>
    public static string ComposeLine(int ok, int failed, IReadOnlyList<string> failures)
    {
        if (failed == 0) return $"✓ {ok}";
        if (ok == 0 && failures.Count > 0)
            return failures.Count == 1 ? failures[0] : $"{failures[0]}（共 {failed} 個）";
        return $"✓ {ok} / ✗ {failed}";
    }

    public string Line => ComposeLine(Ok, Failed, Failures.Select(r => r.UserMessage).ToList());

    public IEnumerable<ConversionResult> Failures => Results.Where(r => !r.Ok);
}
