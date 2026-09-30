namespace Sidecar;

/// <summary>一批被拖曳檔案的分類結果（規格 §4.3 混合選取拒絕、§4.4 同類別批次）。</summary>
internal enum PayloadStatus
{
    /// <summary>沒有檔案（或讀不到）。</summary>
    Empty,

    /// <summary>同類別、可以轉換。</summary>
    Ready,

    /// <summary>混合類別：規格 §4.3 要求**拒絕**，顯示訊息並允許 Esc。</summary>
    Mixed,

    /// <summary>本產品不支援的類別（M1b 只做圖片）。</summary>
    Unsupported
}

/// <summary>
/// <see cref="IFormatCatalog.Classify"/> 的結果。
/// <see cref="SourceFormat"/> 只在 <see cref="PayloadStatus.Ready"/> 時有意義。
/// </summary>
internal readonly record struct PayloadVerdict(PayloadStatus Status, string? SourceFormat)
{
    public static readonly PayloadVerdict Empty = new(PayloadStatus.Empty, null);
}
