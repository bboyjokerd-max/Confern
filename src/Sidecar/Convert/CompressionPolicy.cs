namespace Sidecar;

/// <summary>
/// 「壓縮」工具（輪盤第三顆扇形，工具 id <c>compress</c>）的**等級政策**與其唯一擁有者。
///
/// 為什麼這是一個型別而不是幾個散落的常數／if：
/// 等級由**設定頁**決定（使用者 2026-10-06 明確裁定：不要在輪盤上放多顆扇形、也不要循環切換），
/// 而同一份政策要同時被四個人讀到：
///   ①設定頁（下拉的可選值、驗證、寫回檔案的值）；
///   ②<see cref="SettingsStore"/>（鍵→欄位的拒絕與夾制）；
///   ③worker 協議（跨行程的那一份副本）；
///   ④<see cref="ConversionEngine.Compress"/>（實際選品質、逼近上限）。
/// 四個地方各寫一份「有哪些等級」的話，遲早會出現「設定頁有這個選項、引擎不認得」這種
/// 存得下但沒生效的欄位——那正是本專案已經付出過代價的形狀。
///
/// ⚠ **向後相容的關鍵就在 <see cref="ModeStandard"/>**：它是 <see cref="Settings.CompressionMode"/>
/// 的預設值，而標準模式＝「照既有的 <c>JpgQuality</c>／<c>WebpQuality</c> 走」＝這個工具出現之前
/// 的唯一行為。所以一份**沒有這些新欄位**的舊設定檔載入之後，政策是 (standard, 0)，行為與
/// 改動前逐位元相同（證據見 <c>tools/verify-m3c.ps1</c> 的 S17.20）。
/// </summary>
/// <param name="Mode">
/// 四選一，正規寫法見 <see cref="ModeSmall"/>…<see cref="ModeTargetSize"/>。認不得的一律
/// 由 <see cref="CanonicalMode"/> 收斂成 <see cref="ModeStandard"/> 並回報（不靜默）。
/// </param>
/// <param name="TargetBytes">「壓縮到」的上限（位元組）。<c>&lt;= 0</c>＝沒有上限，模式不會是 target-size。</param>
/// <param name="StandardJpgQuality">標準模式要用的 JPG 品質（＝設定檔的 <c>JpgQuality</c>）。</param>
/// <param name="StandardWebpQuality">標準模式要用的 WebP 品質（＝設定檔的 <c>WebpQuality</c>）。</param>
internal sealed record CompressionPolicy(
    string Mode, long TargetBytes, int StandardJpgQuality, int StandardWebpQuality)
{
    /// <summary>小檔：固定較低的品質。</summary>
    public const string ModeSmall = "small";

    /// <summary>標準：維持既有的品質設定（<b>預設值；＝改動前的行為</b>）。</summary>
    public const string ModeStandard = "standard";

    /// <summary>高畫質：固定較高的品質。</summary>
    public const string ModeHigh = "high";

    /// <summary>壓縮到：反覆重編碼逼近使用者填的上限。</summary>
    public const string ModeTargetSize = "target-size";

    /// <summary>四個正規值，順序即設定頁下拉的順序。</summary>
    public static readonly string[] Modes = { ModeSmall, ModeStandard, ModeHigh, ModeTargetSize };

    /// <summary>
    /// 「小檔」用的品質。60 是規格 §8 品質滑桿的**下限**——也就是本產品已經認可的最低品質，
    /// 不是新發明的一個數字：低於它會出現明顯的方塊感，而這個工具的目的是「變小」不是「變醜」，
    /// 需要更小的檔案請改用「壓縮到」（它會在 60–100 之間找最能滿足上限的那一版）。
    /// </summary>
    public const int SmallQuality = 60;

    /// <summary>
    /// 「高畫質」用的品質。95 而不是 100：JPEG 在 100 幾乎不再變小（實測 200×150 的雜訊圖
    /// 品質 100 = 59 KB、品質 95 = 40 KB，而 PSNR 的差別在那個尺寸下看不出來），
    /// 100 等於這個工具什麼都沒做。真正要「最接近無損」請用「標準」把滑桿拉到 100。
    /// </summary>
    public const int HighQuality = 95;

    /// <summary>「壓縮到」的上限範圍（KB）。上限本身由設定頁驗證，這裡是**共同的權威**。</summary>
    public const int MinTargetKb = 1;

    /// <summary>
    /// 荒謬的大值：1 GiB（1048576 KB）。選這個數字是因為超過它的「上限」已經大於任何一張
    /// 這個產品編得出來的圖（實測：2816×2816 的雜訊 PNG 也才 23 MB），等於沒有上限——
    /// 那不是「壓縮到」，是打錯字。
    ///
    /// ⚠ 這是**拒絕理由**，不是夾制目標：使用者填了一個做不到的要求時，靜默夾成別的值
    /// 只會讓他以為設定生效了。
    /// </summary>
    public const int MaxTargetKb = 1024 * 1024;

    /// <summary>預設政策：標準模式、沒有上限。與 <see cref="Settings"/> 的預設值一致。</summary>
    public static readonly CompressionPolicy Default = new(ModeStandard, 0, 85, 85);

    /// <summary>設定檔裡**沒有這些欄位**時，<see cref="Settings"/> 的欄位會是這些值。</summary>
    public const string DefaultMode = ModeStandard;
    public const int DefaultTargetKb = 0;

    /// <summary>
    /// 由設定產生政策。**這一條就是「設定頁決定等級」的落地點**：引擎拿到的政策只有一個來源。
    /// </summary>
    public static CompressionPolicy FromSettings(Settings s) => new(
        CanonicalMode(s.CompressionMode),
        Math.Clamp(s.CompressionTargetKb, 0, MaxTargetKb) * 1024L,
        s.JpgQuality,
        s.WebpQuality);

    /// <summary>
    /// 任意字串 → 正規模式名，認不得就回**空字串**（呼叫端自己決定要拒絕還是退回預設）。
    /// 刻意不回 null：空字串在這個專案是「沒有這個值」，而 null 是「沒有這回事」——兩者不同。
    /// </summary>
    public static string CanonicalModeOrEmpty(string? text)
    {
        string t = (text ?? "").Trim();
        foreach (string mode in Modes)
            if (string.Equals(mode, t, StringComparison.OrdinalIgnoreCase)) return mode;
        return "";
    }

    /// <summary>任意字串 → 正規模式名，認不得就回 <see cref="ModeStandard"/>（＝改動前的行為）。</summary>
    public static string CanonicalMode(string? text)
    {
        string canonical = CanonicalModeOrEmpty(text);
        return canonical.Length > 0 ? canonical : ModeStandard;
    }

    /// <summary>
    /// 格式 → 「標準」模式的品質。**這個 switch 是全檔唯一一份**：<see cref="Settings.QualityFor"/>
    /// （格式轉換用）與壓縮工具都走它，所以兩者不可能各自演化出不同的對應。
    /// </summary>
    public static int QualityForFormat(int jpgQuality, int webpQuality, string format) => format switch
    {
        "JPG" => jpgQuality,
        "WEBP" => webpQuality,
        _ => 100
    };

    /// <summary>標準模式在這個格式上要用的品質。</summary>
    public int StandardQualityFor(string format) =>
        QualityForFormat(StandardJpgQuality, StandardWebpQuality, format);

    /// <summary>
    /// 日誌與設定頁摘要共用的一行。**只在尾巴追加**（既有套件的斷言比對的是
    /// <see cref="SettingsStore.Describe"/> 的前綴與各 <c>key=value</c>），插在中間會弄紅它們。
    /// </summary>
    public string Describe() =>
        $"mode={Mode} target={(TargetBytes > 0 ? $"{TargetBytes}B" : "-")} " +
        $"standard={ModeStandard}(jpg {StandardJpgQuality}/webp {StandardWebpQuality})";

    /// <summary>這個格式的編碼器有沒有一個「品質」旋鈕可以轉（＝大小可以被品質逼近）。</summary>
    public static bool QualityControlsSize(string format) => format switch
    {
        "JPG" => true,
        // ⚠ PNG／BMP／TIF／GIF 是**無損／無損式**容器：WIC 的編碼器沒有品質參數
        // （見 CodecRegistry.CreateEncoder），調品質只會產生一個位元組完全相同的檔案。
        // WebP 有品質參數，但本機沒有編碼器（CodecRegistry.DeclareWebp），所以走不到這裡。
        _ => false
    };
}
