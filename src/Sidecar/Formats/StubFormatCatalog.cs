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

    public string? SourceFormat(IReadOnlyList<string> paths) => null;

    public IReadOnlyList<string> TargetsFor(string? sourceFormat) => DefaultTargets;

    public string ExtensionOf(string targetFormat) => targetFormat.ToLowerInvariant();
}
