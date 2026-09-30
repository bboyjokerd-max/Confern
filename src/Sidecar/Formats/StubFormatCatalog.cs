namespace Sidecar;

/// <summary>
/// M1b 用的**假**目錄：只提供固定清單，讓輪盤能被建起來、被驗收。
///
/// ⚠️ 它**不是**格式知識來源。M1c 的 `CodecRegistry` 落地後，本檔必須刪除
/// （退休條件寫在 `docs/aegis/plans/2026-09-30-m1b-wheel.md`）。
///
/// <see cref="SourceFormat"/> 一律回 <c>null</c>：拖曳**進行中**的酬載（檔名與原格式）
/// 需要 OLE `IDropTarget` 的 `DragEnter`，那是 M1b 之後的 T7；在那之前輪盤只能顯示佔位符。
/// </summary>
internal sealed class StubFormatCatalog : IFormatCatalog
{
    private static readonly string[] DefaultTargets = { "PNG", "JPG", "WEBP", "BMP", "TIF" };

    /// <summary>
    /// 以副檔名當原格式——**這正是 M1c 要用真正的格式探測取代的事**（M1c 之後不得再用副檔名猜）。
    /// 這裡之所以還是做，是為了讓 T7 的酬載接線能被端到端驗證（日誌要出現真實的 `source=JPG`）。
    /// </summary>
    public string? SourceFormat(IReadOnlyList<string> paths) => Classify(paths).SourceFormat;

    /// <summary>M1b 的類別表：只有圖片。M1c 換成真正的格式探測。</summary>
    private static readonly Dictionary<string, string> CategoryByFormat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPG"] = "IMAGE", ["JPEG"] = "IMAGE", ["PNG"] = "IMAGE", ["WEBP"] = "IMAGE",
        ["BMP"] = "IMAGE", ["TIF"] = "IMAGE", ["TIFF"] = "IMAGE", ["HEIC"] = "IMAGE", ["HEIF"] = "IMAGE"
    };

    /// <summary>M1b 唯一「支援的類別」是圖片。其他副檔名各自成為一個類別——這樣混合判定才正確。</summary>
    private const string ImageCategory = "IMAGE";

    private static string CategoryOf(string format) =>
        CategoryByFormat.TryGetValue(format, out string? cat) ? cat : "EXT:" + format;

    /// <summary>
    /// 規格 §4.3：**同類別**才給一組選項；不同類別＝混合選取（拒絕）。
    /// 因此 [jpg, pdf] 是 <see cref="PayloadStatus.Mixed"/>（「請選同類別」），
    /// 而 [pdf] 單獨一個才是 <see cref="PayloadStatus.Unsupported"/>（「不支援的格式」）——
    /// 這是兩種不同的使用者訊息，不可以混為一談。
    /// </summary>
    public PayloadVerdict Classify(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return PayloadVerdict.Empty;

        string? category = null;
        string? firstFormat = null;
        foreach (string path in paths)
        {
            string? format = FormatOf(path);
            if (format == null) return new PayloadVerdict(PayloadStatus.Unsupported, null);

            string cat = CategoryOf(format);
            if (category == null) { category = cat; firstFormat = format; }
            else if (!string.Equals(category, cat, StringComparison.Ordinal))
                return new PayloadVerdict(PayloadStatus.Mixed, null);
        }

        return string.Equals(category, ImageCategory, StringComparison.Ordinal)
            ? new PayloadVerdict(PayloadStatus.Ready, firstFormat)
            : new PayloadVerdict(PayloadStatus.Unsupported, null);
    }

    private static string? FormatOf(string path)
    {
        string ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) || ext.Length < 2 ? null : ext.TrimStart('.').ToUpperInvariant();
    }

    /// <summary>
    /// 規格 §4.3：扇區＝所有可輸出格式**排除來源**。這條不變式屬本介面的契約，
    /// 因此實作（含本假目錄）都必須自己遵守——M1c 不得把責任推回 UI。
    /// </summary>
    public IReadOnlyList<string> TargetsFor(string? sourceFormat)
    {
        if (string.IsNullOrEmpty(sourceFormat)) return DefaultTargets;
        var list = new List<string>(DefaultTargets.Length);
        foreach (string f in DefaultTargets)
            if (!string.Equals(f, sourceFormat, StringComparison.OrdinalIgnoreCase)) list.Add(f);
        return list;
    }

    public string ExtensionOf(string targetFormat) => targetFormat.ToLowerInvariant();
}
