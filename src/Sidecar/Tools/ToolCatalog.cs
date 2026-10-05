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
    /// ①**不需要重新取樣**——任意角度要插值，會讓畫面變軟；90° 只是把像素搬到新位置；
    /// ②驗收可以寫成客觀斷言——非方形圖轉完**長寬必須對調**，而且四角顏色必須照順時針
    ///    的方向輪替（2026-10-05 已用 60×40、四角不同色的圖逐像素驗過：左上→右上、
    ///    右上→右下、左下→左上）。
    ///
    /// ⚠ **不要寫成「無損」**：本產品的寫出是「解碼 → 轉 → **重新編碼**」。
    /// PNG／BMP／TIF 是無損容器，所以結果逐像素等於「原圖轉 90°」（已驗）；
    /// **JPEG 會重新壓縮**（品質固定 100，最接近無損，但不是無損）。
    /// 真正的無損 JPEG 轉向是另一條 WIC 路徑，見計畫 §1 的相依盤點。
    /// </summary>
    private static readonly ToolDescriptor Rotate90 = new(
        Id: "rotate90",
        Label: "ROTATE",
        // ⚠ 這行字**長度有上限**，不是隨便寫：圓心膠囊畫在中央鏤空裡，而那個洞的直徑是
        // 94px（WheelDesign.CenterRatio = 47/110）。膠囊寬度 = 文字 + 左右內距，
        // 所以文字必須 ≲ 68px 才不會壓到扇區上。實測：
        //   "順時針轉 90 度" ≈ 119px → 膠囊約 145px，壓出去 25px／邊（第一版就是這樣，dump 圖上看得出來）
        //   "轉 90°"        ≈ 47px  → 膠囊約 73px，**完全落在鏤空內**
        // 語意沒有損失：圖示本身就是順時針的 90° 弧箭頭（見 WheelDesign 的 ToolIconPx 註解）。
        Description: "轉 90°",
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
