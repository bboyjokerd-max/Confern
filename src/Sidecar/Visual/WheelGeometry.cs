namespace Sidecar;

/// <summary>
/// 輪盤幾何（純函式，規格 §4.3）。**不含任何繪製或視窗程式碼**，因此可以獨立對照驗證。
/// 座標系與畫面一致：x 向右、y 向下，角度以正上方為 0°、順時針增加。
///
/// 2026-10-01（視覺方向 D）：**留白語意改變**。
/// 舊版把「框」（輪盤＋外擴＋8px 陰影留白）釘在游標 +(18,18)，於是輪盤本體實際落在游標 **+32**，
/// 與規格 §4.3 的 +18 不符。D 的柔暈往外延伸約 39.4px（見 <see cref="WheelDesign"/>），
/// 若沿用舊語意，輪盤會被推離游標到 +62。因此改成：
/// **輪盤本體**釘在游標 +(18,18)，留白由 <see cref="MarginPx"/> 往外長。
/// 這樣點陣圖寬度（304）與視窗原點（游標 − 24）都與舊版相同，驗收腳本的假設不變；
/// 只有高度因為多了「鍵帽＋說明」這一帶而變高。
/// </summary>
internal static class WheelGeometry
{
    /// <summary>輪盤直徑（規格 §4.3：220 px，可依 DPI 縮放）。</summary>
    public const int DiameterPx = 220;

    /// <summary>游標偏移（規格 §4.3：游標 +(18,+18)）——指的是**輪盤本體**的左上角。</summary>
    public const int OffsetPx = 18;

    /// <summary>選中扇區外擴（px）。值來自 <see cref="WheelDesign"/>，避免兩處各寫一份。</summary>
    public const int SelectedExtraPx = WheelDesign.SelExpandPx;

    /// <summary>游標旁自繪鬼影卡的邊長（規格 §4.3）。必須 &lt; <see cref="CursorInBitmapPx"/>。</summary>
    public const int GhostPx = 32;

    /// <summary>
    /// 輪盤本體之外的留白：要放得下**快照玻璃的最大外緣**（柔暈最外圈 ≈ 39.4px）再加 1–2px 反鋸齒。
    /// ⚠ 不可憑感覺調小：調小＝柔暈被點陣圖邊緣切掉，而且只會「看起來有點怪」，不會報錯。
    /// </summary>
    public static int MarginPx =>
        (int)Math.Ceiling(WheelDesign.GlowStartPx + WheelDesign.GlowRings * WheelDesign.GlowStepPx) + 2;

    // ---- 輪盤下方的資訊帶，由上而下：鍵帽 → 說明文字 → 檔名預覽 ----

    /// <summary>輪盤下緣到鍵帽的間距。</summary>
    public const int KeyGapPx = 8;

    public const int KeycapPx = 27;
    public const int CaptionGapPx = 5;
    public const int CaptionPx = 15;
    public const int PreviewGapPx = 6;

    /// <summary>輸出檔名預覽列高度（滑鼠命中不受影響，純顯示）。</summary>
    public const int PreviewPx = 20;

    public const int BottomPadPx = 6;

    /// <summary>輪盤下緣以下的總高度。</summary>
    public static int BottomStripPx =>
        KeyGapPx + KeycapPx + CaptionGapPx + CaptionPx + PreviewGapPx + PreviewPx + BottomPadPx;

    /// <summary>點陣圖寬度（＝視覺層視窗寬度）。留白左右各一份。</summary>
    public static int BitmapWidthPx => MarginPx * 2 + DiameterPx;

    /// <summary>點陣圖高度：上緣留白 ＋ 輪盤 ＋ 下方資訊帶。</summary>
    public static int BitmapHeightPx => MarginPx + DiameterPx + BottomStripPx;

    /// <summary>圓心在點陣圖中的座標（輪盤水平置中，垂直只留上緣留白）。</summary>
    public static int CenterInBitmapPx => MarginPx + DiameterPx / 2;

    /// <summary>
    /// 游標在點陣圖中的座標。舊版的 <c>PadPx</c> 就是這個值：
    /// 驗收腳本以「視覺層視窗原點 ＝ 游標 − 24」斷言，所以它必須維持 24。
    /// </summary>
    public static int CursorInBitmapPx => MarginPx - OffsetPx;

    /// <summary>
    /// 扇區角度（**不含間隙**）。這是**命中測試**用的：間隙只有 2–8°，不該讓那幾度變成死區。
    /// 繪製要用 <see cref="WheelDesign.SweepDeg"/>（有扣間隙），兩者刻意分開。
    /// </summary>
    public static double SweepDeg(int count) => count > 0 ? 360.0 / count : 0.0;

    /// <summary>中心圓盤半徑對輪盤半徑的比例（繪製與命中都必須用同一個值）。</summary>
    public const float CenterRatio = WheelDesign.CenterRatio;

    /// <summary>中心圓盤半徑（px）。圓盤不是扇區，游標停在上面不改變選取。</summary>
    public static double CenterRadiusPx => DiameterPx / 2.0 * CenterRatio;

    /// <summary>正上方起算（規格：扇區自正上方開始、順時針排列）。</summary>
    public const double StartDeg = -90.0;

    /// <summary>相對游標的位移 → 扇區索引（環狀）。</summary>
    public static int SectorAt(double dx, double dy, int count)
    {
        if (count <= 0) return -1;
        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;   // -180..180，0 = 正右
        double a = (deg + 90.0 + 360.0) % 360.0;             // 0 = 正上，順時針增加
        int i = (int)(a / SweepDeg(count));
        return i % count;
    }

    /// <summary>扇區中間角（度，畫面座標）。</summary>
    public static double SectorMidDeg(int index, int count) => StartDeg + (index + 0.5) * SweepDeg(count);

    /// <summary>點是否落在扇區環內（依半徑判定，與角度無關）。</summary>
    public static bool Inside(double dx, double dy, int extra = 0)
    {
        double limit = DiameterPx / 2.0 + extra;
        return dx * dx + dy * dy <= limit * limit;
    }

    /// <summary>
    /// 輪盤**本體**左上角（螢幕座標）：游標 +(18,+18)，超出工作區就翻到游標的左／上側，
    /// 最後連同留白一起夾在工作區內（不然柔暈會被螢幕邊緣切掉）。
    /// </summary>
    public static (int X, int Y) FlipEdge(int cursorX, int cursorY, Native.RECT work)
    {
        int x = cursorX + OffsetPx;
        int y = cursorY + OffsetPx;
        if (x + DiameterPx + MarginPx > work.Right) x = cursorX - OffsetPx - DiameterPx;
        if (y + DiameterPx + MarginPx > work.Bottom) y = cursorY - OffsetPx - DiameterPx;

        int minX = work.Left + MarginPx;
        int minY = work.Top + MarginPx;
        int maxX = Math.Max(minX, work.Right - DiameterPx - MarginPx);
        int maxY = Math.Max(minY, work.Bottom - DiameterPx - MarginPx);
        return (Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }

    /// <summary>輪盤本體左上角 → 視覺層視窗左上角（往左上退掉留白）。</summary>
    public static (int X, int Y) WindowOrigin(int wheelX, int wheelY) =>
        (wheelX - MarginPx, wheelY - MarginPx);

    /// <summary>輪盤本體左上角 → 圓心（螢幕座標）。繪製與滑鼠命中都用這一條。</summary>
    public static (double X, double Y) CenterOfBox(int wheelX, int wheelY) =>
        (wheelX + DiameterPx / 2.0, wheelY + DiameterPx / 2.0);
}
