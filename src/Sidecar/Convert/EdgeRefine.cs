using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sidecar;

/// <summary>
/// 去背（工具 id <c>remove-bg</c>）的**全解析度邊緣精修**：把模型回來的低解析度遮罩，用
/// **原圖的亮度**當引導做一次 guided filter（He, Sun, Tang 2010），把高頻的邊緣結構搬進 alpha。
///
/// ============================ 為什麼需要這一刀（量出來的，不是猜的） ============================
/// 模型的前處理把短邊釘在 <see cref="RemoveBgModel"/> 的 512（<c>shortest_edge 512</c>、
/// <c>size_divisibility 32</c>、<c>do_pad false</c>），而遮罩之後被放大回**原圖尺寸**。
/// 所以在 1920x1281 的照片上，**遮罩的每一個像素覆蓋 2.5 個影像像素**：真正有資訊的只有
/// 767x512 那一格，放大只是把它塗開。實測的形狀是「階梯狀邊緣、毛髮糊成一團、外圍一圈灰色光暈、
/// 內部零星的孤點」——**不是**「邊緣太硬」：同一張 512x410 的人像 feathering 是 4.56%，
/// 1920x1281 的毛髮照是 15.09%，也就是這一顆工具本來就會羽化，缺的是**邊緣的細節**。
///
/// ============================ 演算法：兩階段，順序不能顛倒 ============================
/// <b>第一階段：對齊（guided filter）</b>。在一個半徑 r 的視窗裡，把輸出當成引導的線性函數
/// <c>q = a·I + b</c>，a／b 由視窗內的共變異數解出來（<c>a = cov(I,p)/(var(I)+ε)</c>、
/// <c>b = mean(p) − a·mean(I)</c>），再把重疊視窗的 a／b 平均起來：
/// <code>
/// mean_I  = box(I)                  mean_p = box(p)
/// var_I   = box(I·I) − mean_I²      cov    = box(I·p) − mean_I·mean_p
/// a = cov/(var_I+ε)                 b = mean_p − a·mean_I
/// q = box(a)·I + box(b)
/// </code>
/// 引導 I ＝**來源畫格的亮度**（0..1，<c>0.299R+0.587G+0.114B</c>），輸入 p ＝放大後的遮罩，
/// 輸出 q ＝對齊後的 alpha。
///
/// <b>第二階段：收窄（把放大倍率拿回來）</b>。<c>q' = clamp(0.5 + s_local·(q − 0.5))</c>，
/// 其中 <c>s_local = 1 + (s − 1)·gate</c>、<c>s = clamp(SharpenGain × 放大倍率, 1, SharpenMax)</c>、
/// <c>gate = GateEpsilon/(var(I)+GateEpsilon)</c>。**這一階段不是裝飾，它是量出來的補償**：
/// guided filter 本質上是平滑運算，第一階段把過渡帶**變寬、變軟**（1920x1281 毛髮照實測：
/// 過渡帶 15.09% → 22.91%、平均梯度 3.03 → 2.12），而放寬的來源正是它把 a、b 在視窗內平均掉。
/// s 綁在放大倍率上有兩個效果：①在 1920x1281 上把 ramp 收回（2.5 倍 ⇒ 上限 2.5，實測過渡帶上
/// 平均只用到 1.42 倍，因為閘門不是全開）；②在**比 512 小的圖**上放大倍率 &lt; 1 ⇒ s 夾成 1
/// ⇒ **這一階段自動不做**，因為那裡沒有瓶頸可以補（遮罩本來就是原生解析度）。
/// gate 讓收窄**只在引導平坦的地方全開**（＝只在「我們知道那裡沒有細節」的地方出刀）：
/// 有紋理的地方（毛髮、毛邊、樹葉）維持引導濾波給的軟 ramp，因為**半透明的毛髮是真的半透明**，
/// 把它硬化就是把它刪掉（見 <see cref="GateEpsilon"/> 的實測理由與極性反轉的經過）。
///
/// 為什麼是這一族而不是別的：①它是 O(pixels) 的（盒濾波用移動和，與半徑無關），不用重跑模型；
/// ②它對「p 在視窗內是常數」的區域**逐值保留**（cov=0 ⇒ a=0、b=p）——所以主體內部仍是 1.0、
/// 遠背景仍是 0.0，這一刀只動過渡帶，不會把一張好的遮罩揉壞；③它只需要一個引導影像，
/// 不需要 trimap 或使用者塗抹（alpha matting 的區域解需要一份可信的 trimap，而「哪裡是未知區」
/// 正是這一顆工具答不出來的事）。收窄那一階段用「端點 0／1 是不動點」的單調映射，所以
/// **0.5 的水平集不會移動**：它縮的是過渡帶的寬度，不是把主體放大或縮小（實測 opaque 佔比
/// 18.89% → 26.22% 是把過渡帶裡 &gt;0.5 的那些像素補成 1.0，不是膨脹）。
///
/// ============================ 參數（五個常數，全部量出來的） ============================
/// <b>r = 8</b>：模糊尺度是**放大倍率**給定的。1920x1281 的張量是 768x512（見
/// <see cref="RemoveBgModel.TensorSize"/>），放大 2.5 倍 ⇒ 遮罩的「一格」是 2.5 個影像像素，
/// 而階梯／塊狀的相關長度是 2–3 格 ≈ 7–8 px。r=8 的視窗（17x17）蓋得住它；r 太小的話
/// 視窗裡只有 ramp 的一段，解出來的 a、b 學不到那條邊在哪裡（實測對齊指標 EAC：r=8 → 0.4252、
/// r=4 → 0.2392、r=2 → 0.1682，單調往 r=8 走）。
/// <b>ε = 1e-4</b>：ε 是導引濾波的**正則化地板**（<c>var(I) ≫ ε</c> 時 a→cov/var，遮罩完全跟著
/// 引導走；<c>var(I) ≪ ε</c> 時 a→0，遮罩維持原本的區域平均）。掃過 r∈{2,3,4,8} × ε∈{1e-4,1e-3,1e-2}：
/// ε=1e-4 在 r=2／3／4 都是 EAC 最高的一格（例：r=2 是 0.0802 對 0.0640／0.0119），
/// 在 r=8 與 1e-3 只差 2.5%（0.2160 對 0.2215）⇒ 取小的一邊，因為 1e-4 讓遮罩更服從引導，
/// 而 1e-2 在每一格都幾乎不動（r=8 的 EAC 只有 0.1002）。
/// <b>SharpenGain = 1.0／SharpenMax = 3.0</b>：收窄倍率 ＝ 放大倍率本身（見上），上限 3 是
/// 「不要把羽化的頭髮變成硬邊」的保險。實測 gain∈{0, 0.8, 1.0, 1.3}：gain=0（只有對齊）
/// 留下 17.49–22.91% 的過渡帶與 2.12–2.51 的梯度，gain=1.0 收到 3.28–3.84% 與 10.4–11.5，
/// 而 gain=1.3 只再多收一點（2.69–3.15%）卻明顯開始把粗遮罩的切面畫出來 ⇒ 取 1.0。
/// <b>GateEpsilon = 3e-4</b>：亮度變異在它以下算「平坦」，收窄在那裡全開。這是**決策門檻**，
/// 跟 ε 的**正則化地板**是兩件事，所以刻意不是同一個值。極性與值都是量出來的（見
/// <see cref="GateEpsilon"/>）：舊的 <c>var/(var+1e-3)</c> 把飛散的毛髮吃掉了
/// （高紋理半透明像素只留 30.4%、其中 26286 px 被推到剛好 0），新的 <c>1e-3/(var+1e-3)</c>
/// 保留 79.3% 而格線鎖定反而更好（0.0023 → 0.0012）。
/// 五個常數的完整掃描表、以及三種被量掉的替代收窄方式，都在 <c>docs/aegis</c> 的進度日誌
/// 與驗收的 S22 裡。
///
/// ============================ 成本與決定性 ============================
/// 六個全尺寸 float 陣列（I、p、mean_I、mean_p 與兩個暫存），1920x1281 約 59 MB 峰值；
/// 盒濾波是移動和，**與半徑無關**（每個像素固定次數的加減），實測在 1920x1281 上是
/// **68–80 ms**（引擎日誌那一行會印出來）。全程**單執行緒、固定順序**：同一張輸入一定得到
/// 逐位元組相同的 PNG（S22 有一條斷言就釘著這件事），而且不會去動模型的 session
/// （推論仍然只有一次）。
/// </summary>
internal static class EdgeRefine
{
    /// <summary>精修開啟：guided filter（**預設值**，也是所有沒指定這個欄位的封包的意思）。</summary>
    public const string ModeGuided = "guided";

    /// <summary>精修關閉：alpha 就是放大後的原始遮罩（＝這一刀之前的行為）。</summary>
    public const string ModeOff = "off";

    /// <summary>兩個正規值。順序即說明文字與設定檔的列舉順序。</summary>
    public static readonly string[] Modes = { ModeGuided, ModeOff };

    /// <summary>
    /// 沒有指定時的模式。**刻意等於 <see cref="ModeGuided"/>**：這一刀預設是開的
    /// （使用者的要求），而「不指定」＝「預設」。
    /// </summary>
    public const string DefaultMode = ModeGuided;

    /// <summary>
    /// 引導濾波半徑 ＝ <c>clamp(round(RadiusGain × 放大倍率), 1, RadiusMax)</c>：**視窗要蓋住
    /// 粗遮罩的「格」**。一格是放大倍率那麼寬（1920x1281 是 2.5 px），而階梯／塊狀的相關長度是
    /// 2-3 格 ⇒ 3 格 ≈ 7-8 px 就是半徑的來源（實測對齊指標 EAC：r=8 → 0.4252、r=4 → 0.2392、
    /// r=2 → 0.1682，單調往大邊走）。反過來，**半徑跟著放大倍率縮小**是為了不製造新問題：
    /// 引導濾波會把過渡帶抹開約 2r，這是它自己的成本；遮罩越接近原生解析度，這個成本就越
    /// 不成比例（512x410 的對照圖放大倍率 0.80，固定 r=8 會讓過渡帶從 4.56% 變 21.74%、
    /// 平均梯度從 18.56 掉到 4.56——**比不精修還糟**，所以那裡根本就不該跑，見
    /// <see cref="HasBottleneck"/>）。
    /// </summary>
    public const float RadiusGain = 3.0f;

    /// <summary>半徑上限＝掃描出來的最佳值（放大倍率 2.5 時剛好取到 8）。</summary>
    public const int RadiusMax = 8;

    /// <summary>正規化亮度上的 ε（見類別說明的三個實測值）。</summary>
    public const float Epsilon = 1e-4f;

    /// <summary>
    /// 第二階段（把過渡帶收窄）的**倍率**常數：實際強度 ＝ <c>clamp(SharpenGain × 放大倍率, 1, SharpenMax)</c>。
    /// 1.0 ＝「剛好補回放大倍率造成的 ramp 寬度」；量測後調整（見類別說明）。
    /// </summary>
    public const float SharpenGain = 1.0f;

    /// <summary>收窄倍率的上限。超過它就會把羽化的頭髮變成硬邊（＝換一種假）。</summary>
    public const float SharpenMax = 3.0f;

    /// <summary>
    /// 「這裡的引導有多平坦」的門檻（收窄那一階段的閘門）。閘門值 ＝
    /// <c>GateEpsilon / (var(I) + GateEpsilon)</c>：亮度變異遠小於它 ⇒ 閘門全開（收窄全開）；
    /// 亮度有紋理（var(I) ≫ 它）⇒ 閘門趨近 0（收窄全關，維持引導濾波給的軟 ramp）。
    ///
    /// **為什麼極性是這一個方向（2026-10-08 第二輪實測後反轉，這是這一刀最重要的一次修正）**：
    /// 第一版把閘門寫成 <c>var(I)/(var(I)+ε)</c>，也就是「影像有邊的地方才收窄」。使用者拿
    /// 1920x1281 毛髮照的 1:1／2x 對照看過之後指出：那一版**把飛散的毛髮整片吃掉**，邊緣乾淨
    /// 但內容不見了。量出來的機制正是這樣——收窄是「以 0.5 為中心把 alpha 拉開」，任何
    /// <c>alpha &lt; 0.5/s</c> 的半透明像素都會被推到 0，而**毛髮本來就是合法的半透明**。
    /// 兩個指標（都在 <c>%TEMP%\rembg-refine\wisps.ps1</c> 的定義下、1920x1281、紋理門檻
    /// T = 9x9 平均 |∇Y| ≥ 0.03）：
    /// <list type="bullet">
    /// <item>舊極性：真正軟的「高紋理」像素（44813 px）只剩 **30.4%** 還是軟的，其中
    /// **26286 px 被推到剛好 0**（＝整根毛被刪掉，不是變硬），這一群的 alpha 平均從 0.244 掉到 0.146。</item>
    /// <item>新極性：同一群保留 **79.3%**，被推到剛好 0 的只剩 **2723 px**，alpha 平均 0.244 → 0.241
    /// （幾乎不動）。</item>
    /// <item>而「階梯」並沒有因此回來：格線鎖定 GridLock 由舊版的 0.0023 變成 **0.0012**（越低越好；
    /// 原始遮罩是 0.0523）。**格線鎖定本來就是第一階段（引導濾波）清掉的**——把收窄幾乎關掉
    /// （ε=1e-4）量到 0.0010，也就是說第二階段對去階梯沒有貢獻，它真正做的事只有「把變寬的
    /// ramp 收回來」；既然如此，就該讓它只在平坦處出刀。
    /// 過渡帶（0&lt;alpha&lt;255）仍由 15.09% 收窄到 10.97%，其中低紋理區的比例是舊版的 0.68 倍
    /// （舊版 0.40 倍）——**少收一點，但收的是該收的地方**。</item>
    /// </list>
    /// 值 ＝ 3e-4（＝亮度標準差 0.017 以上就算有紋理）。單調掃過這個常數：
    /// 1e-4 ⇒ 過渡帶 14.18%／高紋理區保留 85.1%／低紋理帶 0.90 倍；
    /// **3e-4 ⇒ 10.97%／79.3%／0.68 倍**；1e-3 ⇒ 7.88%／66.2%／0.48 倍。
    /// 取 3e-4 是「毛髮幾乎全留」與「平坦處仍收到 0.68 倍」之間的平衡點。
    ///
    /// 這一輪也量掉了三個替代方案（都**不做**，數字見驗收報告）：把收窄改成對**原始遮罩**的
    /// 局部平均值做 unsharp（過渡帶反而變寬到 23.76%、孤點 4 → 712）、對**濾波後**的 alpha 做
    /// unsharp（過渡帶 18.02–21.99%、孤點 380–572）、以及再加一道「遮罩自己有沒有跨越 0..1」的
    /// 閘門（過渡帶 22.58%）。原因同一個：局部平均值本身就帶著粗遮罩的階梯，放大它就等於把
    /// 階梯畫清楚。
    /// </summary>
    public const float GateEpsilon = 3e-4f;

    /// <summary>
    /// 任意字串 → 正規模式名，認不得就回**空字串**（呼叫端自己決定要拒絕還是退回預設）。
    /// 與 <see cref="CompressionPolicy.CanonicalModeOrEmpty"/> 同一條規矩：空字串是「沒有這個值」，
    /// null 是「沒有這回事」，兩者不同。
    /// </summary>
    public static string CanonicalModeOrEmpty(string? text)
    {
        string t = (text ?? "").Trim();
        foreach (string mode in Modes)
            if (string.Equals(mode, t, StringComparison.OrdinalIgnoreCase)) return mode;
        return "";
    }

    /// <summary>任意字串 → 正規模式名，認不得就回 <see cref="DefaultMode"/>（＝不指定時的行為）。</summary>
    public static string CanonicalMode(string? text)
    {
        string canonical = CanonicalModeOrEmpty(text);
        return canonical.Length > 0 ? canonical : DefaultMode;
    }

    /// <summary>這個模式要不要真的跑精修。</summary>
    public static bool IsOn(string? mode) => !string.Equals(CanonicalMode(mode), ModeOff, StringComparison.Ordinal);

    /// <summary>
    /// 遮罩放大的倍率 ＝ <c>max(寬比, 高比)</c>，用**模型實際跑的那張張量**算（不是理想比例：
    /// 3000x2250 的理想值是 682.67x512，模型實際回的是 672x512）。倍率 1 以下代表遮罩沒有被
    /// 放大（張量比原圖還大，模型是**縮**過的），也就是**這一顆工具唯一要修的那個缺陷不存在**。
    /// </summary>
    public static double UpscaleFactor(int width, int height, int tensorWidth, int tensorHeight)
    {
        if (tensorWidth <= 0 || tensorHeight <= 0)
            throw new InvalidOperationException(
                $"the tensor size {tensorWidth}x{tensorHeight} is not a size the model could have returned");
        return Math.Max((double)width / tensorWidth, (double)height / tensorHeight);
    }

    /// <summary>
    /// 「瓶頸夠大嗎」的門檻：**遮罩至少要被放大 1.5 倍**才值得精修。這一條是量出來的
    /// （五個放大倍率、四張不同的圖，全部走真正的 worker 路徑）：
    /// <list type="bullet">
    /// <item>x0.80（512x410 人像，張量 640x512 比原圖大）：過渡帶 4.56% → 21.74%、
    /// 平均梯度 18.56 → 4.56 ⇒ **比不精修還糟**。</item>
    /// <item>x1.05（806x538 毛髮）：過渡帶 13.83% → 15.53%、平均梯度 5.47 → 4.27
    /// ⇒ 還是**淨損失**（對齊有變好，但這一顆工具缺的是細節，不是對齊）。</item>
    /// <item>x1.60（1024x820，同一個人像 2x）：過渡帶 4.75% → 4.02%、梯度 9.71 → 9.73、
    /// 對齊 0.3596 → 0.6456 ⇒ **淨收益**。</item>
    /// <item>x2.40（1536x1230，3x）：過渡帶 4.58% → 2.52%、梯度 6.47 → 8.96、對齊 0.3076 → 0.7337
    /// ⇒ 明顯收益。</item>
    /// <item>x2.50（1920x1281 毛髮，本刀的頭號案例）：過渡帶 15.09% → 5.28%、梯度 3.03 → 7.81、
    /// 對齊 0.0325 → 0.4252 ⇒ 明顯收益。</item>
    /// </list>
    /// 交界落在 1.05（損失）與 1.60（收益）之間 ⇒ 門檻取 1.5（低於已知最小收益的那一格）。
    /// 機制上說得通：引導濾波自己會把過渡帶抹開約 2r，而收窄那一階段只補得回「放大倍率」那一份，
    /// 所以遮罩越接近原生解析度，這筆帳就越划不來。**門檻以下的輸出與 <see cref="ModeOff"/>
    /// 逐位元組相同**（S22 有一條斷言就釘著這件事）——不是「偷偷跑一點」，是完全不動。
    /// </summary>
    public const double MinBottleneck = 1.5;

    /// <summary>
    /// 有沒有值得修的瓶頸（＝遮罩是不是至少被放大了 <see cref="MinBottleneck"/> 倍）。
    /// 沒有瓶頸就沒有東西可以補——「不做事」才是正確的答案。
    /// </summary>
    public static bool HasBottleneck(double scale) => scale >= MinBottleneck;

    /// <summary>放大倍率 → 引導濾波半徑（見 <see cref="RadiusGain"/>／<see cref="RadiusMax"/>）。</summary>
    public static int RadiusFor(double scale) =>
        Math.Clamp((int)Math.Round(RadiusGain * scale), 1, RadiusMax);

    /// <summary>
    /// 精修前後的**當場可看的事實**：過渡帶的比例（0&lt;alpha&lt;255 的像素佔比，％）與耗時。
    /// 「有沒有做事」不該只靠「看起來比較好」——這一組數字會寫進日誌那一行。
    /// </summary>
    /// <param name="BandBeforePercent">精修前過渡帶佔比（％）。</param>
    /// <param name="BandAfterPercent">精修後過渡帶佔比（％）。</param>
    /// <param name="Scale">遮罩放大的倍率（＝瓶頸有多大：1920x1281 → 768x512 是 2.50）。</param>
    /// <param name="Sharpen">第二階段用的收窄倍率（1.0 ＝沒有收窄）。</param>
    /// <param name="Milliseconds">精修本身花掉的毫秒數（不含解碼、不含推論）。</param>
    public readonly record struct RefineStats(
        double BandBeforePercent, double BandAfterPercent, double Scale, float Sharpen, double Milliseconds)
    {
        /// <summary>
        /// 過渡帶像素上**實際**用到的收窄倍率平均（＝ <c>1 + (s−1)·gate</c> 在原始過渡帶上的平均）。
        /// 閘門讓每個像素的倍率都不一樣，所以只印 <see cref="Sharpen"/> 會高報：
        /// 那一個數字是**閘門全開時的上限**，而實測在 1920x1281 毛髮照上平均只有 1.42。
        /// </summary>
        public double SharpenMean { get; init; }
    }

    /// <summary>
    /// 來源畫格 → **引導用的亮度**（0..1、row-major、長度 <c>width*height</c>）。
    ///
    /// 用亮度而不是 RGB 三個通道各做一次：這一顆工具的產物是**單通道的 alpha**，
    /// 而三通道各自引導會產生三個互相矛盾的「邊在哪裡」（而且成本三倍）。
    /// 亮度係數是 BT.601 的 luma——本專案的像素就是 BGRA，不需要色彩管理。
    /// </summary>
    public static float[] GuideLuminance(BitmapSource frame, out int width, out int height)
    {
        byte[] pixels = ToBgra32(frame, out width, out height);
        var guide = new float[width * height];
        for (int i = 0, p = 0; i < guide.Length; i++, p += 4)
        {
            // 0.299/0.587/0.114：先乘再加，最後才除以 255（順序固定 ⇒ 同一張圖的結果逐位元相同）。
            guide[i] = (0.299f * pixels[p + 2] + 0.587f * pixels[p + 1] + 0.114f * pixels[p]) / 255f;
        }
        return guide;
    }

    /// <summary>
    /// 就地把 <paramref name="bgra"/> 的 **alpha 通道**換成精修後的結果（B／G／R 一個位元組都不動）。
    ///
    /// 兩個階段，**順序不能顛倒**，而且兩者都有實測的理由（見類別說明）：
    /// <list type="number">
    /// <item><b>對齊</b>：guided filter（半徑 <see cref="RadiusFor"/>、ε <see cref="Epsilon"/>）
    /// 用原圖亮度當引導，把過渡帶搬到影像自己的邊上。</item>
    /// <item><b>收窄</b>：<c>q' = clamp(0.5 + s_local·(q − 0.5))</c>，s ＝ <c>clamp(SharpenGain ×
    /// 放大倍率, 1, SharpenMax)</c>、<c>s_local = 1 + (s−1)·gate</c>。⚠ 這一階段**不是裝飾**：
    /// 引導濾波本質上是平滑運算，它把過渡帶**變寬**（實測 1920x1281：過渡帶 15.09% → 22.91%、
    /// 平均梯度 3.03 → 2.12），而放大倍率正是「那條 ramp 本來有多寬」。所以 s 與放大倍率綁在一起
    /// ——被放大的倍率就是要收回來的倍率。</item>
    /// </list>
    ///
    /// <paramref name="guide"/> 必須是**同一尺寸**的亮度（<see cref="GuideLuminance"/>）：
    /// 尺寸不符就當場丟例外，而不是拿一張對不上的引導去算（那會產生一個「看起來有做事、
    /// 其實每個像素都對錯位置」的遮罩）。
    /// <paramref name="scale"/> 是 <see cref="UpscaleFactor"/> 算出來的放大倍率（呼叫端已經用它
    /// 判斷過 <see cref="HasBottleneck"/>，這裡再算一次半徑與收窄倍率；同一個數字決定三件事，
    /// 所以不再重算）。
    /// </summary>
    public static RefineStats Apply(byte[] bgra, int width, int height, float[] guide, double scale)
    {
        int n = width * height;
        if (guide.Length != n)
            throw new InvalidOperationException(
                $"the guide has {guide.Length} values but the alpha plane is {width}x{height} ({n})");
        int radius = RadiusFor(scale);
        var sw = Stopwatch.StartNew();
        var p = new float[n];
        long bandBefore = 0;
        for (int i = 0, b = 3; i < n; i++, b += 4)
        {
            byte a = bgra[b];
            if (a > 0 && a < 255) bandBefore++;
            p[i] = a / 255f;
        }

        var meanI = new float[n];
        var meanP = new float[n];
        var s1 = new float[n];
        var s2 = new float[n];

        BoxMean(guide, meanI, width, height, radius);
        BoxMean(p, meanP, width, height, radius);

        for (int i = 0; i < n; i++) s1[i] = guide[i] * guide[i];
        BoxMean(s1, s2, width, height, radius);            // s2 = mean(I·I)
        for (int i = 0; i < n; i++) s2[i] -= meanI[i] * meanI[i];   // s2 = var(I)

        for (int i = 0; i < n; i++) p[i] = guide[i] * p[i];         // p 已經用完（mean_p 已算）
        BoxMean(p, s1, width, height, radius);             // s1 = mean(I·p)
        for (int i = 0; i < n; i++) s1[i] -= meanI[i] * meanP[i];   // s1 = cov(I,p)

        // 閘門（見 GateEpsilon）：var(I) 此刻還在 s2，而 p 已經空了 ⇒ 閘門就住在 p。
        // ⚠ **極性在 2026-10-08 的第二輪實測後反轉**：閘門 ＝ ε/(var(I)+ε)，也就是**引導越平坦、
        // 閘門越大**——收窄只在「影像平坦到我們知道那裡沒有細節」的地方全開。理由見 GateEpsilon。
        for (int i = 0; i < n; i++) p[i] = GateEpsilon / (s2[i] + GateEpsilon);

        for (int i = 0; i < n; i++) s1[i] /= s2[i] + Epsilon;       // s1 = a（s2 用完）
        for (int i = 0; i < n; i++) meanP[i] -= s1[i] * meanI[i];   // meanP = b（meanI 用完）

        BoxMean(s1, s2, width, height, radius);            // s2 = mean(a)
        BoxMean(meanP, meanI, width, height, radius);      // meanI = mean(b)

        float sharpen = (float)Math.Clamp(SharpenGain * scale, 1.0, SharpenMax);

        long bandAfter = 0;
        double localSum = 0;
        long localCount = 0;
        for (int i = 0, b = 3; i < n; i++, b += 4)
        {
            byte before = bgra[b];
            float q = s2[i] * guide[i] + meanI[i];
            // 收窄只在閘門開著（＝引導平坦）的地方全開（端點 0／1 是不動點 ⇒ 無跳不連續）。
            float local = 1.0f + (sharpen - 1.0f) * p[i];
            q = 0.5f + local * (q - 0.5f);
            if (before > 0 && before < 255)
            {
                localSum += local;
                localCount++;
            }
            byte a = (byte)Math.Clamp((int)Math.Round(q * 255f), 0, 255);
            if (a > 0 && a < 255) bandAfter++;
            bgra[b] = a;
        }

        sw.Stop();
        return new RefineStats(100.0 * bandBefore / n, 100.0 * bandAfter / n, scale, sharpen,
                               sw.Elapsed.TotalMilliseconds)
        {
            SharpenMean = localCount > 0 ? localSum / localCount : 1.0
        };
    }

    /// <summary>
    /// 盒濾波（**移動和**，O(1)／像素，與半徑無關）：<c>destination[y,x]</c> ＝ 以 (x,y) 為中心、
    /// 半徑 <paramref name="radius"/> 的視窗平均值。
    ///
    /// 邊界用**實際的視窗大小**當分母（不是把半徑夾住再除以固定的 (2r+1)²）：夾住半徑會讓
    /// 影像邊緣的濾波結果整體偏暗，而那正好是主體貼邊時最明顯的地方。
    /// <paramref name="source"/> 與 <paramref name="destination"/> **不可以是同一個陣列**：
    /// 視窗要減去的那一列已經被下一列的輸出覆蓋掉了（這個限制是這一層的契約，呼叫端已經照著排）。
    /// </summary>
    private static void BoxMean(float[] source, float[] destination, int width, int height, int radius)
    {
        if (ReferenceEquals(source, destination))
            throw new InvalidOperationException("BoxMean needs two buffers (the sliding window reads rows behind it)");

        var column = new double[width];
        for (int y = 0; y < height; y++)
        {
            int top = Math.Max(0, y - radius);
            int bottom = Math.Min(height - 1, y + radius);
            if (y == 0)
            {
                for (int yy = top; yy <= bottom; yy++)
                {
                    int row = yy * width;
                    for (int x = 0; x < width; x++) column[x] += source[row + x];
                }
            }
            else
            {
                if (y + radius < height)
                {
                    int row = (y + radius) * width;
                    for (int x = 0; x < width; x++) column[x] += source[row + x];
                }
                if (y - radius - 1 >= 0)
                {
                    int row = (y - radius - 1) * width;
                    for (int x = 0; x < width; x++) column[x] -= source[row + x];
                }
            }

            int rows = bottom - top + 1;
            double sum = 0;
            int firstRight = Math.Min(width - 1, radius);
            for (int x = 0; x <= firstRight; x++) sum += column[x];
            int outRow = y * width;
            for (int x = 0; x < width; x++)
            {
                if (x > 0)
                {
                    if (x + radius < width) sum += column[x + radius];
                    if (x - radius - 1 >= 0) sum -= column[x - radius - 1];
                }
                int columns = Math.Min(width - 1, x + radius) - Math.Max(0, x - radius) + 1;
                destination[outRow + x] = (float)(sum / (columns * rows));
            }
        }
    }

    /// <summary>
    /// 畫格 → Bgra32 的原始位元組。與 <see cref="RemoveBgModel"/> 裡那一份同一個作法、
    /// 同樣**刻意留在這一層**：那一份是 private，而兩邊一旦合併，改一邊就會動到另一邊
    /// （去背的推論路徑不該因為精修而改變前處理的位元組）。
    /// </summary>
    private static byte[] ToBgra32(BitmapSource frame, out int width, out int height)
    {
        BitmapSource bgra = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        width = bgra.PixelWidth;
        height = bgra.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[(long)stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
