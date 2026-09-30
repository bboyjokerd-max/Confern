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

    /// <summary>單行摘要。全成功就只顯示成功數，有失敗才顯示 `✓ N / ✗ M`。</summary>
    public string Line => AllOk ? $"✓ {Ok}" : $"✓ {Ok} / ✗ {Failed}";

    public IEnumerable<ConversionResult> Failures => Results.Where(r => !r.Ok);
}
