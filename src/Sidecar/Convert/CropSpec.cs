using System.Globalization;
using System.Windows;

namespace Sidecar;

/// <summary>
/// 裁切的比例預設（M3 第四顆扇形，工具 id <c>crop</c>）。**這是「有哪些比例」的唯一權威**：
/// 裁切視窗的按鈕列、worker 協議上的 <c>cropRatio</c> 欄位、以及引擎每一張圖各自換算的那一步
/// 全部讀它。三處各寫一份清單的話，遲早會出現「視窗畫得出來、引擎不認得」的組合。
///
/// ⚠ 它有**兩份清單**，而且兩份回答的是不同的問題：
///   · <see cref="CropRatios.Presets"/>＝**按鈕列**（五顆，畫面只能放這麼多）；
///   · <see cref="CropRatios.All"/>＝**合法值集合**（七個，含方向鈕切出來的兩個直式 id）。
/// 驗證與「認不得的值」訊息讀 <see cref="CropRatios.All"/>；畫按鈕讀
/// <see cref="CropRatios.Presets"/>。
///
/// ⚠ <see cref="CropRatios.IdFree"/> 是預設，也是**舊封包的缺席語意**：一個不帶 <c>cropRatio</c> 的
/// crop 工作＝自由裁切（只有使用者拖的那個正規化矩形，沒有比例約束）。
///
/// ⚠ 每一個比例都是**絕對**的：`16:9` 永遠是寬的、`9:16` 永遠是高的，兩者在直式照片與橫式
/// 照片上給出同一種方向。方向是**預設值自己的性質**，不是來源影像的性質。
/// </summary>
/// <param name="Id">穩定識別碼。會進 worker 協議與日誌，**不得就地改字**。</param>
/// <param name="Label">按鈕上的字（zh-Hant，比照圓心膠囊與設定頁）。</param>
internal sealed record CropRatioPreset(string Id, string Label);

/// <summary>
/// 比例預設的正規值集合與查表。**純函式、無狀態**（比照 <see cref="WheelDesign"/> 的紀律：
/// 值與規則放一處，繪製／互動／引擎各自引用）。
/// </summary>
internal static class CropRatios
{
    /// <summary>自由：沒有比例約束，使用者拖到什麼就是什麼。<b>預設值，也是缺席語意。</b></summary>
    public const string IdFree = "free";

    /// <summary>正方形。</summary>
    public const string IdSquare = "1:1";

    /// <summary>寬螢幕。</summary>
    public const string IdWide = "16:9";

    /// <summary>
    /// 直式影片（<see cref="IdWide"/> 的直式版）。2026-10-07 使用者要求：「裁切成功，目前只有直式
    /// 比例，也加上橫式的」——實測更正：當時的 16:9／4:3 都是**常數的橫式**，缺的其實是直式。
    /// 這兩個 id 因此是「同一種構圖的兩個方向」，不是兩種不同的構圖。
    /// </summary>
    public const string IdTall = "9:16";

    /// <summary>相機的經典比例。</summary>
    public const string IdClassic = "4:3";

    /// <summary>直式相機比例（<see cref="IdClassic"/> 的直式版）。</summary>
    public const string IdClassicTall = "3:4";

    /// <summary>原比例：這一張圖自己的長寬比（所以它**逐張不同**，見 <see cref="TargetRatio"/>）。</summary>
    public const string IdOriginal = "original";

    /// <summary>
    /// 「原比例轉 90 度」：同一張圖的高 ÷ 寬。**逐張不同，而且不看固定方向**——直式照片得到
    /// 橫式結果、橫式照片得到直式結果（2026-10-07 使用者拍板「原比例也一起做」）。
    ///
    /// ⚠ 名字刻意**不叫** <c>original-tall</c> 之類：它在直式來源上得到的是橫的，
    /// 任何暗示固定方向的名字都會在日誌、CLI 與 UI 上說謊。
    /// </summary>
    public const string IdOriginalFlipped = "original-flipped";

    public const string DefaultId = IdFree;

    /// <summary>
    /// **按鈕列**的五顆，順序＝由左到右。⚠ 這**不是**「可接受的值集合」（那是 <see cref="All"/>
    /// ——`9:16`／`3:4`／`original-flipped` 是正規值但沒有自己的按鈕，它們是「方向鈕」從
    /// `16:9`／`4:3`／`original` 切過去的結果）。
    /// 兩件事分開的理由：把「按鈕有幾顆」當成「哪些值合法」，會讓「切過去之後的那個 id」
    /// 在下一次驗證時變成非法值（日誌上大聲喊「認不得」、引擎卻明明支援它）。
    ///
    /// 排序：「自由」最左（預設值／缺席語意）、「原比例」最右（唯一的**逐張**比例），
    /// 中間三顆是絕對比例。方向鈕緊跟在這一列的最右邊（見 <c>CropWindow.BuildRatioRow</c>）。
    /// </summary>
    public static readonly CropRatioPreset[] Presets =
    {
        new(IdFree, "自由"),
        new(IdSquare, "1:1"),
        new(IdWide, "16:9"),
        new(IdClassic, "4:3"),
        new(IdOriginal, "原比例")
    };

    /// <summary>
    /// 全部八個正規值（＝**合法值集合**，也是「有哪些比例」的完整清單）：按鈕列的五顆
    /// ＋三個只由方向鈕產生的 id。驗證（CLI／worker／日誌）與「認不得的值」訊息一律讀這一個。
    /// </summary>
    public static readonly CropRatioPreset[] All =
    {
        new(IdFree, "自由"),
        new(IdSquare, "1:1"),
        new(IdWide, "16:9"),
        new(IdTall, "9:16"),
        new(IdClassic, "4:3"),
        new(IdClassicTall, "3:4"),
        new(IdOriginal, "原比例"),
        new(IdOriginalFlipped, "原比例 90°")
    };

    /// <summary>
    /// 方向的成對成員。切換鈕就是「在同一個構圖的兩邊來回」，所以它必須是**對稱**的
    /// （<c>Counterpart(Counterpart(x)) == x</c>），而 <c>free</c>／<c>1:1</c> 刻意不在表裡。
    /// </summary>
    private static readonly (string A, string B)[] DirectionPairs =
    {
        (IdWide, IdTall),
        (IdClassic, IdClassicTall),
        (IdOriginal, IdOriginalFlipped)
    };

    /// <summary>
    /// 這個 id 的**另一個方向**；沒有對應方向（自由／1:1）回 <c>null</c>。
    ///
    /// 回 null 而不是「原樣回自己」：那會讓「切了但沒變」看起來像切成功，而呼叫端要判斷的
    /// 正是「這顆鈕該不該停用」。兩種沒有方向的理由不同——自由沒有比例可談、1:1 是自己的鏡像
    /// （轉 90 度還是同一張正方形）。
    /// </summary>
    public static string? Counterpart(string? id)
    {
        string canonical = CanonicalId(id);
        foreach ((string a, string b) in DirectionPairs)
        {
            if (canonical == a) return b;
            if (canonical == b) return a;
        }
        return null;
    }

    /// <summary>
    /// 這個 id **由按鈕列上的哪一顆鈕代表**（＝它自己，或它的另一個方向）。
    ///
    /// 為什麼需要它：`9:16`／`3:4`／`original-flipped` 沒有自己的按鈕，它們是方向鈕從
    /// `16:9`／`4:3`／`original` 切出來的。自測要按「某一顆鈕」時，唯一正確的來源就是這條對應
    /// ——在測試裡自己寫「`9:16` 就按第三顆」是第二份規則，產品改了順序它就紅在錯的地方。
    /// </summary>
    public static string ButtonIdOf(string? id)
    {
        string canonical = CanonicalId(id);
        foreach (CropRatioPreset p in Presets)
            if (p.Id == canonical) return p.Id;
        return Counterpart(canonical) ?? DefaultId;
    }

    /// <summary>任意字串 → 正規 id；認不得的（含 null／空）回 <see cref="DefaultId"/>。</summary>
    public static string CanonicalId(string? text)
    {
        foreach (CropRatioPreset p in All)
            if (string.Equals(p.Id, (text ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return p.Id;
        return DefaultId;
    }

    /// <summary>這個 id 是不是一個正規值（用來判斷「送來的東西認不得」並大聲記 log）。</summary>
    public static bool IsCanonical(string? text)
    {
        string t = (text ?? "").Trim();
        foreach (CropRatioPreset p in All)
            if (string.Equals(p.Id, t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>顯示用的字。</summary>
    public static string LabelOf(string? id)
    {
        string canonical = CanonicalId(id);
        foreach (CropRatioPreset p in All)
            if (p.Id == canonical) return p.Label;
        return IdFree;
    }

    /// <summary>
    /// 這個比例預設在**這一張圖**上對應的目標長寬比（寬 ÷ 高，像素空間）。
    /// <c>null</c>＝這個預設不做任何比例約束（<see cref="IdFree"/>）。
    ///
    /// ⚠ <see cref="IdOriginal"/> 與 <see cref="IdOriginalFlipped"/> 的答案**逐張不同**——那正是
    /// 「原比例」的意思，而且它們是「批次＝同一比例」裡唯一刻意逐張變動的預設（後者是同一張圖轉
    /// 90 度，所以在直式照片上得到橫式、在橫式照片上得到直式：**不帶固定方向**）。
    ///
    /// ⚠ 其餘六個都是**常數**，而且刻意**不看影像方向**：`9:16` 就是一張直式的圖，
    /// 不管來源是直式還是橫式照片都一樣（同一顆比例套用到整個批次，每一張各自換算）。
    /// 「依來源自動翻轉」是**另一個**功能（「自動：直式來源就裁直式」），不是這裡；
    /// 把方向併進預設值會讓同一個 id 在批次裡對不同來源給出不同方向，
    /// 那正是 2026-10-06 拍板的「批次＝同一比例」要排除的事。
    /// </summary>
    public static double? TargetRatio(string? id, int imageWidth, int imageHeight)
    {
        bool usable = imageWidth > 0 && imageHeight > 0;
        switch (CanonicalId(id))
        {
            case IdSquare: return 1.0;
            case IdWide: return 16.0 / 9.0;
            case IdTall: return 9.0 / 16.0;
            case IdClassic: return 4.0 / 3.0;
            case IdClassicTall: return 3.0 / 4.0;
            case IdOriginal: return usable ? (double)imageWidth / imageHeight : null;
            case IdOriginalFlipped: return usable ? (double)imageHeight / imageWidth : null;
            default: return null;      // free
        }
    }
}

/// <summary>
/// 一次裁切的**完整請求**：一個 0..1 的正規化矩形 ＋ 一個比例預設 id。
///
/// 為什麼是正規化座標而不是像素：這是使用者 2026-10-06 拍板的「批次＝同一比例套用到每一張」。
/// 使用者框的是比例，拖進多張圖時**每一張依自己的尺寸各自換算**（見 <see cref="CropMath"/>）。
/// 若這裡存的是像素，批次就只能是「用第一張的尺寸套到每一張」，那是這條規則明文要避免的錯誤。
/// </summary>
/// <param name="X">左緣（0..1）。</param>
/// <param name="Y">上緣（0..1）。</param>
/// <param name="Width">寬（0..1）。</param>
/// <param name="Height">高（0..1）。</param>
/// <param name="RatioId">比例預設 id，見 <see cref="CropRatios"/>。</param>
internal sealed record CropSpec(double X, double Y, double Width, double Height, string RatioId)
{
    public static CropSpec Free(double x, double y, double w, double h) =>
        new(x, y, w, h, CropRatios.DefaultId);

    /// <summary>日誌與證據用的一行。**欄位只追加在尾巴**（既有套件比對前綴）。</summary>
    public string Describe() =>
        $"rect=({X.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{Y.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{Width.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{Height.ToString("0.####", CultureInfo.InvariantCulture)}) " +
        $"ratio={CropRatios.CanonicalId(RatioId)}";

    /// <summary>
    /// 解析命令列／診斷用的 <c>x,y,w,h</c>（**固定 InvariantCulture**：小數點必須是「.」，
    /// 否則同一個參數在不同的地區設定下會解析成不同的矩形，而驗收腳本沒有任何辦法察覺）。
    /// 不丟例外：解析不出來時回 false 並說出原因（呼叫端負責顯示）。
    /// </summary>
    public static bool TryParse(string? text, out CropSpec spec, out string error)
    {
        spec = Free(0, 0, 1, 1);
        error = "";
        string[] parts = (text ?? "").Split(',');
        if (parts.Length != 4)
        {
            error = $"expected four comma-separated numbers (x,y,w,h), got {parts.Length} part(s)";
            return false;
        }

        var values = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out values[i]) || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
            {
                error = $"'{parts[i].Trim()}' is not a number (use '.' as the decimal separator)";
                return false;
            }
        }

        if (values[2] <= 0 || values[3] <= 0)
        {
            error = $"the rectangle is empty (w={values[2]}, h={values[3]}); a crop needs a non-zero area";
            return false;
        }

        spec = Free(values[0], values[1], values[2], values[3]);
        return true;
    }
}

/// <summary>
/// 裁切結果的分類。**不丟例外**：這個專案的裁切路徑上任何一種退化輸入都必須當場被夾住或
/// 明確拒絕（見 <see cref="ConversionEngine.Crop"/> 的說明）。
/// </summary>
internal enum CropOutcome
{
    /// <summary>有一個非空、且不是整張圖的矩形。</summary>
    Ok,

    /// <summary>矩形是空的（寬或高換算後為 0）：沒有東西可以裁。</summary>
    EmptyRect,

    /// <summary>矩形（在這個尺寸上）等於整張圖：沒有東西可以裁。</summary>
    FullFrame
}

/// <summary>
/// 正規化矩形 → 像素矩形。**這是「同一比例套用到每一張」的唯一換算處**。
///
/// 換算規則（刻意寫死在這裡，不在引擎、也不在視窗）：
///   ① 先把正規化值夾進 [0,1]，且 <c>x+w ≤ 1</c>、<c>y+h ≤ 1</c>（超出邊界＝夾住，不是丟錯）；
///   ② **邊界取整法**：<c>left = round(x·W)</c>、<c>right = round((x+w)·W)</c>（
///      <see cref="MidpointRounding.AwayFromZero"/>），寬 = 右 − 左。取整用在**邊界**而不是寬度上，
///      所以 <c>x+w = 1</c> 一定得到 <c>right = W</c>（整張圖不會少一列），而
///      <c>x=0,w=1</c> 一定是逐像素的全圖。用「左 + 獨立取整的寬」會在右緣留下一條 1px 的縫或
///      多出一列，那種錯在批次裡每一張圖都不一樣大，最難發現；
///   ③ 夾制：<c>0 ≤ left &lt; W</c>，<c>left+1 ≤ right ≤ W</c>（其餘同理）⇒ 寬高**恆 ≥ 1 px**。
///      一個「不到 1 px 的矩形」（例如 w=0.0005 在 512px 上）因此被夾成 1 px，而不是變成 0。
///      選「夾成 1 px」而不是「拒絕」的理由：使用者的意圖（裁這一小塊）仍然表達得出來，
///      而 1 px 是**最小的非空裁切**；拒絕一個只因為太小就什麼都做不到的請求，
///      會讓「批次裡有一張小圖」變成一件要人工處理的事；
///   ④ **比例預設**（<see cref="CropRatios.TargetRatio"/>）：把上面算出來的像素框當成**上限**，
///      在框內取「與目標比例相符、面積最大、置中」的那一個矩形。這一條就是「都裁成中央 1:1，
///      不管原圖多大」的實作：同一個正規化矩形套到不同長寬比的圖上，**每一張都會被修成
///      該張自己的 1:1**，而不是「第一張的 1:1 硬套」。
///      ⚠ 修完之後仍可能是整張圖（例如 3px 的小圖被修成 3×3）⇒ 走 <see cref="CropOutcome.FullFrame"/>。
/// </summary>
internal static class CropMath
{
    /// <summary>最小邊長（px）。小於它的結果會被夾成這個值，見換算規則 ③。</summary>
    public const int MinSidePx = 1;

    /// <summary>Half-up 取整：<c>Math.Round</c> 預設是銀行家取整（0.5 → 偶數），
    /// 對「邊界」而言那會讓 1.5px 與 2.5px 往不同方向跑，同一條規則在兩張圖上不一致。</summary>
    private static int RoundHalfUp(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>把正規化矩形夾進合法範圍。回傳的矩形可能仍然「空」（x 夾到 1 時 w = 0）。</summary>
    public static CropSpec ClampNormalized(CropSpec spec)
    {
        double x = Clamp01(spec.X), y = Clamp01(spec.Y);
        double w = Clamp01(spec.Width), h = Clamp01(spec.Height);
        w = Math.Min(w, 1.0 - x);
        h = Math.Min(h, 1.0 - y);
        if (w < 0) w = 0;
        if (h < 0) h = 0;
        return new CropSpec(x, y, w, h, CropRatios.CanonicalId(spec.RatioId));
    }

    private static double Clamp01(double v) =>
        double.IsNaN(v) ? 0.0 : Math.Clamp(v, 0.0, 1.0);

    /// <summary>
    /// 這一張圖的像素矩形。<paramref name="notes"/> 收集「當場夾住了什麼」——
    /// 呼叫端負責把非空的 notes 寫進日誌（靜默的夾制與靜默的失敗一樣糟）。
    /// </summary>
    public static CropOutcome TryToPixels(CropSpec raw, int imageWidth, int imageHeight,
                                          out Int32Rect rect, out List<string> notes)
    {
        rect = default;
        notes = new List<string>();
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            notes.Add($"the image reports a degenerate size {imageWidth}x{imageHeight}");
            return CropOutcome.EmptyRect;
        }

        CropSpec spec = ClampNormalized(raw);
        if (spec.X != raw.X || spec.Y != raw.Y || spec.Width != raw.Width || spec.Height != raw.Height)
        {
            notes.Add($"normalized rect {Describe(raw)} was clamped to {Describe(spec)} " +
                      "(a crop rectangle must stay inside 0..1 on both axes)");
        }

        if (spec.Width <= 0 || spec.Height <= 0)
        {
            notes.Add("the normalized rectangle is empty (zero width or height)");
            return CropOutcome.EmptyRect;
        }

        int left = RoundHalfUp(spec.X * imageWidth);
        int right = RoundHalfUp((spec.X + spec.Width) * imageWidth);
        int top = RoundHalfUp(spec.Y * imageHeight);
        int bottom = RoundHalfUp((spec.Y + spec.Height) * imageHeight);

        left = Math.Clamp(left, 0, imageWidth - MinSidePx);
        top = Math.Clamp(top, 0, imageHeight - MinSidePx);
        right = Math.Clamp(right, left + MinSidePx, imageWidth);
        bottom = Math.Clamp(bottom, top + MinSidePx, imageHeight);

        int boxW = right - left, boxH = bottom - top;
        double exactW = spec.Width * imageWidth, exactH = spec.Height * imageHeight;
        if (exactW < MinSidePx || exactH < MinSidePx)
        {
            notes.Add($"the rectangle is thinner than one pixel on this image " +
                      $"({exactW.ToString("0.###", CultureInfo.InvariantCulture)}x" +
                      $"{exactH.ToString("0.###", CultureInfo.InvariantCulture)} px on " +
                      $"{imageWidth}x{imageHeight}) -> clamped to {boxW}x{boxH} px (the smallest non-empty crop)");
        }

        double? ratio = CropRatios.TargetRatio(spec.RatioId, imageWidth, imageHeight);
        if (ratio is { } r && r > 0)
        {
            int fitW = boxW, fitH = boxH;
            double wantW = boxH * r;         // 以框高為準時需要的寬
            if (wantW <= boxW)
            {
                fitH = boxH;
                fitW = RoundHalfUp(wantW);
            }
            else
            {
                fitW = boxW;
                fitH = RoundHalfUp(boxW / r);
            }
            fitW = Math.Clamp(fitW, MinSidePx, imageWidth);
            fitH = Math.Clamp(fitH, MinSidePx, imageHeight);
            int fitLeft = Math.Clamp(left + (boxW - fitW) / 2, 0, imageWidth - fitW);
            int fitTop = Math.Clamp(top + (boxH - fitH) / 2, 0, imageHeight - fitH);

            if (fitW != boxW || fitH != boxH || fitLeft != left || fitTop != top)
            {
                notes.Add($"ratio {CropRatios.CanonicalId(spec.RatioId)}: the {boxW}x{boxH} box became " +
                          $"{fitW}x{fitH} at ({fitLeft},{fitTop}) on this {imageWidth}x{imageHeight} image " +
                          "(largest rect with that ratio that fits the box, centred)");
            }
            left = fitLeft; top = fitTop; boxW = fitW; boxH = fitH;
        }

        if (boxW >= imageWidth && boxH >= imageHeight)
        {
            notes.Add($"the rectangle covers the whole {imageWidth}x{imageHeight} image");
            return CropOutcome.FullFrame;
        }

        rect = new Int32Rect(left, top, boxW, boxH);
        return CropOutcome.Ok;
    }

    /// <summary>像素矩形 → 正規化（視窗按下「確認」時走這一步；批次因此與視窗的實際尺寸無關）。</summary>
    public static CropSpec ToNormalized(Int32Rect rect, int imageWidth, int imageHeight, string ratioId)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return CropSpec.Free(0, 0, 1, 1);
        return new CropSpec(
            (double)rect.X / imageWidth, (double)rect.Y / imageHeight,
            (double)rect.Width / imageWidth, (double)rect.Height / imageHeight,
            CropRatios.CanonicalId(ratioId));
    }

    /// <summary>日誌用的簡寫。</summary>
    public static string Describe(CropSpec spec) =>
        $"({spec.X.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{spec.Y.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{spec.Width.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{spec.Height.ToString("0.####", CultureInfo.InvariantCulture)})";
}
