using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Sidecar;

/// <summary>
/// 解碼支援程度。**為什麼要三態而不是 bool**：WebP 與 HEIC 的能力取決於「這台機器有沒有那個 codec，
/// 以及這個檔案是不是真的那個格式」，在還沒有檔案時**無法誠實地回答是或否**。
/// 硬用 bool 就會寫出「可量測地錯」的宣告（實測：WebP 在這台機器上解得出 2816×2816）。
/// </summary>
internal enum DecodeSupport
{
    /// <summary>啟動時就用自產樣本驗證過。</summary>
    Yes,

    /// <summary>無法在啟動時證明，改為拖進檔案時逐檔探測（HEIC／WebP）。</summary>
    PerFile,

    /// <summary>做不到。</summary>
    No
}

/// <summary>一個格式在這台機器上「真的能不能做」。<see cref="Evidence"/> 必須說明是怎麼量到的。</summary>
internal sealed record CodecCapability(string Format, DecodeSupport Decode, bool CanEncode, string Evidence);

/// <summary>解碼一個真實檔案的結局（規格 §6.3、§7.1）。</summary>
internal enum DecodeStatus
{
    /// <summary>解出來了。</summary>
    Ok,

    /// <summary>系統沒有這個格式的解碼器——HEIC 未安裝 HEIF 擴充時就是這個。</summary>
    NeedsExtension,

    /// <summary>本產品不支援的格式（含 `.lnk`）。</summary>
    Unsupported,

    /// <summary>檔案壞了／不是它宣稱的格式。</summary>
    Corrupt,

    /// <summary>讀不到（不存在、沒有權限、來源被鎖住、雲端佔位檔逾時）。</summary>
    Unreadable
}

/// <summary>解碼探測結果。<see cref="Detail"/> 一律附上實測到的細節（型別／HRESULT），不吞掉原因。</summary>
internal sealed record DecodeOutcome(DecodeStatus Status, string Format, string Detail);

/// <summary>
/// 唯一的格式知識來源（規格 §6）。
///
/// **核心原則：能力不是宣告的，是探測出來的。**
/// 目標格式清單＝「這台機器上真的編碼成功的格式」－原格式。做不到的格式**不出現扇區**，
/// 而不是出現之後才失敗——後者正是規格 §7.2 明列的缺陷（靜默失敗）。
///
/// 探測方式：用一張 2×2 的點陣圖**真的跑一次編碼**，再用自己產生的位元組**真的跑一次解碼**。
/// 探測結果只在同一行程內快取；跨行程重新探測（使用者可能剛裝了 HEIF 擴充）。
/// </summary>
internal sealed class CodecRegistry
{
    private const string ImageCategory = "IMAGE";

    /// <summary>
    /// 文件類別（**目前只有 PDF 一個成員**）。2026-10-07 隨工具 `merge-pdf` 加入。
    ///
    /// 為什麼非有這一列不可（這一條不是口味問題）：<see cref="FormatOfPath"/> 是格式的
    /// **唯一**查詢點，而工具目錄的適用性判定（`ToolCatalog.ToolsFor`）問的就是它。
    /// 在這一列存在之前，一個 `.pdf` 會回 <c>null</c>，於是**產品根本說不出「這是一份 PDF」**
    /// ——`merge-pdf` 也就永遠不可能被放行，因為沒有任何地方能誠實地回答「這一批是 PDF 嗎」。
    ///
    /// ⚠ **這一列不會把 PDF 變成可轉換的圖片**，三件事都由既有的程式碼分開保證：
    ///   ① <see cref="CategoryOf"/> 回 `"PDF"`，而 `FormatCatalog.ConvertibleCategories`
    ///      只認 `"IMAGE"` ⇒ 一份 PDF 仍然被判成 `PayloadStatus.Unsupported`，
    ///      第一層扇區（格式轉換）一個都不會出現——**行為與加入這一列之前完全相同**；
    ///   ② <see cref="Capability"/> 回 <see cref="DecodeSupport.No"/> ⇒
    ///      `FormatCatalog.Classify` 走的是 `"UNDECODABLE:PDF"` 那一條，不會被誤判成同類別，
    ///      所以 `[jpg, pdf]` 仍然是 `<see cref="PayloadStatus.Mixed"/>`（「請選同類別」）；
    ///   ③ PDF **不在** <see cref="MvpInputs"/>／<see cref="MvpOutputs"/>，
    ///      所以規格 §6.1／§6.2 的可輸入／可輸出清單一位元都沒動。
    ///
    /// 換句話說：這一列只補上「產品說得出這是一份 PDF」，**沒有承諾任何解碼或編碼能力**
    /// （§7.2 禁止靜默失敗——做不到的格式不出現扇區，而不是出現之後才失敗）。
    /// </summary>
    private const string PdfCategory = "PDF";

    /// <summary>格式表：名稱 →（副檔名、類別）。</summary>
    private static readonly (string Format, string[] Extensions, string Category)[] Table =
    {
        ("JPG",  new[] { "jpg", "jpeg" },  ImageCategory),
        ("PNG",  new[] { "png" },          ImageCategory),
        ("BMP",  new[] { "bmp" },          ImageCategory),
        ("TIF",  new[] { "tif", "tiff" },  ImageCategory),
        ("GIF",  new[] { "gif" },          ImageCategory),
        ("WEBP", new[] { "webp" },         ImageCategory),
        ("HEIC", new[] { "heic", "heif" }, ImageCategory),
        // ⚠ 放在**最後**：這個順序就是 `--codecs` 與 `--codec-probe` 的輸出行順序，
        // 而既有的套件比對的是那些行——把 PDF 插在中間會讓它們轉紅，而那不是在驗這件事。
        ("PDF",  new[] { "pdf" },          PdfCategory),
    };

    /// <summary>Windows 內建的 WIC 編碼器格式（經 WPF 包裝）。這五個是「應該要能用」的基準。</summary>
    private static readonly string[] WicEncoderFormats = { "PNG", "JPG", "BMP", "TIF", "GIF" };

    /// <summary>
    /// 建立編碼器——**編碼器知識的唯一來源**（轉檔引擎與能力探測都走這裡，
    /// 免得「探測的設定」與「實際轉檔的設定」各自演化）。
    /// 規格 §6.2：JPG 品質可設定（60–100）、TIFF 無壓縮、PNG 無損、BMP 32-bit（由引擎轉像素格式）。
    /// </summary>
    public static BitmapEncoder? CreateEncoder(string format, int quality) => format switch
    {
        "PNG" => new PngBitmapEncoder(),
        "JPG" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, MinEncodeQuality, MaxEncodeQuality) },
        "BMP" => new BmpBitmapEncoder(),
        "TIF" => new TiffBitmapEncoder { Compression = TiffCompressOption.None },
        "GIF" => new GifBitmapEncoder(),
        _ => null,
    };

    /// <summary>
    /// 品質的合法範圍（規格 §6.2 的 60–100）。這兩個常數是**唯一**的定義處：
    /// <see cref="CreateEncoder"/> 的夾回用它，命令列的「你把 20 夾成 60 了」那句提示也用它。
    /// 之前是兩處各寫一次 <c>60</c>／<c>100</c>，而那種重複壞掉的表現是
    /// 「安靜地換了一個品質」——使用者以為自己指定了 20。
    /// </summary>
    public const int MinEncodeQuality = 60;

    public const int MaxEncodeQuality = 100;

    /// <summary>
    /// 這個格式有沒有「**不解碼**的容器層去 metadata」路徑（工具 `strip-metadata`，規格 §9.4）。
    ///
    /// 政策寫在這裡的理由與 <see cref="CreateEncoder"/> 一樣：**格式知識只有一個擁有者**。
    /// 機制（怎麼拆 JPEG 的 marker）在 <see cref="JpegMetadataStrip"/>，但「哪些格式有這條路」
    /// 屬於格式知識——散到引擎裡就會出現第二個知道格式清單的地方。
    ///
    /// 目前只有 `JPG`。刻意**不**為 PNG／WebP 寫解剖器：它們走重編碼就已經逐像素相同
    /// （PNG／BMP／TIF／GIF 是無損容器），多寫兩個解析器只是多兩個要保養的東西
    /// ——規格 §9.4「被拒絕的選項 (b)」記的就是這件事。
    /// </summary>
    public static bool HasLosslessMetadataStrip(string format) =>
        string.Equals(format, "JPG", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 這個**目標**格式是不是由「非 WIC」的路徑生產（切片「圖片 → 單頁 PDF」，2026-10-14）。
    ///
    /// 為什麼這必須住在這裡：它問的是「**誰生產這個格式**」，那是格式知識。散到引擎裡就會出現
    /// 第二個知道格式清單的地方——與 <see cref="HasLosslessMetadataStrip"/> 同一條理由。
    ///
    /// 目前只有 `PDF`：<see cref="CreateEncoder"/> 給不出它（回傳型別是 <c>BitmapEncoder</c>，
    /// 而 PDF 是一個放頁面與內嵌影像物件的容器），生產者是 <see cref="PdfFromImage"/>。
    /// </summary>
    public static bool IsNonImageTarget(string format) =>
        string.Equals(format, "PDF", StringComparison.OrdinalIgnoreCase);

    /// <summary>規格 §6.1 宣告的**輸入**格式（MVP 承諾）。</summary>
    private static readonly string[] MvpInputs = { "JPG", "PNG", "BMP", "TIF", "GIF", "WEBP", "HEIC" };

    /// <summary>
    /// 規格 §6.2 宣告的**輸出**格式（MVP 承諾）。**GIF 與 HEIC 不在其中。**
    ///
    /// ⚠ **2026-10-14 修訂：加入 `PDF`**（切片「圖片 → 單頁 PDF」，使用者裁定原話：
    /// 「目前感覺只有『圖片->PDF』我覺得可以做」「這項應該就塞進 M1 就好了吧?」）。
    /// 這是**宣告**的修訂，不是偷偷放行：它讓 PDF 出現在第一層格式輪盤的目標清單裡，
    /// 而輪盤的內容本來就是「宣告 ∩ 這台機器真的做得到」（見建構子的註解）。
    /// **PDF 不是可解碼的來源**——`DeclarePdf` 的 `Decode` 仍然是 `No`，
    /// 所以 `--convert <pdf>` 仍然被拒、PDF 也仍然不會出現在來源格式裡。
    /// </summary>
    private static readonly string[] MvpOutputs = { "PNG", "JPG", "WEBP", "BMP", "TIF", "PDF" };

    /// <summary>
    /// 規格 §6.2 宣告的輸出矩陣——**對外的唯一查詢點**（2026-10-14 從 `Program.cs` 移除複本）。
    ///
    /// 為什麼要有這個屬性（這是一次**真的不一致**的量測，不是潔癖）：`--codecs` 的自我檢查
    /// 原本自己抄了一份 `string[] declared = { "PNG", "JPG", "WEBP", "BMP", "TIF" }`，
    /// 於是 PDF 一加進宣告矩陣，那份複本立刻成為**唯一說謊的地方**——
    /// `FAIL  offered [PNG,JPG,BMP,TIF,PDF] is a subset of the declared MVP matrix`，
    /// 而真正的宣告矩陣早就有 PDF 了。格式知識只有一個擁有者：要問就問這裡。
    /// </summary>
    internal static IReadOnlyList<string> DeclaredOutputs => MvpOutputs;

    private readonly List<CodecCapability> _capabilities = new();

    public CodecRegistry()
    {
        ProbeWicEncoders();
        DeclareWebp();
        DeclareHeic();
        DeclarePdf();

        // 兩個方向都會出錯，所以這裡把「宣告」與「實測」分開記錄：
        //   只信能力 → 多承諾：GIF 真的編得出來，但規格 §6.2 沒把它列為 MVP 輸出。
        //   只信宣告 → 假承諾：WebP 宣告了，但這台機器沒有編碼器（實測才發現）。
        // 真正提供的是兩者的交集；兩邊的差集都要留下來（否則它們會默默消失）。
        OfferedOutputs = MvpOutputs.Where(f => EncodableFormats.Contains(f)).ToArray();
        CapableButNotDeclared = EncodableFormats.Where(f => !MvpOutputs.Contains(f)).ToArray();
        DeclaredButUnavailable = MvpOutputs.Where(f => !EncodableFormats.Contains(f)).ToArray();
    }

    /// <summary>**實際提供**的輸出＝規格宣告的 ∩ 實測可用的。</summary>
    public IReadOnlyList<string> OfferedOutputs { get; }

    /// <summary>能力上可行、但 MVP 未宣告的輸出（目前是 GIF）——留著，免得它被忘記。</summary>
    public IReadOnlyList<string> CapableButNotDeclared { get; }

    /// <summary>MVP 宣告了、但這台機器做不到的輸出（目前是 WebP，缺 libwebp）。</summary>
    public IReadOnlyList<string> DeclaredButUnavailable { get; }

    /// <summary>每個格式的實測結果，順序與格式表一致。</summary>
    public IReadOnlyList<CodecCapability> Capabilities => _capabilities;

    public IReadOnlyList<string> DecodableFormats =>
        _capabilities.Where(c => c.Decode != DecodeSupport.No).Select(c => c.Format).ToArray();

    public IReadOnlyList<string> EncodableFormats =>
        _capabilities.Where(c => c.CanEncode).Select(c => c.Format).ToArray();

    /// <summary>可作為輸入的所有格式名稱（含「要靠系統擴充才成立」的，例如 HEIC）。</summary>
    public IReadOnlyList<string> InputFormats => MvpInputs;

    /// <summary>由路徑的副檔名取得格式名稱；不認得回 <c>null</c>。</summary>
    public string? FormatOfPath(string path)
    {
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext.Length < 2) return null;
        string bare = ext.TrimStart('.').ToLowerInvariant();
        foreach ((string format, string[] extensions, string _) in Table)
            if (extensions.Contains(bare, StringComparer.Ordinal)) return format;
        return null;
    }

    /// <summary>格式名稱 → 類別（用於「混合選取」判定，規格 §4.3）。</summary>
    public string? CategoryOf(string format)
    {
        foreach ((string f, string[] _, string category) in Table)
            if (string.Equals(f, format, StringComparison.OrdinalIgnoreCase)) return category;
        return null;
    }

    /// <summary>格式名稱的主要副檔名（輸出檔名用，規格 §5）。</summary>
    public string? PrimaryExtension(string format)
    {
        foreach ((string f, string[] extensions, string _) in Table)
            if (string.Equals(f, format, StringComparison.OrdinalIgnoreCase)) return extensions[0];
        return null;
    }

    /// <summary>
    /// 某個格式的**所有**副檔名（含別名，例如 JPG→jpg/jpeg）。
    ///
    /// 存在的理由：`confern formats` 要列「這個格式認哪些副檔名」，而那是
    /// <see cref="Table"/> 已知的事。**不可以**在顯示那一層再抄一份 switch——
    /// 抄一份就等于有兩個地方能說「jpeg 算 JPG」，總有一天只改到其中一個。
    /// 判定方向仍然只有 <see cref="FormatOfPath"/> 一條。
    /// </summary>
    public IReadOnlyList<string> ExtensionsOf(string format)
    {
        foreach ((string f, string[] extensions, string _) in Table)
            if (string.Equals(f, format, StringComparison.OrdinalIgnoreCase)) return extensions;
        return Array.Empty<string>();
    }

    public CodecCapability? Capability(string format) =>
        _capabilities.FirstOrDefault(c => string.Equals(c.Format, format, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 系統 HEIF 影像擴充是否安裝（規格 §6.3）。**這是觀測，不是能力宣告**：
    /// 真正的判定是拖進檔案時嘗試解碼——同一台機器上，不同檔案的結局可能不同。
    /// </summary>
    public static string HeifExtensionState()
    {
        const string apps = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(apps);
            if (key == null) return "unknown(registry-key-unreadable)";
            foreach (string name in key.GetSubKeyNames())
                if (name.StartsWith("Microsoft.HEIFImageExtension_", StringComparison.OrdinalIgnoreCase))
                    return "installed";
            return "missing";
        }
        catch (Exception ex)
        {
            return $"unknown({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// 真的去解一個檔案。**這是 HEIC 唯一的正確判定方式**（規格 §6.3）：
    /// 能不能解取決於系統 HEIF 擴充，而那件事在同一個檔案上是可量測的，不需要猜。
    ///
    /// 來源一律**唯讀**開啟並允許他人共用（規格 §5：原檔永不修改、時間戳不變）。
    /// 探測時用 <see cref="BitmapCacheOption.None"/>：只看標頭就能取得尺寸與像素格式，
    /// 不把整個檔案載進記憶體（§5「大檔：串流處理」）。
    /// </summary>
    public DecodeOutcome ProbeDecode(string path)
    {
        string format = FormatOfPath(path) ?? "?";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                                                         BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                return new DecodeOutcome(DecodeStatus.Corrupt, format, "decoder returned 0 frames");

            BitmapFrame frame = decoder.Frames[0];
            return new DecodeOutcome(DecodeStatus.Ok, format,
                $"{frame.PixelWidth}x{frame.PixelHeight} {frame.Format} frames={decoder.Frames.Count} " +
                $"size={new FileInfo(path).Length}B");
        }
        catch (FileNotFoundException) { return new DecodeOutcome(DecodeStatus.Unreadable, format, "file not found"); }
        catch (DirectoryNotFoundException) { return new DecodeOutcome(DecodeStatus.Unreadable, format, "folder not found"); }
        catch (Exception ex)
        {
            return new DecodeOutcome(ClassifyDecodeException(ex, format, path), format, DescribeDecodeException(ex, path));
        }
    }

    /// <summary>
    /// 解碼例外的**唯一分類權威**：探測（<see cref="ProbeDecode"/>）與實際轉檔都走這裡，
    /// 免得「探測說的話」與「轉檔說的話」各自演化。
    /// </summary>
    public static DecodeStatus ClassifyDecodeException(Exception ex, string format, string path) => ex switch
    {
        UnauthorizedAccessException => DecodeStatus.Unreadable,
        FileFormatException => DecodeStatus.Corrupt,
        NotSupportedException => IsHeifFormat(format) && LooksLikeHeif(path, out _)
            ? DecodeStatus.NeedsExtension
            : DecodeStatus.Unsupported,
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x88982F50u =>
            IsHeifFormat(format) && LooksLikeHeif(path, out _) ? DecodeStatus.NeedsExtension : DecodeStatus.Unsupported,
        System.Runtime.InteropServices.COMException com when CorruptHresults.Contains((uint)com.HResult) =>
            DecodeStatus.Corrupt,
        _ => DecodeStatus.Unsupported
    };

    /// <summary>給日誌用的細節：型別＋HRESULT＋訊息，並在 HEIF 檔上附上品牌盒判定結果。</summary>
    private static string DescribeDecodeException(Exception ex, string path)
    {
        string note = LooksLikeHeif(path, out string brand) ? $" heifBrand={brand}" : "";
        string inner = ex.InnerException == null
            ? ""
            : $" <- {ex.InnerException.GetType().Name} 0x{ex.InnerException.HResult:X8}";
        return $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}{note}: {Short(ex.Message)}{inner}";
    }

    private static string Inner(Exception ex) =>
        ex.InnerException == null ? "" : $" <- {ex.InnerException.GetType().Name} 0x{ex.InnerException.HResult:X8}";

    private static bool IsHeifFormat(string format) =>
        format is "HEIC";

    /// <summary>
    /// 「資料壞了」的 HRESULT 集合。實測而來，不是從文件抄的：
    /// `0xC00D36BE`＝檔案與相關的檔案格式規格不符（品牌盒正確、影像資料被截斷的 HEIC）；
    /// `0x88982F61`＝`WINCODEC_ERR_BADIMAGE`；`0x88982F62`＝`WINCODEC_ERR_BADHEADER`。
    /// </summary>
    private static readonly uint[] CorruptHresults = { 0xC00D36BEu, 0x88982F61u, 0x88982F62u };

    /// <summary>
    /// 前 12 個位元組是不是 HEIF 的 ISO-BMFF 品牌盒（`ftyp` + heic/heix/hevc/mif1…）。
    ///
    /// **用途是分辨兩種失敗**，否則一個改名成 `.heic` 的純文字檔會被誤報成「需要安裝 HEIF 擴充」：
    /// 「這是 HEIC，但系統沒有解碼器」→ 需要擴充；「這根本不是影像」→ 無法解碼。
    /// 只讀 12 個位元組，不載入整檔（規格 §5）。
    /// </summary>
    private static bool LooksLikeHeif(string path, out string brand)
    {
        brand = "?";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[12];
            if (stream.Read(header, 0, header.Length) < header.Length) return false;
            if (header[4] != (byte)'f' || header[5] != (byte)'t' ||
                header[6] != (byte)'y' || header[7] != (byte)'p') return false;

            brand = System.Text.Encoding.ASCII.GetString(header, 8, 4);
            return brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 真的編碼一次、再真的解碼一次。**失敗就是失敗**——回報不可用，不假裝可用。
    /// </summary>
    private void ProbeWicEncoders()
    {
        BitmapSource source = MakeProbeBitmap();

        foreach (string format in WicEncoderFormats)
        {
            byte[]? bytes = null;
            try
            {
                BitmapEncoder? encoder = CreateEncoder(format, 85);
                if (encoder == null)
                {
                    _capabilities.Add(new CodecCapability(format, DecodeSupport.No, false,
                        "no encoder factory for this format"));
                    continue;
                }
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                if (stream.Length > 0) bytes = stream.ToArray();
            }
            catch (Exception ex)
            {
                _capabilities.Add(new CodecCapability(format, DecodeSupport.No, false,
                    $"encode threw {ex.GetType().Name}: {Short(ex.Message)}"));
                continue;
            }

            if (bytes == null)
            {
                _capabilities.Add(new CodecCapability(format, DecodeSupport.No, false, "encode produced 0 bytes"));
                continue;
            }

            // 用剛產生的位元組解回來，解碼能力才算「量過」而不是「假設的」
            try
            {
                using var read = new MemoryStream(bytes);
                BitmapDecoder decoder = BitmapDecoder.Create(read, BitmapCreateOptions.PreservePixelFormat,
                                                             BitmapCacheOption.OnLoad);
                bool ok = decoder.Frames.Count >= 1 && decoder.Frames[0].PixelWidth == 2;
                _capabilities.Add(new CodecCapability(format, ok ? DecodeSupport.Yes : DecodeSupport.No, true,
                    ok ? $"encode+decode round-trip ok ({bytes.Length} bytes, {decoder.Frames.Count} frame(s))"
                       : $"encode ok ({bytes.Length} bytes) but decode returned no usable frame"));
            }
            catch (Exception ex)
            {
                _capabilities.Add(new CodecCapability(format, DecodeSupport.No, true,
                    $"encode ok ({bytes.Length} bytes) but decode threw {ex.GetType().Name}: {Short(ex.Message)}"));
            }
        }
    }

    private void DeclareWebp() =>
        _capabilities.Add(new CodecCapability("WEBP", DecodeSupport.PerFile, false,
            "decode is probed per file: WIC resolves WebP through the installed system codec when one exists " +
            "(measured available on this machine), so it cannot be declared false at startup; " +
            "encode needs libwebp (BSD), which is not bundled yet -- claiming it would be a silent failure (spec 7.2)"));

    private void DeclareHeic() =>
        _capabilities.Add(new CodecCapability("HEIC", DecodeSupport.PerFile, false,
            "decode is probed per file (spec 6.3): it depends on the system HEIF image extension, " +
            "so the answer is per file rather than per machine; encode is deliberately not provided " +
            "(no HEVC codec is shipped)"));

    /// <summary>
    /// 工具 `merge-pdf` 加入的第三個類別（2026-10-07，見 <see cref="PdfCategory"/>）。
    ///
    /// **解碼永遠是 <c>false</c>，而且這不是「還沒做」而是「刻意不做」**：
    /// PDF 不是點陣圖，把它解成像素是另一件事（要一個 rasterizer），本工具也**不需要**它
    /// ——合併是整頁物件搬移，全程在 PDF 的物件模型上做（見 `ConversionEngine.MergePdf`）。
    /// 把它宣告成可解碼會讓它出現在第一層的來源格式裡、讓 `--convert` 對它放行，
    /// 而那一條路上沒有任何東西做得動它＝§7.2 的靜默失敗。
    ///
    /// ⚠ 這裡**不呼叫** <see cref="ProbeDecode"/>（那條路是 WIC 的，而 WIC 沒有 PDF 解碼器），
    /// 也不探測「PDFsharp 在不在」——它是一個 <c>PackageReference</c>，在就是載得起來的東西；
    /// 探測一個編譯期相依會讓這一列在**建置成功**的前提下仍然可能說謊。
    ///
    /// **2026-10-14：編碼不再是 <c>false</c>，而是量出來的。** 切片「圖片 → 單頁 PDF」讓 PDF
    /// 成為第一層的一個**輸出**格式（一張圖 → 一頁 PDF），生產者是 <see cref="PdfFromImage"/>
    /// ——不是 WIC，所以 <see cref="CreateEncoder"/> 仍然給不出它。<c>CanEncode</c> 由
    /// <see cref="ProbePdfEncoder"/> 決定（真的寫一份、再真的讀回來），失敗就不出現扇形（§7.2）。
    /// **這與「解碼」是兩件獨立的事**：PDF 仍然永遠不是來源格式。
    /// </summary>
    private void DeclarePdf()
    {
        (bool ok, string evidence) = ProbePdfEncoder();
        _capabilities.Add(new CodecCapability("PDF", DecodeSupport.No, ok,
            "decode is never provided on purpose: PDF is not a bitmap format and rasterising one is a " +
            "different job (the merge tool copies whole page objects, it does not rasterise). " +
            "encode is probed: " + evidence));
    }

    /// <summary>
    /// PDF **輸出**的探測（2026-10-14）：真的寫一份 PDF，再真的用 PDFsharp 讀回來。
    ///
    /// 形狀與 <see cref="ProbeWicEncoders"/> 相同（那一條是「真的編碼一次、再真的解碼一次」），
    /// 而且理由也相同：**做不到就不出現扇形**（規格 §7.2 禁止靜默失敗），而「做不到」這件事只有
    /// 量過才知道。這裡量三件事：**頁數要是 1**、**頁面尺寸要是那張 2×2 圖算出來的點數**
    /// （<see cref="PdfFromImage.PointsFromPixels"/>，96 dpi ⇒ 1.5 pt）、而且**真的讀得回來**。
    ///
    /// ⚠ 這一條**不是**在驗「PDFsharp 在不在」——它是編譯期相依，在就是載得起來。它驗的是
    /// 「這一條路在這個執行環境裡真的走得完」，而那是執行期的事實。
    /// </summary>
    private static (bool Ok, string Evidence) ProbePdfEncoder()
    {
        try
        {
            using var buffer = new MemoryStream();
            PdfFromImage.Write(MakeProbeBitmap(), buffer);
            long bytes = buffer.Length;
            buffer.Position = 0;

            using PdfSharp.Pdf.PdfDocument read =
                PdfSharp.Pdf.IO.PdfReader.Open(buffer, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);

            if (read.PageCount != 1)
                return (false, $"wrote {bytes}B but read back {read.PageCount} page(s), expected 1");

            PdfSharp.Pdf.PdfPage page = read.Pages[0];
            double width = page.Width.Point;
            double height = page.Height.Point;
            double expectedWidth = PdfFromImage.PointsFromPixels(2, 96);
            double expectedHeight = PdfFromImage.PointsFromPixels(2, 96);
            if (Math.Abs(width - expectedWidth) > 0.01 || Math.Abs(height - expectedHeight) > 0.01)
            {
                return (false, $"page is {width:F2}x{height:F2} pt, expected " +
                               $"{expectedWidth:F2}x{expectedHeight:F2}");
            }

            return (true, $"a 2x2 image round-tripped as a {bytes}B one-page PDF of " +
                          $"{width:F2}x{height:F2} pt (written, then read back through PdfReader)");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {Short(ex.Message)}");
        }
    }

    private static BitmapSource MakeProbeBitmap()
    {
        const int size = 2;
        int stride = size * 4;
        var pixels = new byte[stride * size];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 0x20;   // B
            pixels[i + 1] = 0x80;   // G
            pixels[i + 2] = 0xE0;   // R
            pixels[i + 3] = 0xFF;   // A
        }
        BitmapSource source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();            // 跨執行緒安全：Freeze 之後沒有 Dispatcher 親和性
        return source;
    }

    private static string Short(string message) =>
        message.Length <= 90 ? message : message[..90] + "…";
}
