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
    };

    /// <summary>Windows 內建的 WIC 編碼器（經 WPF 包裝）。這五個是「應該要能用」的基準。</summary>
    private static readonly (string Format, Func<BitmapEncoder> Make)[] WicEncoders =
    {
        ("PNG", () => new PngBitmapEncoder()),
        ("JPG", () => new JpegBitmapEncoder { QualityLevel = 85 }),   // 規格 §6.2：品質 85
        ("BMP", () => new BmpBitmapEncoder()),
        ("TIF", () => new TiffBitmapEncoder()),
        ("GIF", () => new GifBitmapEncoder()),
    };

    private readonly List<CodecCapability> _capabilities = new();

    public CodecRegistry()
    {
        ProbeWicEncoders();
        DeclareWebp();
        DeclareHeic();
    }

    /// <summary>每個格式的實測結果，順序與格式表一致。</summary>
    public IReadOnlyList<CodecCapability> Capabilities => _capabilities;

    public IReadOnlyList<string> DecodableFormats =>
        _capabilities.Where(c => c.Decode != DecodeSupport.No).Select(c => c.Format).ToArray();

    public IReadOnlyList<string> EncodableFormats =>
        _capabilities.Where(c => c.CanEncode).Select(c => c.Format).ToArray();

    /// <summary>可作為輸入的所有格式名稱（含「要靠系統擴充才成立」的，例如 HEIC）。</summary>
    public IReadOnlyList<string> InputFormats => Table.Select(t => t.Format).ToArray();

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
        catch (UnauthorizedAccessException ex)
        {
            return new DecodeOutcome(DecodeStatus.Unreadable, format, $"access denied: {Short(ex.Message)}");
        }
        catch (FileFormatException ex)
        {
            return new DecodeOutcome(DecodeStatus.Corrupt, format,
                $"FileFormatException hresult=0x{ex.HResult:X8}: {Short(ex.Message)}" + Inner(ex));
        }
        catch (NotSupportedException ex)
        {
            // WIC 找不到可用解碼器時說的是「No imaging component suitable to complete this operation was found.」
            // **實測：這不是 COMException 0x88982F50**，所以照猜測寫會讓 HEIC 顯示錯誤的訊息。
            // 兩種完全不同的處置必須分開：這是 HEIC → 「需要 HEIF 影像擴充」；否則 → 「不支援的格式」。
            if (IsHeifFormat(format) && LooksLikeHeif(path, out string brand))
                return new DecodeOutcome(DecodeStatus.NeedsExtension, format,
                    $"no HEIF decoder available (container brand={brand}); {Short(ex.Message)}");
            return new DecodeOutcome(DecodeStatus.Unsupported, format, $"NotSupportedException: {Short(ex.Message)}");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            uint hr = (uint)ex.HResult;
            string brandNote = IsHeifFormat(format) && LooksLikeHeif(path, out string brand)
                ? $" heifBrand={brand}" : "";

            // WINCODEC_ERR_COMPONENTNOTFOUND = 0x88982F50：系統找不到這個格式的解碼器
            if (hr == 0x88982F50u)
            {
                return IsHeifFormat(format) && brandNote.Length > 0
                    ? new DecodeOutcome(DecodeStatus.NeedsExtension, format,
                        $"0x{hr:X8} WINCODEC_ERR_COMPONENTNOTFOUND{brandNote}: {Short(ex.Message)}")
                    : new DecodeOutcome(DecodeStatus.Unsupported, format,
                        $"0x{hr:X8} WINCODEC_ERR_COMPONENTNOTFOUND: {Short(ex.Message)}");
            }

            // 「檔案與相關的檔案格式規格不符」＝資料壞了，不是格式不支援。
            // 實測：品牌盒正確但影像資料被截斷的 HEIC → 0xC00D36BE。
            // §7.1 對這兩者的使用者訊息不同（「無法解碼」vs「不支援的格式」），不可以混。
            if (CorruptHresults.Contains(hr))
                return new DecodeOutcome(DecodeStatus.Corrupt, format,
                    $"0x{hr:X8} data does not match the format spec{brandNote}: {Short(ex.Message)}");

            return new DecodeOutcome(DecodeStatus.Unsupported, format,
                $"COMException hresult=0x{hr:X8}{brandNote}: {Short(ex.Message)}");
        }
        catch (Exception ex)
        {
            return new DecodeOutcome(DecodeStatus.Unsupported, format,
                $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {Short(ex.Message)}");
        }
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

        foreach ((string format, Func<BitmapEncoder> make) in WicEncoders)
        {
            byte[]? bytes = null;
            try
            {
                BitmapEncoder encoder = make();
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
