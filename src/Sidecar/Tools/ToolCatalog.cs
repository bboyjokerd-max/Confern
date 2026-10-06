namespace Sidecar;

/// <summary>
/// <see cref="IToolCatalog"/> 的實作。**工具清單只寫在這裡**——加一個工具＝加一筆
/// <see cref="ToolDescriptor"/> ＋ 對應的執行實作，輪盤與命中測試不必改一行
/// （<see cref="WheelGeometry"/> 早就對扇區數泛化：<c>SweepDeg(count)</c>／<c>SectorAt(…, count)</c>）。
///
/// 目前有三個工具（2026-10-06），三個都是**已經能執行**才放上來的：`ROTATE`（順時針 90°）、
/// `CLEAN`（去 metadata）與 `COMPRESS`（壓縮，等級由設定頁決定）。
/// M3 的其餘四項（裁切、讀 QR、合併 PDF、去背）各自需要新互動或新相依，
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
    /// M3 的第三個工具：**壓縮**（`compress`）。輸出格式＝來源格式（壓縮不換格式），
    /// 只把品質換成「能讓檔案變小的那一版」。
    ///
    /// ⚠ **等級不在這裡、也不在輪盤上**（使用者 2026-10-06 明確裁定）：由設定頁的
    /// 「壓縮方式」單選決定（小檔／標準／高畫質／壓縮到），唯一權威是
    /// <see cref="CompressionPolicy"/>。輪盤只多**一顆**扇形，沒有循環切換、沒有多顆扇形。
    ///
    /// 為什麼圓心膠囊寫「依設定壓縮」而不是寫出當前等級：膠囊會**跟著 hover 走**，
    /// 而等級會在使用者改設定之後才變——把一個會過期的等級寫進 hover 提示，等於在畫面上
    /// 放第二個（而且會說謊的）設定來源。要問「現在是哪一級」請看設定頁；
    /// 引擎每次執行都會把實際用的 mode 與 quality 寫進日誌。
    ///
    /// 文字長度預算是硬性的（圓心鏤空直徑 94px）：17px 下 `依設定壓縮` 實測與既有的
    /// `去中繼資料` 同一個量級（5 個漢字 ≈ 97px ⇒ 膠囊 ≈ 129px），落在產品既有範圍內。
    /// 扇區標籤 `COMPRESS` 是 8 個大寫字母，比 `ROTATE` 長——3 扇區的字級是 12.7px、
    /// 文字框寬 80px，實測仍放得下（見 `tools/verify-m3c.ps1` 的 S17.40 量測）。
    /// </summary>
    private static readonly ToolDescriptor Compress = new(
        Id: "compress",
        Label: "COMPRESS",
        Description: "依設定壓縮",
        Icon: ToolIcon.Compress);

    /// <summary>
    /// 扇區順序＝這個陣列的順序（自正上方起、順時針）。新增工具**只在這裡加一筆**，
    /// 輪盤與命中測試不必改一行。
    /// </summary>
    public IReadOnlyList<ToolDescriptor> Tools { get; } = new[] { Rotate90, StripMetadata, Compress };

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
