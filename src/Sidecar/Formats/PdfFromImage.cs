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
/// ## 一頁，不是多頁
/// 使用者 2026-10-14 裁定先做「A：每張圖各自一頁」。「多張合成一份多頁 PDF」是**另一種語意**
/// （多進一出），在本專案已經證明需要一個排序 UI（見 <c>MergeWindow</c> 與
/// <c>ConversionEngine.MergePdf</c>），所以**不在這裡**，而且刻意不預留半套介面。
/// 多頁 TIFF／動畫 GIF 因此只取第一頁／第一格——與規格 §6.1 對這兩種格式的既有處置一致。
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

    /// <summary>
    /// 把一個已解碼的畫格寫成**一頁** PDF。
    ///
    /// ⚠ **寫檔契約不在這裡**：呼叫端負責 <c>AtomicWriter</c> 與 <c>NamingPolicy</c>
    /// （規格 §5 只有一個擁有者），本方法只把位元組寫進它拿到的那個 stream。
    /// </summary>
    internal static void Write(BitmapSource frame, Stream output)
    {
        double width = PointsFromPixels(frame.PixelWidth, frame.DpiX);
        double height = PointsFromPixels(frame.PixelHeight, frame.DpiY);

        using var document = new PdfDocument();
        PdfPage page = document.AddPage();
        page.Width = XUnit.FromPoint(width);
        page.Height = XUnit.FromPoint(height);

        using (XGraphics graphics = XGraphics.FromPdfPage(page))
        using (XImage image = XImage.FromBitmapSource(frame))
        {
            graphics.DrawImage(image, 0, 0, width, height);
        }

        // closeStream: false —— 這個 stream 是 AtomicWriter 的，關掉它等於替別人做決定
        // （它還要 flush、還要 rename，那整段是規格 §5 的原子寫入）。
        document.Save(output, false);
    }
}
