namespace Sidecar;

/// <summary>
/// 輪盤幾何（純函式，規格 §4.3）。**不含任何繪製或視窗程式碼**，因此可以獨立對照驗證。
/// 座標系與畫面一致：x 向右、y 向下，角度以正上方為 0°、順時針增加。
/// </summary>
internal static class WheelGeometry
{
    /// <summary>輪盤直徑（規格 §4.3：220 px，可依 DPI 縮放）。</summary>
    public const int DiameterPx = 220;

    /// <summary>游標偏移（規格 §4.3：游標 +(18,+18)）。</summary>
    public const int OffsetPx = 18;

    /// <summary>選中扇區外擴（規格 §4.3：6 px）。</summary>
    public const int SelectedExtraPx = 6;

    /// <summary>扇區外的陰影／反鋸齒留白。</summary>
    public const int GlowPx = 8;

    /// <summary>
    /// 游標四周保留的空間：讓自繪鬼影（32 px，以游標為中心）一定落在點陣圖內。
    /// 必須大於 GhostPx/2。
    /// </summary>
    public const int PadPx = 24;

    public const int GhostPx = 32;

    /// <summary>右／下緣留白，避免外擴的選中扇區被裁掉。</summary>
    public const int EdgeMarginPx = 14;

    /// <summary>輪盤本體框（含外擴與陰影）在點陣圖中的邊長。</summary>
    public static int BoxPx => DiameterPx + 2 * (SelectedExtraPx + GlowPx);

    /// <summary>輪盤框左上角在點陣圖中的座標。</summary>
    public static int BoxOriginInBitmapPx => PadPx + OffsetPx;

    /// <summary>預覽文字列高度（輸出檔名／`N 個檔案`）。</summary>
    public const int PreviewPx = 26;

    public static int BitmapWidthPx => BoxOriginInBitmapPx + BoxPx + EdgeMarginPx;
    public static int BitmapHeightPx => BitmapWidthPx + PreviewPx;

    /// <summary>每個扇區的角度（度）。</summary>
    public static double SweepDeg(int count) => count > 0 ? 360.0 / count : 0.0;

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
    /// 翻邊（規格 §4.3）：游標 +(18,+18)，超出工作區就翻到游標的左／上側，最後夾在工作區內。
    /// </summary>
    public static (int X, int Y) FlipEdge(int cursorX, int cursorY, int boxPx, Native.RECT work)
    {
        int x = cursorX + OffsetPx;
        int y = cursorY + OffsetPx;
        if (x + boxPx > work.Right) x = cursorX - OffsetPx - boxPx;
        if (y + boxPx > work.Bottom) y = cursorY - OffsetPx - boxPx;

        int maxX = Math.Max(work.Left, work.Right - boxPx);
        int maxY = Math.Max(work.Top, work.Bottom - boxPx);
        x = Math.Clamp(x, work.Left, maxX);
        y = Math.Clamp(y, work.Top, maxY);
        return (x, y);
    }

    /// <summary>輪盤框左上角 → 視覺層視窗左上角（往左上退掉游標保留區與偏移）。</summary>
    public static (int X, int Y) WindowOrigin(int boxX, int boxY) =>
        (boxX - BoxOriginInBitmapPx, boxY - BoxOriginInBitmapPx);
}
