namespace Sidecar;

/// <summary>
/// <see cref="IToolCatalog"/> 的實作。**工具清單只寫在這裡**——加一個工具＝加一筆
/// <see cref="ToolDescriptor"/> ＋ 對應的執行實作，輪盤與命中測試不必改一行
/// （<see cref="WheelGeometry"/> 早就對扇區數泛化：<c>SweepDeg(count)</c>／<c>SectorAt(…, count)</c>）。
///
/// 目前有兩個工具（2026-10-06），兩個都是**已經能執行**才放上來的：`ROTATE`（順時針 90°）
/// 與 `CLEAN`（去 metadata）。M3 的其餘五項（裁切、壓縮、讀 QR、合併 PDF、去背）各自需要
/// 新互動或新相依，見 `docs/aegis/plans/2026-10-05-m3-tools-wheel.md` §1 的相依盤點。
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

    /// <summary>
    /// M3 的第二個工具：**去 metadata**（`strip-metadata`）。選它當第二刀的理由與旋轉同一條
    /// ——**不需要新相依**（只用 WIC 與既有的 `AtomicWriter`），而且驗收可以寫成客觀斷言：
    /// **像素必須完全相同、metadata 必須消失**，兩件都能當場量（規格 §9.4）。
    ///
    /// ⚠ **不要寫成「重新編碼」**：這一個工具**預設不解碼**。JPEG 走容器層移除
    /// （`Formats/JpegMetadataStrip`：丟掉 APP1／APP2／…／COM，熵編碼的掃描資料一個位元組都不動），
    /// 所以像素是**位元組級的相同**；其他格式才走「解碼 → 丟 metadata → 重編碼」，
    /// 而 PNG／BMP／TIF／GIF 是無損容器，結果同樣逐像素相同。**WebP 是有損的**，
    /// 對它而言「重新編碼」是真的會動到像素——與旋轉工具同一條既有裁定。詳見 `Convert/ConversionEngine.StripMetadata`。
    ///
    /// 中心膠囊的文字長度有預算（圓心鏤空直徑 94px、膠囊最小寬 100px）：
    /// 實測 17px Segoe UI Bold 下 `轉 90°` = 55.6px、`去中繼資料` = 97.3px（膠囊 129px）。
    /// 第一層在正常情況下畫的就是 138–145px 的膠囊（`JPG → WEBP` = 110.3px），
    /// 所以 129px 落在產品**既有**的範圍內，不是新開的先例。
    /// </summary>
    private static readonly ToolDescriptor StripMetadata = new(
        Id: "strip-metadata",
        Label: "CLEAN",
        Description: "去中繼資料",
        Icon: ToolIcon.StripMetadata);

    /// <summary>
    /// 扇區順序＝這個陣列的順序（自正上方起、順時針）。新增工具**只在這裡加一筆**，
    /// 輪盤與命中測試不必改一行。
    /// </summary>
    public IReadOnlyList<ToolDescriptor> Tools { get; } = new[] { Rotate90, StripMetadata };

    public IReadOnlyList<ToolDescriptor> ToolsFor(PayloadVerdict verdict)
    {
        // 拒絕語意沿用第一層（規格 §4.3）：混合選取／不支援／空的，都不給工具，
        // 由呼叫端顯示既有的拒絕訊息。**不要在這裡偷偷放行**——那會讓第一層的拒絕
        // 在第二層變成「可以按」，而兩層的訊息會互相矛盾。
        if (verdict.Status != PayloadStatus.Ready) return Array.Empty<ToolDescriptor>();

        // 兩個工具對所有「本產品支援的圖片」都成立，所以目前沒有更細的條件。
        // 這不是「以後也一定如此」的宣告：`strip-metadata` 的適用性其實取決於「這個容器有沒有
        // metadata 容器」（一個沒有 EXIF／ICC／註解的 JPEG 也會成功，只是沒東西可丟——
        // 仍然產出一個檔案，這比「按下去沒反應」誠實）。真正的細條件會寫在**這裡**，不寫在 UI 裡。
        // 未來的工具會有：裁切（要能取像素）、讀 QR（只適用於解碼得動的點陣圖）。
        return Tools;
    }
}
