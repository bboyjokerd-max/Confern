using System.IO;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Sidecar;

/// <summary>
/// 圖片 → **一頁** PDF 的唯一生產者（切片「圖片 → 單頁 PDF」，2026-10-14）。
///
/// ## 為什麼它不在 <see cref="CodecRegistry.CreateEncoder"/> 那條路上
/// 那一條回傳的是 WPF 的 <c>BitmapEncoder</c>（WIC）。PDF **不是** WIC 編碼器——它是一個容器，
/// 裡面放的是頁面與內嵌的影像物件，不是一個 <c>BitmapFrame</c>。硬把它塞進那個抽象，會讓五個既有
/// 格式的編碼路徑一起長出「這一種目標格式要另外處理」的分支。
/// ⇒ 政策仍然只有一個擁有者（<see cref="CodecRegistry"/> 說 PDF 是可輸出目標，並且說它由非 WIC 的
/// 路徑生產），機制集中在這個檔案；形狀與 <see cref="JpegMetadataStrip"/> 一致
/// （政策在 CodecRegistry、機制在 Formats/）。
///
/// ## 一頁與多頁（2026-10-14 同一天追加）
/// 上午做的是選項 A「每張圖各自一頁」；下午使用者追加了選項 B「一次選多個圖檔 ⇒ 合併成一份
/// 多頁 PDF」（原話：「可以新增一個『合併成PDF』的功能嗎?」）。兩者是同一個知識的兩個用法，
/// 所以**只有一份實作**：<see cref="WriteMany"/> 吃一批畫格，單張版本就是它的 N=1 特例。
/// 入口（一進一出 vs 多進一出）與排序 UI 屬於 <c>ConversionEngine.MergePdf</c>，不在這裡。
/// 多頁 TIFF／動畫 GIF 仍然只取第一頁／第一格——與規格 §6.1 對這兩種格式的既有處置一致。
///
/// ## 頁面大小是使用者的選擇（不是我們的）
/// 使用者對「合併時頁面多大」的回答是「可以提供選擇嗎?」，所以有三個模式
/// （見 <see cref="PdfPageSize"/>）。預設 <see cref="PdfPageSize.Original"/>＝與單張路徑
/// **逐位元同一條路**，那樣「圖片→PDF」的既有行為不可能因為這個功能而改變。
///
/// ## 頁面尺寸＝來源的實際 DPI
/// <c>pt = px × 72 / dpi</c>，DPI 取畫格自己報的 <c>DpiX</c>／<c>DpiY</c>，取不到（回 0）時用 96。
/// 這樣「1000×1000 px、300 dpi」的圖印出來是 240 pt 見方，而不是 750 pt——而且這一條**可以客觀
/// 斷言**：套件自己用同一個公式算出期望值，再從產物讀回頁面尺寸比對，不需要人眼看圖。
///
/// ## 不承諾的事（照實寫，免得被當成漏做）
/// ①**不承諾**內嵌影像的位元組與來源相同：這裡是把解碼後的畫格交給 PDFsharp 重新寫進 PDF 的
/// 影像物件，JPEG 的掃描資料不會原樣搬過去（那是 `merge-pdf` 才有的性質——它搬的是整頁物件）。
/// ②**不承諾**透明度會被保留：PDFsharp 對帶 alpha 的畫格怎麼處置，本切片沒有實測過，
/// 所以驗收也不含這一條。要承諾之前先量一次。
/// </summary>
/// <summary>
/// 合併多張圖時的**頁面大小**（使用者 2026-10-14：「可以提供選擇嗎?」）。
///
/// 三個模式的差別只在「一頁多大、圖畫在哪」，**不改任何一張圖的內容**；頁序一律是輸入順序。
/// </summary>
internal enum PdfPageSize
{
    /// <summary>每一頁＝那一張圖自己的實際尺寸（預設）。與「圖片 → 單頁 PDF」逐位元同一條路。</summary>
    Original,

    /// <summary>每一頁＝**第一張**的尺寸，其餘等比縮小置中。適合尺寸不一但想印成一本的掃描件。</summary>
    First,

    /// <summary>每一頁＝A4，等比縮小置中留邊。適合拿去列印或跟既有文件併在一起。</summary>
    A4
}

internal static class PdfFromImage
{
    /// <summary>畫格沒有 DPI 資訊時的假設值（螢幕的經典值，也是 WIC 的預設）。</summary>
    private const double FallbackDpi = 96.0;

    /// <summary>
    /// 來源像素 → PDF 點。全程用 double：整數除法會把 300 dpi 的圖算成四分之一大小，而那種錯
    /// 只有印出來才會發現。
    /// </summary>
    internal static double PointsFromPixels(int pixels, double dpi) =>
        pixels * 72.0 / (dpi > 0 ? dpi : FallbackDpi);

    /// <summary>A4 的寬與高（pt）。用算式寫，讓套件可以用同一個算式算出期望值再比對。</summary>
    internal static double A4WidthPoint => 210.0 * 72.0 / 25.4;

    /// <inheritdoc cref="A4WidthPoint"/>
    internal static double A4HeightPoint => 297.0 * 72.0 / 25.4;

    /// <summary>
    /// 把一批已解碼的畫格寫成**一份多頁** PDF（一頁一張，順序＝給定的順序）。
    ///
    /// ⚠ **寫檔契約不在這裡**：呼叫端負責 <c>AtomicWriter</c> 與 <c>NamingPolicy</c>
    /// （規格 §5 只有一個擁有者），本方法只把位元組寫進它拿到的那個 stream。
    ///
    /// ⚠ 失敗的分類也不在這裡：畫格是呼叫端解碼的，所以「這張圖解不開」必須由呼叫端在
    /// **進來之前**說出來（見 <c>ConversionEngine.MergeImagesToPdf</c>），否則使用者會拿到
    /// 一句「PDF 寫入失敗」，而真正的原因是他那張圖壞了。
    /// </summary>
    internal static int WriteMany(IEnumerable<BitmapSource> frames, Stream output, PdfPageSize size)
    {
        // ⚠ 參數是 IEnumerable 而不是 IReadOnlyList：呼叫端（合併多張圖那條路）要用**延遲**序列
        //    餵進來，這樣任何一刻都只有**一張**畫格在記憶體裡。第一版收成 List<BitmapSource>，
        //    一個 200 張 12MP 的掃描資料夾會吃到約 10 GB——而「拖一個資料夾」的主要情境正是它。
        //    介面收窄成 IEnumerable 之後，「先全部解好再寫」在型別上就寫不出來了。
        using var document = new PdfDocument();
        int pages = 0;
        double baseWidth = 0, baseHeight = 0;

        foreach (BitmapSource frame in frames)
        {
            if (pages == 0)
            {
                // 基準尺寸（First 模式用）：第一張自己的實際尺寸。
                baseWidth = PointsFromPixels(frame.PixelWidth, frame.DpiX);
                baseHeight = PointsFromPixels(frame.PixelHeight, frame.DpiY);
            }

            double width = PointsFromPixels(frame.PixelWidth, frame.DpiX);
            double height = PointsFromPixels(frame.PixelHeight, frame.DpiY);

            double pageWidth, pageHeight, drawWidth, drawHeight;
            switch (size)
            {
                case PdfPageSize.First:
                    pageWidth = baseWidth; pageHeight = baseHeight;
                    (drawWidth, drawHeight) = Fit(width, height, pageWidth, pageHeight);
                    break;
                case PdfPageSize.A4:
                    pageWidth = A4WidthPoint; pageHeight = A4HeightPoint;
                    (drawWidth, drawHeight) = Fit(width, height, pageWidth, pageHeight);
                    break;
                default:   // Original：這一條就是單張路徑，逐位元相同
                    pageWidth = width; pageHeight = height;
                    drawWidth = width; drawHeight = height;
                    break;
            }

            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);

            // 置中：縮小之後剩下的邊留白，而不是靠左上（靠左上會讓 A4 看起來像裁切過的）。
            double x = (pageWidth - drawWidth) / 2.0;
            double y = (pageHeight - drawHeight) / 2.0;

            using XGraphics graphics = XGraphics.FromPdfPage(page);
            using XImage image = XImage.FromBitmapSource(frame);
            graphics.DrawImage(image, x, y, drawWidth, drawHeight);
            pages++;
        }

        if (pages == 0)
            throw new ArgumentException("WriteMany needs at least one frame", nameof(frames));

        // closeStream: false —— 這個 stream 是 AtomicWriter 的，關掉它等於替別人做決定
        // （它還要 flush、還要 rename，那整段是規格 §5 的原子寫入）。
        document.Save(output, false);
        return pages;
    }

    /// <summary>等比縮到框內（**只縮不放**：放大會讓小圖變模糊，而那不是使用者的要求）。</summary>
    private static (double Width, double Height) Fit(double width, double height,
                                                     double boxWidth, double boxHeight)
    {
        double scale = Math.Min(boxWidth / width, boxHeight / height);
        if (scale > 1.0) scale = 1.0;
        return (width * scale, height * scale);
    }

    /// <summary>
    /// 把一個已解碼的畫格寫成**一頁** PDF（＝<see cref="WriteMany"/> 的 N=1 特例）。
    ///
    /// ⚠ **保留這一支而不是讓呼叫端自己包陣列**：它讓「單張路徑」與「多張路徑」在原始碼上
    /// 也只有一條實作，於是兩者不可能各自演化（見類別說明）。
    /// </summary>
    internal static void Write(BitmapSource frame, Stream output) =>
        WriteMany(new[] { frame }, output, PdfPageSize.Original);
}
