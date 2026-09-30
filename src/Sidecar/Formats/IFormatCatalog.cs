namespace Sidecar;

/// <summary>
/// 格式知識的**唯一查詢介面**（規格 §9.1：`CodecRegistry` 是唯一格式知識來源，
/// **UI 不得自行判斷副檔名**）。
///
/// M1b 先把這組查詢介面凍結，讓輪盤可以先做；真正的實作在 M1c
/// （WIC／libwebp／HEIC 探測）。<see cref="WheelPresenter"/> 只拿到字串，不碰任何格式判斷。
/// </summary>
internal interface IFormatCatalog
{
    /// <summary>由來源檔案清單判定「原格式」；無法判定時回 <c>null</c>。</summary>
    string? SourceFormat(IReadOnlyList<string> paths);

    /// <summary>可輸出的目標格式（**已排除原格式**，規格 §4.3：原格式不出現）。</summary>
    IReadOnlyList<string> TargetsFor(string? sourceFormat);

    /// <summary>目標格式的輸出副檔名（小寫、不含點）。</summary>
    string ExtensionOf(string targetFormat);
}
