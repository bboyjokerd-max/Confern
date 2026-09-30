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
    public string? SourceFormat(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        string ext = Path.GetExtension(paths[0]);
        if (string.IsNullOrEmpty(ext) || ext.Length < 2) return null;
        return ext.TrimStart('.').ToUpperInvariant();
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
