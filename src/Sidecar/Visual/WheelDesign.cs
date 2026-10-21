using System.Drawing;

namespace Sidecar;

/// <summary>
/// 視覺方向 **D「冷鋼薄荷・對比加強」** 的設計權杖（2026-10-01 由使用者拍板，含同日三項調整）。
///
/// 這個類別只放**常數與純函式**：沒有任何繪製、視窗、狀態或 I/O。
/// 繪製在 <see cref="WheelPresenter"/>，玻璃快照在 <see cref="GlassSurface"/>。
///
/// 數值來源（兩者必須一致，改動前先看這裡）：
///   - 互動原型 `Desktop\viz\tangerine-3c72cda8.html`（其 SVG 單位即真實 px）
///   - 實測與量測：`docs/aegis/work/2026-10-01-glass-spike/91-round2-d-palette.md`
///   - 外觀權威圖：`docs/design/reference/spike-glass-d-*-design-3x.png`
/// </summary>
internal static class WheelDesign
{
    // ================= 玻璃 =================

    /// <summary>玻璃暗罩 <c>rgba(8,11,15,.80)</c>。**不可調低**：淺色桌布實測需 ≥ 60 % 白字才可讀。</summary>
    public static Color Veil => Palettes.Current.Veil;

    /// <summary>模糊半徑（px）。3 次可分離 box blur ⇒ σ ≈ 17.5，對應設計的 blur(18px)。</summary>
    public const int BlurRadiusPx = 17;

    // ================= 扇區 =================

    /// <summary>扇區填色（交替）：<c>rgba(150,180,205,.20)</c>。</summary>
    public static Color FillA => Palettes.Current.FillA;

    /// <summary>扇區填色（交替）：<c>rgba(70,95,118,.13)</c>。</summary>
    public static Color FillB => Palettes.Current.FillB;

    /// <summary>選中扇區填色 <c>#3F6B7A</c>（**不透明**——這是選中唯一的亮度手段，暗罩下沒有下探空間）。</summary>
    public static Color SelFill => Palettes.Current.SelFill;

    /// <summary>選中扇區描邊 **琥珀 `#FFC24D`**（使用者 2026-10-01 拍板）。
    /// 舊值 `#5AF2D6` 與外緣薄荷線色相只差約 27°，會被誤認成外緣線加粗；
    /// 琥珀與薄荷近乎互補（約 129°）。與選中填色的 WCAG 對比 3.64:1（非文字門檻 3:1 通過）。</summary>
    public static Color SelStroke => Palettes.Current.SelStroke;

    public const float SelStrokePx = 2f;

    /// <summary>選中扇區外擴（px）。⚠ 玻璃圓盤的判定半徑必須含這一項，否則整條描邊會被裁掉。</summary>
    public const int SelExpandPx = 4;

    /// <summary>外緣髮絲線 <c>rgba(173,249,235,.55)</c>（薄荷往白混 50 %）。</summary>
    public static Color Rim => Palettes.Current.Rim;

    public const float RimPx = 1f;

    /// <summary>內圈髮絲線（外緣往內 3.4px 的第二條細線）。</summary>
    public static Color InnerHair => Palettes.Current.InnerHair;

    public const float InnerHairPx = 0.8f;

    // ================= 標籤 =================

    public static Color Label => Palettes.Current.Label;

    /// <summary>未選中扇區的文字不透明度（原案 .55 → 對比加強版 .42）。</summary>
    public const float LabelDimOpacity = 0.42f;

    /// <summary>扇區間隙（度）。2 扇區 8°、3 扇區 3°、4 扇區 2°（間隙取代分隔線）。</summary>
    public static double GapDeg(int count) => count switch
    {
        2 => 8.0,
        3 => 3.0,
        4 => 2.0,
        _ => 1.5
    };

    /// <summary>扇區掃描角＝360/count − 間隙。</summary>
    public static double SweepDeg(int count) => count > 0 ? 360.0 / count - GapDeg(count) : 0.0;

    /// <summary>標籤字級（px，1× 的真實像素）。</summary>
    public static float LabelFontPx(int count) => count switch
    {
        2 => 15f,
        3 => 12.7f,
        4 => 11.3f,
        _ => 11f
    };

    /// <summary>標籤半徑（佔輪盤半徑的比例）。扇區越多、字越外面。</summary>
    public static float LabelRadiusRatio(int count) => count switch
    {
        2 => 0.66f,
        3 => 0.72f,
        4 => 0.76f,
        _ => 0.72f
    };

    /// <summary>
    /// 工具扇區的線稿圖示尺寸（px，1× 的真實像素）。M3 工具輪盤用。
    ///
    /// ⚠ **這個值不在原案的常數表裡**（M1 只有一層輪盤，沒有工具圖示），
    /// 出處是參考版式 `docs/design/reference/wheel-tools-*.jpg`：工具扇區是「圖示在上、文字在下」，
    /// 參考圖裡圖示的可視高度約為標籤字高的 1.5 倍 ⇒ <see cref="LabelFontPx"/>(1) = 11px × 1.5 ≈ 17px。
    /// 量到的來源寫在這裡而不是散在繪製碼裡：**幾何值的唯一權威仍然只有這個檔案**。
    /// 改它會連帶改變文字往下讓開的距離（`WheelPresenter.DrawWheel` 的 `iconLift`）。
    /// </summary>
    public const float ToolIconPx = 17f;

    // ================= 光暈／投影（依桌布亮度自動切換） =================

    /// <summary>桌布平均亮度門檻：高於它＝淺色桌布。**不是旗標**——正式程式只拿得到畫面，沒人會告訴它深淺。</summary>
    public const double LightBackdropLuma = 140.0;

    /// <summary>深色桌布用的薄荷亮暈（原型值）。</summary>
    public static Color GlowOnDark => Palettes.Current.GlowOnDark;

    /// <summary>淺色桌布用的深青暈。亮暈在淺底上等於隱形，要的是「浮起來」的暗暈。</summary>
    public static Color GlowOnLight => Palettes.Current.GlowOnLight;

    public static bool IsLightBackdrop(double meanLuma) => meanLuma > LightBackdropLuma;

    public static Color Glow(double meanLuma) => IsLightBackdrop(meanLuma) ? GlowOnLight : GlowOnDark;

    /// <summary>盤外柔投影的最大不透明度（0–255）。</summary>
    public static int ShadowMax(double meanLuma) => IsLightBackdrop(meanLuma) ? 130 : 110;

    // ================= 光暈的強度與厚度（2026-10-01 依設計稿實測） =================
    //
    // 量測方式＝色度（G−R）反推「已合成的 alpha」：薄荷光暈 G−R＝78、淺底深青暈 G−R＝74，
    // 灰桌布 ≈ 0，所以 (G−R)/78 × 255 就是該點的光暈 alpha，不受桌布明暗起伏干擾。
    // ⚠ 量到的是**合成後**的 alpha（含投影壓暗），不是圖層自己的 alpha；
    //   換算圖層 alpha 時忘了除以 (1−投影) 就會高估，很容易誤判成「實作太濃」。
    //
    // 實測（盤緣外距離 → 合成 alpha）：
    //   深色底：設計稿 +6px 108、+14px 65、+26px 21；舊實作 +6px 88、+14px 49、+26px 10
    //           ⇒ **相符**（實作略窄），深色分支不需改。
    //   淺色底：設計稿 +6px 76、+14px 48；實作 +6px 117、+14px 70（×1.8）⇒ **約 1.5 倍過濃**。
    // 使用者 2026-10-01 在實機看到「光暈變成一大圈」後定案：**淺色放棄 ×1.8**，
    // 改為對齊設計稿的 94（實測合成 83／46，對設計稿 76／48）。
    // 這條覆蓋了先前對 spike §8.4 問題②「淺底加亮」的核可——那次是在設計稿上判斷的，
    // 沒有在真實桌面上看過。

    /// <summary>盤緣處的**累積**不透明度（0–255）。</summary>
    public const int GlowRimAlphaDark = 113;
    public const int GlowRimAlphaLight = 94;

    /// <summary>
    /// 光暈總開關。2026-10-01 使用者實機測試：深色底即使已對齊設計稿，仍被看成「一大圈」，
    /// 故先關掉做排除法——關掉後若那一圈還在，就是投影而不是光暈。
    /// **還原＝改成 true**；強度、厚度、圈數等參數全部保留，沒有刪除任何一個。
    /// 刻意用 static readonly 而非 const：const 會讓編譯器把 if 折成不可達程式碼並發 CS0162。
    /// </summary>
    public static readonly bool GlowEnabled = false;

    public static int GlowRimAlpha(double meanLuma) =>
        IsLightBackdrop(meanLuma) ? GlowRimAlphaLight : GlowRimAlphaDark;

    /// <summary>
    /// 柔暈從盤緣外幾 px 才開始。設計稿在盤緣外留一圈空隙（spike 的 GlowInsideDisc 把
    /// 盤內＋選中外擴那 4px 的光暈歸零）；不留的話光暈會貼著玻璃邊，看起來像一條亮環。
    /// </summary>
    public const float GlowStartPx = 3f;

    /// <summary>柔暈衰減厚度（px）。設計稿實測約在盤緣外 39px 處歸零。</summary>
    public const float GlowSpanPx = 36.4f;

    /// <summary>光暈疊幾圈。**只影響階梯的細緻度，不影響總量**（總量由 <see cref="GlowRimAlpha"/> 決定）。</summary>
    public const int GlowRings = 14;

    /// <summary>
    /// 扇形外緣尖點的圓角半徑（px）。使用者 2026-10-01：「從圓形分割出來的區塊，
    /// 可以加一點點圓角不要這麼銳利」。7px 對 110px 的輪半徑約 6%，是「一點點」的量級：
    /// 尖點鈍掉、但扇區還是扇區。內側（半徑 47 的圓孔邊）不做圓角——那裡被來源縮圖蓋住。
    /// </summary>
    public const float SectorCornerPx = 7f;

    // ================= 中心：來源縮圖 ＋ 膠囊 =================

    /// <summary>中心鏤空半徑對輪盤半徑的比例（原型 RI=47 / R=110）。</summary>
    public const float CenterRatio = 47f / 110f;

    /// <summary>膠囊底色＝白 `#FFFFFF` @ alpha .85（使用者要求「加一些透明度」）。</summary>
    public static Color CapBg => Palettes.Current.CapBg;

    public static Color CapFg => Palettes.Current.CapFg;

    public const float CapFontPx = 17f;      // 原 22px → 縮小
    public const float CapPadXPx = 16f;      // 原 8·24 → 5·16
    public const float CapPadYPx = 5f;
    public const float CapMinWidthPx = 100f;

    /// <summary>縮圖取不到時的佔位漸層（上→下）。</summary>
    public static Color ThumbTop => Palettes.Current.ThumbTop;
    public static Color ThumbBottom => Palettes.Current.ThumbBottom;

    /// <summary>佔位山的顏色（後山較亮、前山較暗）。</summary>
    public static Color ThumbRidgeBack => Palettes.Current.ThumbRidgeBack;
    public static Color ThumbRidgeFront => Palettes.Current.ThumbRidgeFront;

    /// <summary>縮圖內圈的細白環 <c>rgba(255,255,255,.16)</c>。</summary>
    public static Color ThumbRing => Palettes.Current.ThumbRing;

    // ================= 鍵帽提示 =================

    /// <summary>鍵帽藥丸：白底深字（實體鍵帽的視覺）。</summary>
    public static Color KeyPill => Palettes.Current.KeyPill;

    public static Color KeyPillFg => Palettes.Current.KeyPillFg;

    public const float KeyFontPx = 16f;
    public const float KeyCapFontPx = 12f;
    public const float KeyPadXPx = 12f;
    public const float KeyPadYPx = 5f;

    /// <summary>說明文字「按住觸發鍵」：白色字身 ＋ **暗色描邊**。
    ///
    /// 原型是純白 <c>rgba(255,255,255,.72)</c>，在淺色桌布上會整個消失（實測 714 個取樣點裡只有 53 個是暗的）。
    /// 這是**刻意偏離原型**的一處，理由與量測見 91 號文件 §8.2。</summary>
    public static Color KeyCaptionFill => Palettes.Current.KeyCaptionFill;
    public static Color KeyCaptionOutline => Palettes.Current.KeyCaptionOutline;
    public const float KeyCaptionOutlinePx = 3f;

    /// <summary>說明文字的固定字串（觸發鍵在輪盤可見期間恆為按住狀態，見 <see cref="DragDetector"/>）。</summary>
    public static string KeyCaptionText => UiStrings.Get("wheel.holdToConvert");

    // ================= 共用：通知／失敗的底色 =================

    /// <summary>
    /// 失敗／拒絕的底色 `rgb(150,40,40)`。輪盤的 notice 藥丸與進度列都用它——
    /// 同一個產品裡「這是錯誤」不可以有兩種紅（先前兩處各寫了一份字面值）。
    /// 透明度由各自的場合決定（輪盤 230、進度列 236）。
    /// </summary>
    public static Color NoticeFill => Palettes.Current.NoticeFill;
}
