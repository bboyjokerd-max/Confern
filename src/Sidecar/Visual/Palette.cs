using System.Drawing;

namespace Sidecar;

/// <summary>
/// 配色（**顏色的唯一權威**，2026-10-16 使用者：「可以提供不同配色選擇嗎？我希望這是個有設計質感的產品，
/// 可以提供基本五種配色」）。
///
/// 分工：這個檔案只放**數值**；<see cref="WheelDesign"/> 的同名屬性轉發到 <see cref="Palettes.Current"/>，
/// 所以那 100 個呼叫點（<see cref="WheelPresenter"/> 53 次、<see cref="SettingsUi"/> 29 次…）**一行都不用改**，
/// 也不會出現「某支程式讀到舊色票」這種半套狀態——只有一個地方決定現在是哪一套。
///
/// **不變的東西**（使用者 2026-10-01 已拍板，換色票不得動）：方向 D、光暈關閉
/// （<see cref="WheelDesign.GlowEnabled"/>）、扇形 7px 圓角、幾何與所有 alpha 之外的尺寸。
/// 每一套只改**材質與色相**，不改版面。
///
/// 為什麼每一套都只留**一個**暖色或亮色（SelStroke）：這是方向 D 的原始設計意圖——暗罩下
/// 「你即將承諾的那一扇」必須是畫面上唯一的亮點。石墨把這一點推到極致（無色相，只靠明度）；
/// 淺色那一套（porcelain）則反過來，靠**深**來當亮點。
/// </summary>
internal sealed record Palette
{
    public required string Id { get; init; }
    public required string NameZh { get; init; }
    public required string NameEn { get; init; }

    // ===== 玻璃 =====
    public required Color Veil { get; init; }
    // ===== 扇區 =====
    public required Color FillA { get; init; }
    public required Color FillB { get; init; }
    public required Color SelFill { get; init; }
    public required Color SelStroke { get; init; }
    public required Color Rim { get; init; }
    public required Color InnerHair { get; init; }
    // ===== 標籤 =====
    public required Color Label { get; init; }
    // ===== 光暈（目前關閉，但色票必須一起換：打開的那一天不能是舊色） =====
    public required Color GlowOnDark { get; init; }
    public required Color GlowOnLight { get; init; }
    // ===== 藥丸 =====
    public required Color CapBg { get; init; }
    public required Color CapFg { get; init; }
    // ===== 鍵帽 =====
    public required Color ThumbTop { get; init; }
    public required Color ThumbBottom { get; init; }
    public required Color ThumbRidgeBack { get; init; }
    public required Color ThumbRidgeFront { get; init; }
    public required Color ThumbRing { get; init; }
    public required Color KeyPill { get; init; }
    public required Color KeyPillFg { get; init; }
    public required Color KeyCaptionFill { get; init; }
    public required Color KeyCaptionOutline { get; init; }
    // ===== 提示條 =====
    public required Color NoticeFill { get; init; }
}

internal static class Palettes
{
    /// <summary>
    /// 預設＝<c>midnight</c>：**與 2026-10-01 拍板的原色逐位元相同**（這是回歸的錨，不得改動；
    /// 驗收會拿這一份跟舊二進位的 dump 比）。
    /// </summary>
    public const string DefaultId = "midnight";

    private static readonly Palette Midnight = new()
    {
        Id = "midnight", NameZh = "暮藍", NameEn = "Midnight",
        Veil = Color.FromArgb(204, 8, 11, 15),
        FillA = Color.FromArgb(51, 150, 180, 205),
        FillB = Color.FromArgb(33, 70, 95, 118),
        SelFill = Color.FromArgb(255, 63, 107, 122),
        SelStroke = Color.FromArgb(255, 255, 194, 77),
        Rim = Color.FromArgb(140, 173, 249, 235),
        InnerHair = Color.FromArgb(22, 255, 255, 255),
        Label = Color.FromArgb(255, 255, 255, 255),
        GlowOnDark = Color.FromArgb(255, 163, 241, 228),
        GlowOnLight = Color.FromArgb(255, 46, 120, 110),
        CapBg = Color.FromArgb(217, 255, 255, 255),
        CapFg = Color.FromArgb(255, 10, 15, 20),
        ThumbTop = Color.FromArgb(255, 51, 84, 124),
        ThumbBottom = Color.FromArgb(255, 21, 34, 56),
        ThumbRidgeBack = Color.FromArgb(255, 53, 96, 75),
        ThumbRidgeFront = Color.FromArgb(255, 30, 58, 46),
        ThumbRing = Color.FromArgb(41, 255, 255, 255),
        KeyPill = Color.FromArgb(242, 255, 255, 255),
        KeyPillFg = Color.FromArgb(255, 10, 15, 20),
        KeyCaptionFill = Color.FromArgb(235, 255, 255, 255),
        KeyCaptionOutline = Color.FromArgb(165, 8, 11, 15),
        NoticeFill = Color.FromArgb(255, 150, 40, 40)
    };

    /// <summary>
    /// 石墨：**沒有色相**的一整套。選中不是「變色」而是「變亮」（近白描邊），正好接上方向 D
    /// 「選中唯一的亮度手段」那條規則。像銑出來的鋁：中性、精準、不安慰人。
    /// </summary>
    private static readonly Palette Graphite = new()
    {
        Id = "graphite", NameZh = "石墨", NameEn = "Graphite",
        Veil = Color.FromArgb(204, 11, 11, 12),
        FillA = Color.FromArgb(51, 201, 205, 212),
        FillB = Color.FromArgb(33, 154, 160, 168),
        SelFill = Color.FromArgb(255, 58, 63, 70),
        SelStroke = Color.FromArgb(255, 242, 244, 247),
        Rim = Color.FromArgb(140, 230, 232, 236),
        InnerHair = Color.FromArgb(22, 255, 255, 255),
        Label = Color.FromArgb(255, 255, 255, 255),
        GlowOnDark = Color.FromArgb(255, 235, 238, 242),
        GlowOnLight = Color.FromArgb(255, 80, 84, 90),
        CapBg = Color.FromArgb(217, 255, 255, 255),
        CapFg = Color.FromArgb(255, 11, 11, 12),
        ThumbTop = Color.FromArgb(255, 74, 80, 88),
        ThumbBottom = Color.FromArgb(255, 34, 37, 42),
        ThumbRidgeBack = Color.FromArgb(255, 90, 96, 104),
        ThumbRidgeFront = Color.FromArgb(255, 52, 56, 62),
        ThumbRing = Color.FromArgb(41, 255, 255, 255),
        KeyPill = Color.FromArgb(242, 255, 255, 255),
        KeyPillFg = Color.FromArgb(255, 11, 11, 12),
        KeyCaptionFill = Color.FromArgb(235, 255, 255, 255),
        KeyCaptionOutline = Color.FromArgb(165, 11, 11, 12),
        NoticeFill = Color.FromArgb(255, 150, 40, 40)
    };

    /// <summary>
    /// 柑橘：**品牌本色**（Confern＝柑橘）。暖紙張質感，亮點是橙 <c>#FF9A3C</c>——
    /// 與薄荷那種「冷儀器」相反，這一套像午後的木桌。
    /// </summary>
    private static readonly Palette Citrus = new()
    {
        Id = "citrus", NameZh = "柑橘", NameEn = "Citrus",
        Veil = Color.FromArgb(204, 13, 9, 6),
        FillA = Color.FromArgb(51, 196, 168, 130),
        FillB = Color.FromArgb(33, 120, 92, 64),
        SelFill = Color.FromArgb(255, 122, 74, 34),
        SelStroke = Color.FromArgb(255, 255, 154, 60),
        Rim = Color.FromArgb(140, 255, 217, 168),
        InnerHair = Color.FromArgb(22, 255, 240, 220),
        Label = Color.FromArgb(255, 255, 250, 244),
        GlowOnDark = Color.FromArgb(255, 255, 205, 150),
        GlowOnLight = Color.FromArgb(255, 160, 80, 20),
        CapBg = Color.FromArgb(217, 255, 250, 242),
        CapFg = Color.FromArgb(255, 26, 16, 8),
        ThumbTop = Color.FromArgb(255, 150, 96, 48),
        ThumbBottom = Color.FromArgb(255, 74, 44, 18),
        ThumbRidgeBack = Color.FromArgb(255, 120, 86, 40),
        ThumbRidgeFront = Color.FromArgb(255, 70, 48, 20),
        ThumbRing = Color.FromArgb(41, 255, 235, 210),
        KeyPill = Color.FromArgb(242, 255, 250, 242),
        KeyPillFg = Color.FromArgb(255, 26, 16, 8),
        KeyCaptionFill = Color.FromArgb(235, 255, 248, 238),
        KeyCaptionOutline = Color.FromArgb(165, 13, 9, 6),
        NoticeFill = Color.FromArgb(255, 150, 40, 40)
    };

    /// <summary>
    /// 青瓷：釉色。rim 與描邊**同一色系**（只差明度），是最安靜的一套——扇區幾乎只剩輪廓，
    /// 適合長時間盯著看。
    /// </summary>
    private static readonly Palette Celadon = new()
    {
        Id = "celadon", NameZh = "青瓷", NameEn = "Celadon",
        Veil = Color.FromArgb(204, 7, 13, 11),
        FillA = Color.FromArgb(51, 168, 196, 184),
        FillB = Color.FromArgb(33, 96, 120, 108),
        SelFill = Color.FromArgb(255, 47, 83, 70),
        SelStroke = Color.FromArgb(255, 142, 230, 184),
        Rim = Color.FromArgb(140, 185, 242, 217),
        InnerHair = Color.FromArgb(22, 230, 255, 245),
        Label = Color.FromArgb(255, 246, 255, 250),
        GlowOnDark = Color.FromArgb(255, 190, 245, 225),
        GlowOnLight = Color.FromArgb(255, 40, 110, 90),
        CapBg = Color.FromArgb(217, 250, 255, 252),
        CapFg = Color.FromArgb(255, 8, 20, 16),
        ThumbTop = Color.FromArgb(255, 60, 110, 92),
        ThumbBottom = Color.FromArgb(255, 26, 52, 42),
        ThumbRidgeBack = Color.FromArgb(255, 80, 130, 110),
        ThumbRidgeFront = Color.FromArgb(255, 44, 78, 64),
        ThumbRing = Color.FromArgb(41, 235, 255, 245),
        KeyPill = Color.FromArgb(242, 250, 255, 252),
        KeyPillFg = Color.FromArgb(255, 8, 20, 16),
        KeyCaptionFill = Color.FromArgb(235, 246, 255, 250),
        KeyCaptionOutline = Color.FromArgb(165, 7, 13, 11),
        NoticeFill = Color.FromArgb(255, 150, 40, 40)
    };

    /// <summary>
    /// 絳紫：把對比拉到最大的一套。洋紅描邊 <c>#FFB8E1</c> 在紫墨底上幾乎是發光的，
    /// 適合「一次只做一件事、要很清楚現在選到哪」的人。
    /// </summary>
    private static readonly Palette Plum = new()
    {
        Id = "plum", NameZh = "絳紫", NameEn = "Plum",
        Veil = Color.FromArgb(204, 14, 10, 18),
        FillA = Color.FromArgb(51, 186, 160, 196),
        FillB = Color.FromArgb(33, 104, 80, 116),
        SelFill = Color.FromArgb(255, 78, 53, 89),
        SelStroke = Color.FromArgb(255, 255, 184, 225),
        Rim = Color.FromArgb(140, 231, 198, 245),
        InnerHair = Color.FromArgb(22, 250, 240, 255),
        Label = Color.FromArgb(255, 255, 250, 255),
        GlowOnDark = Color.FromArgb(255, 240, 205, 250),
        GlowOnLight = Color.FromArgb(255, 120, 40, 120),
        CapBg = Color.FromArgb(217, 255, 250, 255),
        CapFg = Color.FromArgb(255, 18, 10, 24),
        ThumbTop = Color.FromArgb(255, 110, 74, 140),
        ThumbBottom = Color.FromArgb(255, 46, 28, 64),
        ThumbRidgeBack = Color.FromArgb(255, 96, 72, 120),
        ThumbRidgeFront = Color.FromArgb(255, 52, 36, 70),
        ThumbRing = Color.FromArgb(41, 255, 240, 255),
        KeyPill = Color.FromArgb(242, 255, 250, 255),
        KeyPillFg = Color.FromArgb(255, 18, 10, 24),
        KeyCaptionFill = Color.FromArgb(235, 252, 246, 255),
        KeyCaptionOutline = Color.FromArgb(165, 14, 10, 18),
        NoticeFill = Color.FromArgb(255, 150, 40, 40)
    };

    /// <summary>
    /// 冷瓷（**淺色**）：整套唯一淺底的一套。亮點靠**深**（#1F4E79），標籤也跟著變深——
    /// 沒有這一份，白字會在白底上消失。⚠ 目前**不列入選單**：淺遮罩蓋在不同的桌面上
    /// 辨識度還沒實測，等實測過再放出來（放出來只要把 <see cref="Selectable"/> 加一筆）。
    /// </summary>
    private static readonly Palette Porcelain = new()
    {
        Id = "porcelain", NameZh = "冷瓷", NameEn = "Porcelain",
        Veil = Color.FromArgb(216, 242, 244, 246),
        FillA = Color.FromArgb(40, 74, 106, 134),
        FillB = Color.FromArgb(28, 40, 64, 86),
        SelFill = Color.FromArgb(255, 220, 230, 238),
        SelStroke = Color.FromArgb(255, 31, 78, 121),
        Rim = Color.FromArgb(150, 143, 169, 191),
        InnerHair = Color.FromArgb(28, 20, 40, 60),
        Label = Color.FromArgb(255, 18, 22, 26),
        GlowOnDark = Color.FromArgb(255, 200, 225, 240),
        GlowOnLight = Color.FromArgb(255, 46, 120, 110),
        CapBg = Color.FromArgb(232, 255, 255, 255),
        CapFg = Color.FromArgb(255, 16, 20, 24),
        ThumbTop = Color.FromArgb(255, 120, 140, 160),
        ThumbBottom = Color.FromArgb(255, 70, 86, 104),
        ThumbRidgeBack = Color.FromArgb(255, 96, 120, 104),
        ThumbRidgeFront = Color.FromArgb(255, 62, 84, 70),
        ThumbRing = Color.FromArgb(41, 30, 50, 70),
        KeyPill = Color.FromArgb(242, 255, 255, 255),
        KeyPillFg = Color.FromArgb(255, 16, 20, 24),
        KeyCaptionFill = Color.FromArgb(238, 255, 255, 255),
        KeyCaptionOutline = Color.FromArgb(165, 200, 210, 220),
        NoticeFill = Color.FromArgb(255, 170, 60, 60)
    };

    /// <summary>使用者在選單裡看得到的五套（順序＝設定頁的顯示順序，預設在第一個）。</summary>
    public static readonly Palette[] Selectable = { Midnight, Graphite, Citrus, Celadon, Plum };

    /// <summary>所有認得的 id（含尚未列入選單的），供 <see cref="TrySet"/> 解析。</summary>
    private static readonly Palette[] All = { Midnight, Graphite, Citrus, Celadon, Plum, Porcelain };

    private static Palette _current = Midnight;

    /// <summary>現在生效的一套。**唯一的讀取點**——換色票只會改這一個欄位。</summary>
    public static Palette Current => _current;

    public static string CurrentId => _current.Id;

    /// <summary>
    /// 換一套。認不得的 id **不做任何事並回 false**（呼叫端負責說出來）：
    /// 靜默退回預設會讓「我明明選了柑橘、結果還是暮藍」變成一個沒有錯誤訊息的錯。
    /// </summary>
    public static bool TrySet(string? id, out Palette applied)
    {
        applied = _current;
        if (string.IsNullOrWhiteSpace(id)) return false;
        foreach (Palette p in All)
        {
            if (string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _current = p;
                applied = p;
                return true;
            }
        }
        return false;
    }

    public static string Ids() => string.Join(", ", Array.ConvertAll(All, p => p.Id));

    /// <summary>選單用的 id 清單（不含實驗性的那幾套），給設定頁與 help 共用。</summary>
    public static string SelectableIds() => string.Join(", ", Array.ConvertAll(Selectable, p => p.Id));
}
