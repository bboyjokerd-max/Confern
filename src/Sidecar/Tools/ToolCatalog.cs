namespace Sidecar;

/// <summary>
/// <see cref="IToolCatalog"/> 的實作。**工具清單只寫在這裡**——加一個工具＝加一筆
/// <see cref="ToolDescriptor"/> ＋ 對應的執行實作，輪盤與命中測試不必改一行
/// （<see cref="WheelGeometry"/> 早就對扇區數泛化：<c>SweepDeg(count)</c>／<c>SectorAt(…, count)</c>）。
///
/// 目前只有一個工具，這是刻意的：**已經能執行的才放上來**。M3 的其餘六項
/// （裁切、壓縮、去 metadata、讀 QR、合併 PDF、去背）各自需要新互動或新相依，
/// 見 `docs/aegis/plans/2026-10-05-m3-tools-wheel.md` §1 的相依盤點。
/// </summary>
internal sealed class ToolCatalog : IToolCatalog
{
    /// <summary>
    /// 順時針 90 度。選 90° 而不是「任意角度」的理由有兩個：
    /// ①它是唯一可以**無損**做的角度（JPEG 的無損轉向、其他格式只是重新排列像素，
    /// 不需要重新取樣，因此不會因為插值而讓畫面變軟）；
    /// ②驗收可以寫成客觀斷言——非方形圖轉完之後**長寬必須對調**，不必靠肉眼看圖。
    /// </summary>
    private static readonly ToolDescriptor Rotate90 = new(
        Id: "rotate90",
        Label: "ROTATE",
        Description: "順時針轉 90 度",
        Icon: ToolIcon.Rotate90);

    public IReadOnlyList<ToolDescriptor> Tools { get; } = new[] { Rotate90 };

    public IReadOnlyList<ToolDescriptor> ToolsFor(PayloadVerdict verdict)
    {
        // 拒絕語意沿用第一層（規格 §4.3）：混合選取／不支援／空的，都不給工具，
        // 由呼叫端顯示既有的拒絕訊息。**不要在這裡偷偷放行**——那會讓第一層的拒絕
        // 在第二層變成「可以按」，而兩層的訊息會互相矛盾。
        if (verdict.Status != PayloadStatus.Ready) return Array.Empty<ToolDescriptor>();

        // 旋轉對所有「本產品支援的圖片」都成立，所以目前沒有更細的條件。
        // 未來的工具會有：裁切（要能取像素）、去 metadata（要有 metadata 容器）、
        // 讀 QR（只適用於解碼得動的點陣圖）。那些條件寫在這裡，不寫在 UI 裡。
        return Tools;
    }
}
