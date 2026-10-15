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
///
/// ⚠ **2026-10-14：`Ready` 有兩種，這裡把它顯性化。** 在此之前那個區別只存在於
/// <see cref="FormatCatalog.Classify"/> 的兩段註解裡（判定①「可以轉成圖片」與判定②
/// 「不能轉但這個格式有工具適用」，例如 PDF），而工具目錄只看得到 `Status`，
/// 於是沒有辦法問「這一批是不是圖片」。
///   • <see cref="CanConvert"/>＝判定①（類別屬於可轉換類別）⇒ 第一層格式扇形會出現。
///   • <see cref="CanConvert"/> 為 false 而 `Status` 為 `Ready`＝判定②⇒ 第一層沒有扇形，
///     但工具層打開（`merge-pdf` 就是靠這一條對 PDF 生效的）。
/// <see cref="FileCount"/> 是這一**批**的檔案數：`merge-pdf` 的圖片分支靠它判斷「≥2 張」。
/// 兩者都由 `Classify` 填——**判定知識仍然只有那一個地方**，這裡只是把結果帶出來。
/// </summary>
internal readonly record struct PayloadVerdict(
    PayloadStatus Status, string? SourceFormat, int FileCount, bool CanConvert)
{
    public static readonly PayloadVerdict Empty = new(PayloadStatus.Empty, null, 0, false);
}
