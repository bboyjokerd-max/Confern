using System.Collections.ObjectModel;
using System.Windows;                 // Int32Rect（裁切矩形用；WindowsBase）
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;       // 只有工具 remove-bg 會用到（InferenceSession，見 RemoveBgModel）
using PdfSharp.Pdf;                   // 只有工具 merge-pdf 會用到（PdfDocument／PdfPage）
using PdfSharp.Pdf.IO;                // PdfReader／PdfDocumentOpenMode（只有工具 merge-pdf）
using ZXing;                          // 只有工具 read-qr 會用到（BarcodeReaderGeneric／RGBLuminanceSource）

namespace Sidecar;

/// <summary>
/// 單一檔案的轉檔：解碼 → 編碼 → 原子寫入。
///
/// 這一層**只做轉檔**，不知道 UI、不知道批次、不知道進度（那些是 JobQueue／UI 的事）。
/// 三條硬規則：
/// ① 來源一律**唯讀**開啟（`FileShare.ReadWrite`），永不修改、永不刪除、永不搬移（規格 §5）；
/// ② 輸出永不覆寫（由 <see cref="AtomicWriter"/> 的 `overwrite: false` 保證）；
/// ③ 任何失敗都不留暫存檔（§7.2）。
/// </summary>
internal sealed class ConversionEngine
{
    private readonly CodecRegistry _codecs;
    private readonly Action<string> _log;

    public ConversionEngine(CodecRegistry codecs, Action<string> log)
    {
        _codecs = codecs;
        _log = log;
    }

    /// <param name="targetDirectory">
    /// 輸出資料夾（規格 §9.5）。null／空白＝來源檔旁邊，也就是**預設且逐位元不變**的行為。
    /// 指定的資料夾不存在時 <see cref="NamingPolicy"/> 會回退到來源旁邊，這裡負責說出來。
    /// </param>
    public ConversionResult Convert(string sourcePath, string targetFormat, int quality,
                                    TempJournal? journal = null, string? targetDirectory = null)
    {
        // 1) 格式判定與能力——唯一格式知識來源（UI 不得自己看副檔名）
        string? sourceFormat = _codecs.FormatOfPath(sourcePath);
        if (sourceFormat == null)
            return Fail(sourcePath, ConversionError.Unsupported, "extension is not in the format table");
        if (_codecs.Capability(sourceFormat) is { Decode: DecodeSupport.No })
            return Fail(sourcePath, ConversionError.Unsupported, $"no decoder for {sourceFormat}");
        if (!_codecs.OfferedOutputs.Contains(targetFormat, StringComparer.Ordinal))
            return Fail(sourcePath, ConversionError.TargetUnavailable,
                $"{targetFormat} is not an offered output (offered=[{string.Join(",", _codecs.OfferedOutputs)}])");

        // 2) 來源還在嗎（規格 §7.1：來源於轉檔中被刪除 → 明確錯誤）
        if (!AtomicWriter.Exists(sourcePath))
            return Fail(sourcePath, ConversionError.SourceMissing, "source disappeared before conversion");

        // 3) 先便宜地探一次（只看檔頭）：把「無法解碼／需要擴充／不支援」的判定與 --codecs 共用同一套
        DecodeOutcome probe = _codecs.ProbeDecode(sourcePath);
        if (probe.Status != DecodeStatus.Ok)
            return Fail(sourcePath, MapDecodeStatus(probe.Status), probe.Detail);

        // 4) 命名（永不覆寫、永不詢問）
        string? extension = _codecs.PrimaryExtension(targetFormat);
        if (extension == null) return Fail(sourcePath, ConversionError.TargetUnavailable, "no extension for target");

        string targetPath;
        var triedNames = new List<string>();
        try
        {
            targetPath = NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory, triedNames);
            // 指定的輸出資料夾不存在 → NamingPolicy 已經回退到來源旁邊。**要說出來**：
            // 靜默的回退會讓使用者以為設定生效了，然後找不到檔案（規格 §9.5）。
            if (!string.IsNullOrWhiteSpace(targetDirectory)
                && string.Equals(Path.GetDirectoryName(targetPath),
                                 Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
                                 StringComparison.OrdinalIgnoreCase))
            {
                _log($"output dir: '{targetDirectory}' unavailable -> fell back to the source folder");
            }
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.WriteDenied, $"{ex.GetType().Name}: {ex.Message}");
        }

        // 4b) 這個目標格式不是 WIC 生產的（目前只有 PDF）⇒ 走另一條生產者。
        //     為什麼分流放在這裡而不是更前面：上面四段（格式表、來源還在嗎、便宜探測、命名）
        //     對 PDF 這一條**一字不差地適用**——它一樣要有可解碼的圖片、一樣要永不覆寫的
        //     檔名（`NamingPolicy` 的 `(1)` 序號規則照用）。真正不同的是「誰把位元組做出來」。
        //     ⚠ 這一條**不得**流到下面那個 CreateEncoder 去（它對 PDF 回 null，會變成
        //     「做不到卻出現在扇形上」）。判定本身住在 CodecRegistry：那是格式知識。
        if (CodecRegistry.IsNonImageTarget(targetFormat))
            return ToPdf(sourcePath, targetPath, journal, targetDirectory, extension);

        // 5) 解碼一次，然後寫入（重試規則見 WriteWithCollisionRetry）
        BitmapSource frame;
        int frameCount;
        try
        {
            (frame, frameCount) = DecodeFirstFrame(sourcePath);
        }
        catch (Exception ex)
        {
            string format = _codecs.FormatOfPath(sourcePath) ?? "?";
            return Fail(sourcePath, MapDecodeStatus(CodecRegistry.ClassifyDecodeException(ex, format, sourcePath)),
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // 多頁 TIFF／動畫 GIF：MVP 只轉第一頁／第一格（規格 §6.1）——但要說出來，不能默默少東西
        if (frameCount > 1)
            _log($"note: {Path.GetFileName(sourcePath)} has {frameCount} frames; only the first is converted (spec 6.1)");

        // EXIF 方向：容器不遵循 tag 時，把方向烤進像素（見 ApplyOrientation 的說明）
        int orientation = ReadOrientation(frame);
        bool bakedOrientation = false;
        if (orientation >= 2 && !TargetHonoursExifOrientation(targetFormat))
        {
            frame = ApplyOrientation(frame, orientation);
            bakedOrientation = true;
            _log($"orientation: EXIF={orientation} baked into pixels for {targetFormat} " +
                 $"({frame.PixelWidth}x{frame.PixelHeight}); {targetFormat} viewers ignore the tag, " +
                 $"so leaving it unbaked would show the image sideways (measured 2026-10-01)");
        }
        else if (orientation >= 2)
        {
            _log($"orientation: EXIF={orientation} kept as metadata for {targetFormat} (its viewers honour the tag)");
        }

        long written = 0;
        var convertTimer = System.Diagnostics.Stopwatch.StartNew();
        ConversionResult done = WriteWithCollisionRetry(sourcePath, targetPath, journal, stream =>
        {
            EncodeWithMetadataFallback(frame, sourcePath, targetFormat, quality, stream, bakedOrientation);
            written = stream.Length;
        }, tried => NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory, tried.ToList()),
           out targetPath);
        convertTimer.Stop();
        if (!done.Ok) return Fail(sourcePath, done.Error, done.Detail);

        // 逐檔耗時要看得見：規格 §9.3 有「5 MB JPG→PNG < 800 ms」的預算，
        // 沒有這個數字就只能靠感覺，而感覺沒辦法發現退化。
        _log($"converted {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
             $"({sourceFormat}->{targetFormat}, {written}B, quality={quality}, " +
             $"{convertTimer.Elapsed.TotalMilliseconds:F0} ms)");
        return new ConversionResult(sourcePath, targetPath, ConversionError.None,
            $"{written}B in {convertTimer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}");
    }

    /// <summary>
    /// 工具：順時針 90 度（工具 id `rotate90`）。M3 的第一個工具
    /// （見 `docs/aegis/plans/2026-10-05-m3-tools-wheel.md`）。
    ///
    /// **刻意與 <see cref="Convert"/> 共用同一條路徑**：同樣的來源檢查、同樣的解碼探測、
    /// 同樣的 <see cref="NamingPolicy"/> 命名、同樣的 <see cref="AtomicWriter"/> 原子寫入
    /// （永不覆寫）。規格 §5 的寫入契約只有一個擁有者，工具不得自己另開一條。
    ///
    /// 與 Convert 的差別只有三點：
    ///   ①輸出格式＝**來源格式**（旋轉不換格式，所以不經過「這個格式能不能輸出」的判定）；
    ///   ②EXIF 方向**先**烤進像素、**再**轉 90°（順序顛倒會轉錯：EXIF=6 的圖本來就該是直的，
    ///     先轉就會變成轉兩次）；
    ///   ③品質固定 100。⚠ 這是**重編碼**：PNG／BMP／TIF 是無損容器，所以結果逐像素等於
    ///     「來源轉 90°」；**JPEG 會重新壓縮**，100 只是最接近無損的一檔，不是真正的無損旋轉
    ///     （真正的無損 JPEG 轉向需要另一條 WIC 路徑，見計畫 §1 的相依盤點）。
    /// </summary>
    public ConversionResult Rotate90(string sourcePath, TempJournal? journal = null,
                                     string? targetDirectory = null)
    {
        string? sourceFormat = _codecs.FormatOfPath(sourcePath);
        if (sourceFormat == null)
            return Fail(sourcePath, ConversionError.Unsupported, "extension is not in the format table");
        if (_codecs.Capability(sourceFormat) is { Decode: DecodeSupport.No })
            return Fail(sourcePath, ConversionError.Unsupported, $"no decoder for {sourceFormat}");
        if (!AtomicWriter.Exists(sourcePath))
            return Fail(sourcePath, ConversionError.SourceMissing, "source disappeared before rotation");

        DecodeOutcome probe = _codecs.ProbeDecode(sourcePath);
        if (probe.Status != DecodeStatus.Ok)
            return Fail(sourcePath, MapDecodeStatus(probe.Status), probe.Detail);

        string? extension = _codecs.PrimaryExtension(sourceFormat);
        if (extension == null)
            return Fail(sourcePath, ConversionError.TargetUnavailable, "no extension for source format");

        string targetPath;
        try
        {
            targetPath = NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory);
            // 指定的輸出資料夾不存在 → NamingPolicy 已經回退到來源旁邊。**要說出來**：
            // 靜默的回退會讓使用者以為設定生效了，然後找不到檔案（規格 §9.5）。
            if (!string.IsNullOrWhiteSpace(targetDirectory)
                && string.Equals(Path.GetDirectoryName(targetPath),
                                 Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
                                 StringComparison.OrdinalIgnoreCase))
            {
                _log($"output dir: '{targetDirectory}' unavailable -> fell back to the source folder");
            }
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.WriteDenied, $"{ex.GetType().Name}: {ex.Message}");
        }

        BitmapSource frame;
        int frameCount;
        try
        {
            (frame, frameCount) = DecodeFirstFrame(sourcePath);
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, MapDecodeStatus(CodecRegistry.ClassifyDecodeException(ex, sourceFormat, sourcePath)),
                $"{ex.GetType().Name}: {ex.Message}");
        }

        if (frameCount > 1)
            _log($"note: {Path.GetFileName(sourcePath)} has {frameCount} frames; only the first is rotated (spec 6.1)");

        int orientation = ReadOrientation(frame);
        if (orientation >= 2)
        {
            frame = ApplyOrientation(frame, orientation);
            _log($"rotate90: EXIF={orientation} baked into pixels BEFORE rotating " +
                 $"(the tag would otherwise rotate it a second time)");
        }

        int w0 = frame.PixelWidth, h0 = frame.PixelHeight;
        frame = ApplyTransform(frame, new RotateTransform(90));
        // ⚠ 這一行的格式是**驗收斷言的一部分**（T4：非方形圖的長寬必須對調）。改它要先改計畫。
        _log($"rotate90: {Path.GetFileName(sourcePath)} {w0}x{h0} -> {frame.PixelWidth}x{frame.PixelHeight} " +
             $"({sourceFormat}, quality=100, re-encode)");

        long written = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        ConversionResult done = WriteWithCollisionRetry(sourcePath, targetPath, journal, stream =>
        {
            // bakedOrientation: true → 方向已烤進像素，所以 EXIF tag 要被中和成 1，
            // 否則會遵循 tag 的檢視器會再轉一次（旋轉工具尤其致命）。
            EncodeWithMetadataFallback(frame, sourcePath, sourceFormat, 100, stream, bakedOrientation: true);
            written = stream.Length;
        }, tried => NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory, tried.ToList()),
           out targetPath);
        timer.Stop();
        if (!done.Ok) return Fail(sourcePath, done.Error, done.Detail);

        _log($"rotated {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
             $"({written}B, {timer.Elapsed.TotalMilliseconds:F0} ms)");
        return new ConversionResult(sourcePath, targetPath, ConversionError.None,
            $"{written}B in {timer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}");
    }

    /// <summary>
    /// 工具：去 metadata（工具 id `strip-metadata`，M3 的第二個工具；規格 §9.4）。
    ///
    /// **骨架與 <see cref="Rotate90"/> 逐項對齊**（格式檢查 → <see cref="AtomicWriter.Exists"/> →
    /// <see cref="CodecRegistry.ProbeDecode"/> → <see cref="NamingPolicy.ResolveOutputPath"/> →
    /// 處理 → <see cref="AtomicWriter.Write"/> ＋ 同一套 3 次重試）：規格 §5 的寫入契約只有一個擁有者，
    /// 工具不得自己另開一條。
    ///
    /// **「處理」那一步有兩條，走哪一條由容器決定**（政策＝
    /// <see cref="CodecRegistry.HasLosslessMetadataStrip"/>）：
    ///   ①**JPEG**：容器層移除（<see cref="JpegMetadataStrip"/>，熵編碼的掃描資料整段照抄）
    ///     ⇒ 解出來的像素與來源**逐位元組相同**；
    ///   ②**其他格式**：解碼 → 丟掉 metadata／ICC → 以**來源格式**重編碼（品質 100）。
    ///     PNG／BMP／TIF／GIF 是無損容器 ⇒ 逐像素相同；**WebP 有損 ⇒ 會重新編碼**，
    ///     這與 <see cref="Rotate90"/> 已明講的「不要寫成無損」是同一條裁定。
    ///
    /// ⚠ **唯一會動到像素的情況**：來源的 EXIF 方向 ≠ 1。方向 tag 一清掉，遵循 tag 的檢視器就沒東西
    /// 可以遵循，圖會躺下——所以那一種**必須**走重編碼並先把方向烤進像素。方向只有在不為 1 時才會
    /// 改變像素，而且它一定會寫進日誌，不會默默發生。
    /// </summary>
    public ConversionResult StripMetadata(string sourcePath, TempJournal? journal = null,
                                          string? targetDirectory = null)
    {
        string? sourceFormat = _codecs.FormatOfPath(sourcePath);
        if (sourceFormat == null)
            return Fail(sourcePath, ConversionError.Unsupported, "extension is not in the format table");
        if (_codecs.Capability(sourceFormat) is { Decode: DecodeSupport.No })
            return Fail(sourcePath, ConversionError.Unsupported, $"no decoder for {sourceFormat}");
        if (!AtomicWriter.Exists(sourcePath))
            return Fail(sourcePath, ConversionError.SourceMissing, "source disappeared before stripping");

        DecodeOutcome probe = _codecs.ProbeDecode(sourcePath);
        if (probe.Status != DecodeStatus.Ok)
            return Fail(sourcePath, MapDecodeStatus(probe.Status), probe.Detail);

        string? extension = _codecs.PrimaryExtension(sourceFormat);
        if (extension == null)
            return Fail(sourcePath, ConversionError.TargetUnavailable, "no extension for source format");

        string targetPath;
        try
        {
            targetPath = NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory);
            // 指定的輸出資料夾不存在 → NamingPolicy 已經回退到來源旁邊。**要說出來**：
            // 靜默的回退會讓使用者以為設定生效了，然後找不到檔案（規格 §9.5）。
            if (!string.IsNullOrWhiteSpace(targetDirectory)
                && string.Equals(Path.GetDirectoryName(targetPath),
                                 Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
                                 StringComparison.OrdinalIgnoreCase))
            {
                _log($"output dir: '{targetDirectory}' unavailable -> fell back to the source folder");
            }
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.WriteDenied, $"{ex.GetType().Name}: {ex.Message}");
        }

        // 方向只有在**不為 1** 時才影響選擇：方向＝1（或沒有 tag）可以走容器層，像素一位元組都不動；
        // 方向 ≠ 1 必須把方向烤進像素再丟 tag，否則圖會躺下（見方法說明）。
        int orientation = ReadOrientationOfFile(sourcePath);
        bool lossless = CodecRegistry.HasLosslessMetadataStrip(sourceFormat) && orientation < 2;
        if (CodecRegistry.HasLosslessMetadataStrip(sourceFormat) && orientation >= 2)
        {
            _log($"strip-metadata: EXIF orientation={orientation} -> re-encoding to bake it into pixels " +
                 $"before dropping the tag (the container path would leave the image sideways)");
        }

        long written = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        ConversionResult done = WriteWithCollisionRetry(sourcePath, targetPath, journal, stream =>
        {
            if (lossless)
            {
                JpegMetadataStrip.Result stripped = JpegMetadataStrip.Strip(sourcePath, stream);
                if (stripped.Parsed)
                {
                    _log($"strip-metadata: {Path.GetFileName(sourcePath)} container path " +
                         $"(jpeg) removed {stripped.RemovedSummary} ({stripped.RemovedBytes}B), " +
                         $"scan data copied verbatim");
                    if (stripped.RemovedSegments == 0)
                        _log($"strip-metadata: note: {Path.GetFileName(sourcePath)} carried no " +
                             "metadata segment at all; the output is the source, byte for byte");
                }
                else
                {
                    // 走不下去就退回重編碼——**但先說出來**。半成品留在輸出流裡的話，
                    // 使用者會拿到一個截斷的檔案，那比慢一點嚴重得多。
                    _log($"strip-metadata: container path failed ({stripped.Detail}) -> " +
                         "falling back to decode + re-encode");
                    stream.SetLength(0);
                    stream.Position = 0;
                    StripByReencode(sourcePath, sourceFormat, stream);
                }
            }
            else
            {
                StripByReencode(sourcePath, sourceFormat, stream);
            }
            written = stream.Length;
        }, tried => NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory, tried.ToList()),
           out targetPath, StripFailure);
        timer.Stop();
        if (!done.Ok) return Fail(sourcePath, done.Error, done.Detail);

        _log($"stripped {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
             $"({written}B, {(lossless ? "container" : "re-encode")}, {timer.Elapsed.TotalMilliseconds:F0} ms)");
        return new ConversionResult(sourcePath, targetPath, ConversionError.None,
            $"{written}B in {timer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}");

        // 備援路徑上的**解碼**失敗要沿用解碼的分類（規格 §7.1 的訊息不可以因為
        // 「這一條是備援」就降級成 Unknown）；其餘照寫入失敗分類。
        (ConversionError, string) StripFailure(Exception ex)
        {
            if (ex is StripDecodeFailure decodeFailure && decodeFailure.InnerException is { } inner)
            {
                return (MapDecodeStatus(CodecRegistry.ClassifyDecodeException(inner, sourceFormat, sourcePath)),
                        $"{inner.GetType().Name}: {inner.Message}");
            }
            return (ClassifyWriteFailure(ex), $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {ex.Message}");
        }
    }

    /// <summary>
    /// 工具：壓縮（工具 id `compress`，M3 的第三顆扇形；規格 §9.4）。
    /// **輸出格式＝來源格式**（壓縮不換格式），等級由 <see cref="CompressionPolicy"/> 決定，
    /// 而政策的唯一來源是設定頁（使用者 2026-10-06 裁定：不做成輪盤上的多顆扇形）。
    ///
    /// 骨架與 <see cref="Rotate90"/>／<see cref="StripMetadata"/> 逐項對齊
    /// （格式檢查 → <see cref="AtomicWriter.Exists"/> → <see cref="CodecRegistry.ProbeDecode"/> →
    /// <see cref="NamingPolicy.ResolveOutputPath"/> → 處理 → <see cref="AtomicWriter.Write"/> ＋
    /// 同一套 3 次重試）：規格 §5 的寫入契約只有一個擁有者，工具不得自己另開一條。
    ///
    /// 四個模式（<see cref="CompressionPolicy.Mode"/>）：
    ///   • <c>small</c>——固定 <see cref="CompressionPolicy.SmallQuality"/>；
    ///   • <c>standard</c>——<see cref="CompressionPolicy.StandardQualityFor"/>＝**既有的品質設定**
    ///     （＝這個工具出現之前的行為）；
    ///   • <c>high</c>——固定 <see cref="CompressionPolicy.HighQuality"/>；
    ///   • <c>target-size</c>——**反覆重編碼逼近上限**，見 <see cref="SearchByQuality"/>。
    ///
    /// ⚠ **沒有品質旋鈕的格式（PNG／BMP／TIF／GIF）**：它們是無損（或無損式）容器，WIC 的編碼器
    /// 沒有品質參數（<see cref="CodecRegistry.CreateEncoder"/>），所以小檔／標準／高畫質**在它們
    /// 身上產生位元組完全相同的檔案**——這件事會寫進日誌，不假裝三個模式有差別；
    /// 「壓縮到」在它們身上也沒有等價手段可以逼近，所以只做**一次無損重編碼**並誠實回報：
    /// 進得去上限就成功，進不去就回 <see cref="ConversionError.TargetTooSmall"/>（不留檔案）。
    /// </summary>
    public ConversionResult Compress(string sourcePath, CompressionPolicy policy,
                                     TempJournal? journal = null, string? targetDirectory = null)
    {
        string? sourceFormat = _codecs.FormatOfPath(sourcePath);
        if (sourceFormat == null)
            return Fail(sourcePath, ConversionError.Unsupported, "extension is not in the format table");
        if (_codecs.Capability(sourceFormat) is { Decode: DecodeSupport.No })
            return Fail(sourcePath, ConversionError.Unsupported, $"no decoder for {sourceFormat}");

        // ⚠ 這個工具**輸出＝來源格式**，所以「解碼得動」不等於「做得到」：WebP 在本機解得動、
        // 編不動（見 CodecRegistry.DeclareWebp）。不明講的話使用者會拿到 Unknown（「轉檔失敗」），
        // 而不是規格 §7.1 已經有的「此格式目前無法輸出」。
        if (_codecs.Capability(sourceFormat) is { CanEncode: false })
            return Fail(sourcePath, ConversionError.TargetUnavailable,
                $"no encoder for the source format {sourceFormat}; the compress tool writes back in the " +
                "source format, so it cannot run here (spec 7.1: say it, do not pretend)");

        if (!AtomicWriter.Exists(sourcePath))
            return Fail(sourcePath, ConversionError.SourceMissing, "source disappeared before compression");

        DecodeOutcome probe = _codecs.ProbeDecode(sourcePath);
        if (probe.Status != DecodeStatus.Ok)
            return Fail(sourcePath, MapDecodeStatus(probe.Status), probe.Detail);

        string? extension = _codecs.PrimaryExtension(sourceFormat);
        if (extension == null)
            return Fail(sourcePath, ConversionError.TargetUnavailable, "no extension for source format");

        string targetPath;
        try
        {
            targetPath = NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory);
            // 指定的輸出資料夾不存在 → NamingPolicy 已經回退到來源旁邊。**要說出來**：
            // 靜默的回退會讓使用者以為設定生效了，然後找不到檔案（規格 §9.5）。
            if (!string.IsNullOrWhiteSpace(targetDirectory)
                && string.Equals(Path.GetDirectoryName(targetPath),
                                 Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
                                 StringComparison.OrdinalIgnoreCase))
            {
                _log($"output dir: '{targetDirectory}' unavailable -> fell back to the source folder");
            }
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.WriteDenied, $"{ex.GetType().Name}: {ex.Message}");
        }

        BitmapSource frame;
        int frameCount;
        try
        {
            (frame, frameCount) = DecodeFirstFrame(sourcePath);
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, MapDecodeStatus(CodecRegistry.ClassifyDecodeException(ex, sourceFormat, sourcePath)),
                $"{ex.GetType().Name}: {ex.Message}");
        }

        if (frameCount > 1)
            _log($"note: {Path.GetFileName(sourcePath)} has {frameCount} frames; only the first is compressed (spec 6.1)");

        // EXIF 方向：與 Convert 同一條既有的裁定——JPG／TIFF 的檢視器會遵循 tag 所以原樣保留，
        // 其餘的檢視器不看 tag，必須把方向烤進像素再中和 tag（否則壓縮完的圖會躺下）。
        int orientation = ReadOrientation(frame);
        bool bakedOrientation = false;
        if (orientation >= 2 && !TargetHonoursExifOrientation(sourceFormat))
        {
            frame = ApplyOrientation(frame, orientation);
            bakedOrientation = true;
            _log($"orientation: EXIF={orientation} baked into pixels for {sourceFormat} " +
                 $"({frame.PixelWidth}x{frame.PixelHeight})");
        }

        long beforeBytes = new FileInfo(LongPath.Prepare(sourcePath)).Length;
        long target = policy.TargetBytes;
        bool canControlSize = CompressionPolicy.QualityControlsSize(sourceFormat);

        int quality;
        byte[]? precomputed = null;

        if (policy.Mode == CompressionPolicy.ModeTargetSize)
        {
            if (canControlSize)
            {
                SearchOutcome search = SearchByQuality(frame, sourcePath, sourceFormat, bakedOrientation, target);
                if (!search.Reachable)
                {
                    // 回報實話：連最低品質都超過上限時**不留檔案**。留一個「比較小的」檔案會讓
                    // 使用者以為「壓縮到 100 KB」成功了，而它其實是 210 KB。
                    _log($"compress: target-size unreachable for {Path.GetFileName(sourcePath)} " +
                         $"(even quality={CompressionPolicy.SmallQuality} is {search.SizeAtFloor}B > {target}B) " +
                         "-> refusing; no file is written (say it, do not pretend)");
                    return Fail(sourcePath, ConversionError.TargetTooSmall,
                        $"smallest encodable version is {search.SizeAtFloor}B at quality " +
                        $"{CompressionPolicy.SmallQuality}, above the {target}B limit");
                }
                quality = search.Quality;
                precomputed = search.Bytes;
            }
            else
            {
                // 沒有等價手段：只做一次無損重編碼，然後**拿實際量到的位元組數**跟上限比。
                _log($"compress: {sourceFormat} is a lossless container (no quality knob in the encoder) " +
                     $"=> the limit {target}B cannot be approached by re-encoding; measuring one lossless " +
                     "re-encode against it instead");
                quality = policy.StandardQualityFor(sourceFormat);
                precomputed = EncodeToBytes(frame, sourcePath, sourceFormat, quality, bakedOrientation);
                if (precomputed.LongLength > target)
                {
                    _log($"compress: target-size unreachable for {Path.GetFileName(sourcePath)} " +
                         $"({sourceFormat} lossless re-encode is {precomputed.LongLength}B > {target}B; " +
                         "there is no lower-quality setting to try) -> refusing; no file is written");
                    return Fail(sourcePath, ConversionError.TargetTooSmall,
                        $"a lossless {sourceFormat} re-encode is {precomputed.LongLength}B and this format " +
                        $"has no quality setting to lower, so the {target}B limit cannot be met");
                }
            }
        }
        else
        {
            quality = policy.Mode switch
            {
                CompressionPolicy.ModeSmall => CompressionPolicy.SmallQuality,
                CompressionPolicy.ModeHigh => CompressionPolicy.HighQuality,
                _ => policy.StandardQualityFor(sourceFormat)
            };
            if (!canControlSize)
            {
                // 誠實：這三個模式在這個格式上**沒有差別**。不講的話，「小檔」看起來像壞了。
                _log($"compress: note: {sourceFormat} is a lossless container, so its encoder has no " +
                     $"quality knob; mode={policy.Mode} and standard produce byte-identical output here");
            }
        }

        long written = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        ConversionResult done = WriteWithCollisionRetry(sourcePath, targetPath, journal, stream =>
        {
            if (precomputed == null)
            {
                EncodeWithMetadataFallback(frame, sourcePath, sourceFormat, quality, stream, bakedOrientation);
            }
            else
            {
                // 搜尋／量測階段編出來的位元組**原封不動**寫出：重新編一次會得到同樣的位元組
                // （編碼器是決定性的），但「量到的」與「寫出的」就不再是同一個東西了。
                stream.Write(precomputed, 0, precomputed.Length);
            }
            written = stream.Length;
        }, tried => NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory, tried.ToList()),
           out targetPath);
        timer.Stop();
        if (!done.Ok) return Fail(sourcePath, done.Error, done.Detail);

        _log($"compressed {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
             $"({sourceFormat}, mode={policy.Mode}, quality={quality}, " +
             $"{beforeBytes}B -> {written}B" +
             $"{(target > 0 ? $", target={target}B" : "")}, {timer.Elapsed.TotalMilliseconds:F0} ms)");
        return new ConversionResult(sourcePath, targetPath, ConversionError.None,
            $"{beforeBytes}B -> {written}B in {timer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}");
    }

    /// <summary>
    /// 工具：裁切（工具 id `crop`，M3 的第四顆扇形；規格 §9.4）。
    ///
    /// **輸出格式＝來源格式**（裁切不換格式），骨架與 <see cref="Compress"/>／
    /// <see cref="StripMetadata"/> 逐項對齊（格式檢查 → 「編不編得動」→ <see cref="AtomicWriter.Exists"/>
    /// → <see cref="CodecRegistry.ProbeDecode"/> → <see cref="NamingPolicy.ResolveOutputPath"/>
    /// → 處理 → <see cref="AtomicWriter.Write"/> ＋ 同一套 3 次重試）。
    /// 規格 §5 的寫入契約只有一個擁有者，工具不得自己另開一條。
    ///
    /// **唯一的輸入是一個正規化的矩形**（<see cref="CropSpec"/>），換算成這一張圖的像素矩形
    /// 全部交給 <see cref="CropMath.TryToPixels"/>——**引擎不自己算**。那是「批次＝同一比例套用到
    /// 每一張」的唯一實作處，若在這裡再算一次，批次語意就會有兩份而其中一份會過期。
    ///
    /// 三種退化輸入的處置（都不丟例外）：
    ///   • 矩形為空 → <see cref="ConversionError.CropRectEmpty"/>（不寫檔）；
    ///   • 矩形等於全圖 → <see cref="ConversionError.CropNothingToDo"/>（不寫檔，理由見該成員）；
    ///   • 不到 1 px → 夾成 1 px 並把夾制寫進日誌（<see cref="CropMath"/> 會回報）。
    ///
    /// ⚠ **EXIF 方向**：裁切的矩形是使用者在**視窗看到的那張圖**上框出來的，而視窗顯示的是
    /// 「方向套用之後」的畫面（與本產品其他地方的裁定一致：WIC 的解碼器不會自動轉正）。
    /// 所以方向 ≥ 2 時**必須先烤進像素再裁**，否則矩形會落在錯的地方；而且烤完必須把 tag
    /// 中和成 1，否則會遵循 tag 的檢視器會把結果再轉一次。這一條對 JPG／TIFF 也一樣
    /// （與 <see cref="Convert"/> 的「JPG／TIFF 保留 tag」不同）——差別的理由是：
    /// 裁切**一定會動到像素**，保留一個已經與像素不符的方向 tag 只會讓結果顯示錯。
    /// 方向＝1（絕大多數）時這一段完全不執行，行為與其他工具逐位元相同。
    ///
    /// 品質固定 100：與 <see cref="Rotate90"/> 同一個值、同一個理由——PNG／BMP／TIF 是無損
    /// 容器，結果逐像素等於「來源的該矩形」；**JPEG 會重新壓縮**，100 只是最接近無損的一檔。
    /// </summary>
    public ConversionResult Crop(string sourcePath, CropSpec spec, TempJournal? journal = null,
                                 string? targetDirectory = null)
    {
        string? sourceFormat = _codecs.FormatOfPath(sourcePath);
        if (sourceFormat == null)
            return Fail(sourcePath, ConversionError.Unsupported, "extension is not in the format table");
        if (_codecs.Capability(sourceFormat) is { Decode: DecodeSupport.No })
            return Fail(sourcePath, ConversionError.Unsupported, $"no decoder for {sourceFormat}");
        // 與 Compress 同一條理由：輸出＝來源格式，所以「解碼得動」不等於「做得到」。
        if (_codecs.Capability(sourceFormat) is { CanEncode: false })
            return Fail(sourcePath, ConversionError.TargetUnavailable,
                $"no encoder for the source format {sourceFormat}; the crop tool writes back in the " +
                "source format, so it cannot run here (spec 7.1: say it, do not pretend)");
        if (!AtomicWriter.Exists(sourcePath))
            return Fail(sourcePath, ConversionError.SourceMissing, "source disappeared before cropping");

        DecodeOutcome probe = _codecs.ProbeDecode(sourcePath);
        if (probe.Status != DecodeStatus.Ok)
            return Fail(sourcePath, MapDecodeStatus(probe.Status), probe.Detail);

        string? extension = _codecs.PrimaryExtension(sourceFormat);
        if (extension == null)
            return Fail(sourcePath, ConversionError.TargetUnavailable, "no extension for source format");

        string targetPath;
        try
        {
            targetPath = NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory);
            // 指定的輸出資料夾不存在 → NamingPolicy 已經回退到來源旁邊。**要說出來**。
            if (!string.IsNullOrWhiteSpace(targetDirectory)
                && string.Equals(Path.GetDirectoryName(targetPath),
                                 Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
                                 StringComparison.OrdinalIgnoreCase))
            {
                _log($"output dir: '{targetDirectory}' unavailable -> fell back to the source folder");
            }
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.WriteDenied, $"{ex.GetType().Name}: {ex.Message}");
        }

        BitmapSource frame;
        int frameCount;
        try
        {
            (frame, frameCount) = DecodeFirstFrame(sourcePath);
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, MapDecodeStatus(CodecRegistry.ClassifyDecodeException(ex, sourceFormat, sourcePath)),
                $"{ex.GetType().Name}: {ex.Message}");
        }

        if (frameCount > 1)
            _log($"note: {Path.GetFileName(sourcePath)} has {frameCount} frames; only the first is cropped (spec 6.1)");

        // 方向先烤進像素，**再**裁（順序不能顛倒：矩形是在轉正後的畫面上框的）。
        int orientation = ReadOrientation(frame);
        bool bakedOrientation = false;
        if (orientation >= 2)
        {
            frame = ApplyOrientation(frame, orientation);
            bakedOrientation = true;
            _log($"crop: EXIF={orientation} baked into pixels BEFORE cropping " +
                 $"({frame.PixelWidth}x{frame.PixelHeight}) -- the rectangle was drawn on the upright " +
                 "picture, and the tag has to be neutralised afterwards or viewers rotate it again");
        }

        int inW = frame.PixelWidth, inH = frame.PixelHeight;
        CropOutcome outcome = CropMath.TryToPixels(spec, inW, inH, out Int32Rect rect, out List<string> notes);
        foreach (string note in notes) _log($"crop: {Path.GetFileName(sourcePath)}: {note}");

        if (outcome == CropOutcome.EmptyRect)
        {
            _log($"crop: refusing {Path.GetFileName(sourcePath)} (empty rectangle {spec.Describe()}); " +
                 "no file is written (say it, do not pretend)");
            return Fail(sourcePath, ConversionError.CropRectEmpty,
                $"the crop rectangle {spec.Describe()} is empty on a {inW}x{inH} image");
        }
        if (outcome == CropOutcome.FullFrame)
        {
            // 全圖＝什麼都沒做。留一個重壓過的檔案會讓使用者以為裁切成功了（見 CropNothingToDo）。
            _log($"crop: refusing {Path.GetFileName(sourcePath)} (the rectangle covers the whole " +
                 $"{inW}x{inH} image, so there is nothing to crop -- and a re-encode would be lossy " +
                 "for JPEG); no file is written");
            return Fail(sourcePath, ConversionError.CropNothingToDo,
                $"the crop rectangle covers the whole {inW}x{inH} image; a crop that changes nothing " +
                "is refused rather than re-encoded");
        }

        BitmapSource cropped;
        try
        {
            var bitmap = new CroppedBitmap(frame, rect);
            bitmap.Freeze();        // 凍結後才能跨執行緒交給編碼器（與 ApplyTransform 同一個理由）
            cropped = bitmap;
        }
        catch (Exception ex)
        {
            // 到這裡理論上到不了（矩形已經夾在畫格內），但「理論上」不是一種保證方式。
            return Fail(sourcePath, ConversionError.Unknown,
                $"CroppedBitmap({rect.X},{rect.Y},{rect.Width},{rect.Height}) on {inW}x{inH} failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        long written = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        ConversionResult done = WriteWithCollisionRetry(sourcePath, targetPath, journal, stream =>
        {
            EncodeWithMetadataFallback(cropped, sourcePath, sourceFormat, 100, stream, bakedOrientation);
            written = stream.Length;
        }, CollisionCrop(sourcePath, extension, targetDirectory), out targetPath);
        timer.Stop();
        if (!done.Ok) return Fail(sourcePath, done.Error, done.Detail);

        // ⚠ 這一行的格式是**驗收斷言的一部分**（輸入尺寸 → 輸出尺寸）。改它要先改計畫。
        _log($"crop: {Path.GetFileName(sourcePath)} {inW}x{inH} -> {cropped.PixelWidth}x{cropped.PixelHeight} " +
             $"(rect={rect.X},{rect.Y},{rect.Width},{rect.Height}, {spec.Describe()}, " +
             $"{sourceFormat}, quality=100, re-encode)");
        return new ConversionResult(sourcePath, targetPath, ConversionError.None,
            $"{inW}x{inH} -> {cropped.PixelWidth}x{cropped.PixelHeight} " +
            $"({written}B in {timer.Elapsed.TotalMilliseconds:F0} ms) -> {targetPath}");
    }

    /// <summary>
    /// 工具：去背（工具 id `remove-bg`，M3 的第七顆扇形；規格 §9.4）。
    ///
    /// **輸出格式永遠是 PNG**，而且與來源**同尺寸**：這個工具的產物是「原圖減掉背景」，
    /// 所以尺寸不可以變（變了就是另一個工具）。**一定要 PNG**——JPEG 沒有透明通道，
    /// 存成 JPEG 會把 alpha 直接丟掉，使用者拿到一張看起來沒去背的圖。
    ///
    /// **管線**（每一段都與一個已量測的事實對應）：
    /// <list type="number">
    /// <item>解碼第一張畫格（WIC），**把 EXIF 方向先烤進像素**——矩形／模型都要看正立的人像，
    /// 而且 PNG 的檢視器不讀方向標籤（與 <see cref="Crop"/> 同一條既有裁定）。</item>
    /// <item>模型：解析路徑 → 檢查狀態 → 取（快取的）session，見 <see cref="RemoveBgModel"/>。
    /// **三種狀態各有自己的錯誤碼**（缺檔／損壞／找不到主體），而且**都不寫任何檔案**。</item>
    /// <item>前處理：短邊縮到 512、尺寸取整到 32 的倍數、**不補成正方形**（保持長寬比）。
    /// ⚠ 這一條是量出來的裁定：直接拉伸成 512×512 的遮罩平均差 **0.26140**，而且人像被壓扁的
    /// 變形**無法事後補救**（見 <see cref="RemoveBgModel"/> 的常數註解）。</item>
    /// <item>推論（fp16，實測 72–115 ms／張；session 只建一次，139–190 ms）。</item>
    /// <item>遮罩 → Bgra32 灰階 → **用實際張量尺寸當來源**放大回原尺寸 → 取 R 當 alpha。
    /// ⚠ 放大**不可以**用理想比例回推：取整到 32 會讓張量長寬比與原圖微微不同
    /// （實測 3000×2250 → 672×512，理想值是 682.67×512），用原尺寸回推會讓遮罩整體位移。</item>
    /// <item><b>邊緣精修（<see cref="EdgeRefine"/>，預設開啟）</b>：放大回來的 alpha 只有
    /// 512 短邊那麼多的資訊（1920×1281 ⇒ 每個遮罩像素蓋住 2.5 個影像像素），所以用**原圖的亮度**
    /// 當引導做一次 guided filter，把高頻邊緣搬回 alpha。**只動過渡帶**：主體內仍是 1.0、
    /// 遠背景仍是 0.0（p 在視窗內是常數時 a=0、b=p）。形狀是「模型推論一次、之後純像素運算」——
    /// **不重跑模型**。<c>--remove-bg-refine off</c> 可以關掉它，關掉時 alpha 就是這一刀之前的位元組。</item>
    /// <item><b>顏色回填（2026-10-14 修，這一刀之前漏掉的一步）</b>：把**來源的 B／G／R** 放回輸出，
    /// 只留下遮罩來的 alpha（見 <see cref="RestoreSourceColours"/>）。少了這一步，輸出是一張
    /// 「遮罩＋alpha」——使用者一眼看出「這不是去背，是遮罩」。**不做 premultiply**。</item>
    /// <item>寫 **Bgra32 PNG**，走既有的命名（<see cref="NamingPolicy"/>，永不覆寫）與
    /// 原子寫入（<see cref="AtomicWriter"/>），失敗重試由 <see cref="WriteWithCollisionRetry"/> 擁有。</item>
    /// </list>
    ///
    /// <para>
    /// <b>「找不到人像主體」的判準（這一刀最重要的一條產品語意）</b>：
    /// 模型的輸出是**人像**遮罩，而它對非人像的圖會回一個近乎全空的遮罩。實測：
    /// <code>
    /// fixtures/qr/qr-small-in-scene-160.png  3000x2250  max=0.0165  mean=0.0001   &lt;- 非人像
    /// NASA Leland Melvin 與兩隻狗（512x410 縮圖）        max=1.0000  mean=0.5241   &lt;- 人像
    /// </code>
    /// 門檻取 <see cref="EmptyMaskMax"/>／<see cref="EmptyMaskMean"/>（0.30／0.02）：
    /// 兩個測資都離門檻很遠（非人像差 0.284／0.020，人像差 0.70／0.50），
    /// 所以這不是一個在臨界值上跳舞的判定。判準是**聯集**（任一條成立就算找不到主體）——
    /// 「整張幾乎全透明」與「只有零星幾個亮點」都是使用者拿不到主體的形狀。
    ///
    /// ⚠ **不做「全不透明」的第二種狀態**（規格 §9.4 的第三種失敗那一句的後半）：
    /// 遮罩全亮時的產物是一張**與原圖一樣的 PNG**，那是「我沒切到東西」而不是「我切錯了」，
    /// 所以它照樣輸出，而統計數字（min/max/mean）就在日誌那一行裡——使用者與驗收都看得到。
    /// 不讓它變成第三個錯誤碼的理由是**不要發明一個產品沒有做決定的狀態**：規格沒有說
    /// 「遮罩全亮時該拒絕還是輸出」，而拒絕一個內容正確的請求是更糟的預設。
    /// </para>
    ///
    /// <param name="modelPath">
    /// 模型檔路徑；null／空白＝預設位置（執行檔旁邊的 <c>models\model_fp16.onnx</c>）。
    /// 這個參數存在的唯一理由是**診斷要能指向一個不存在的檔與一個被截斷的檔**，
    /// 好讓「缺模型」與「模型損壞」這兩種狀態可以在驗收裡真的被走過。
    /// </param>
    /// <param name="refine">
    /// 邊緣精修的模式（見 <see cref="EdgeRefine.Modes"/>）。**null／空白／認不得＝
    /// <see cref="EdgeRefine.DefaultMode"/>（guided，開）**——「沒有這個欄位」的封包
    /// 與「不指定」的呼叫端都該得到預設行為。只有明確的 <c>off</c> 才會關掉它。
    /// </param>
    /// <returns>
    /// 成功＝<see cref="ConversionError.None"/> 且 <see cref="ConversionResult.OutputPath"/> 有值；
    /// 三種失敗各自帶自己的錯誤碼，而且**輸出目錄裡不會有任何新檔案**。
    /// </returns>
    public ConversionResult RemoveBg(string sourcePath, string? modelPath = null,
                                     TempJournal? journal = null, string? targetDirectory = null,
                                     string? refine = null)
    {
        string? sourceFormat = _codecs.FormatOfPath(sourcePath);
        if (sourceFormat == null)
            return Fail(sourcePath, ConversionError.Unsupported, "extension is not in the format table");
        if (_codecs.Capability(sourceFormat) is { Decode: DecodeSupport.No })
            return Fail(sourcePath, ConversionError.Unsupported, $"no decoder for {sourceFormat}");
        if (!AtomicWriter.Exists(sourcePath))
            return Fail(sourcePath, ConversionError.SourceMissing, "source disappeared before remove-bg");

        DecodeOutcome probe = _codecs.ProbeDecode(sourcePath);
        if (probe.Status != DecodeStatus.Ok)
            return Fail(sourcePath, MapDecodeStatus(probe.Status), probe.Detail);

        // 輸出的容器與副檔名都是固定的，而且**由格式表提供**（這裡不自己寫 ".png" 這個字面值：
        // 「PNG 的副檔名是什麼」是 CodecRegistry 的知識，第二份會走鐘）。
        string? extension = _codecs.PrimaryExtension("PNG");
        if (extension == null || CodecRegistry.CreateEncoder("PNG", 100) == null)
            return Fail(sourcePath, ConversionError.TargetUnavailable,
                "PNG is not an offered output in this build; remove-bg always writes PNG " +
                "(JPEG has no alpha channel, so it cannot be the output here)");

        // ---- 模型：三種狀態的第一種與第二種在這裡就結束（兩者都不寫檔）----
        string resolvedModel = RemoveBgModel.ResolvePath(modelPath);
        RemoveBgModel.ModelLoad load = RemoveBgModel.Check(resolvedModel, _log);
        // 同一顆模型在一批裡只說一次（session 已經快取，逐檔重講只是洗版）；
        // 但**換一顆模型一定要說**，否則「剛剛驗的是哪一顆」在日誌裡會說謊。
        if (!string.Equals(_lastModelReported, resolvedModel, StringComparison.OrdinalIgnoreCase)
            || load != RemoveBgModel.ModelLoad.Ready)
        {
            _lastModelReported = resolvedModel;
            _log($"remove-bg: model state={load} path='{resolvedModel}' " +
                 $"exists={AtomicWriter.Exists(resolvedModel)}");
        }
        if (load == RemoveBgModel.ModelLoad.Missing)
            return Fail(sourcePath, ConversionError.ModelUnavailable,
                $"no model file at '{resolvedModel}' (File.Exists=false); " +
                $"the file has to be fetched first -- tools\\get-rembg-model.ps1 puts it at " +
                $"{RemoveBgModel.DefaultFolderName}\\{RemoveBgModel.DefaultFileName} next to the executable");
        if (load != RemoveBgModel.ModelLoad.Ready)
            return Fail(sourcePath, ConversionError.ModelCorrupt,
                $"the model at '{resolvedModel}' exists but could not be loaded " +
                "(truncated download or not an ONNX file; measured: [ErrorCode:InvalidProtobuf] " +
                "Protobuf parsing failed)");

        string targetPath;
        try
        {
            targetPath = NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory);
            if (!string.IsNullOrWhiteSpace(targetDirectory)
                && string.Equals(Path.GetDirectoryName(targetPath),
                                 Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
                                 StringComparison.OrdinalIgnoreCase))
            {
                _log($"output dir: '{targetDirectory}' unavailable -> fell back to the source folder");
            }
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.WriteDenied, $"{ex.GetType().Name}: {ex.Message}");
        }

        BitmapSource frame;
        int frameCount;
        try
        {
            (frame, frameCount) = DecodeFirstFrame(sourcePath);
        }
        catch (Exception ex)
        {
            return Fail(sourcePath,
                MapDecodeStatus(CodecRegistry.ClassifyDecodeException(ex, sourceFormat, sourcePath)),
                $"{ex.GetType().Name}: {ex.Message}");
        }
        if (frameCount > 1)
            _log($"note: {Path.GetFileName(sourcePath)} has {frameCount} frames; only the first is " +
                 "cut out (the output is a single still PNG)");

        int orientation = ReadOrientation(frame);
        if (orientation >= 2)
        {
            frame = ApplyOrientation(frame, orientation);
            _log($"remove-bg: EXIF={orientation} baked into pixels BEFORE the model runs " +
                 $"({frame.PixelWidth}x{frame.PixelHeight}) -- PNG viewers do not read the tag, " +
                 "and the output PNG carries no metadata at all");
        }
        int inW = frame.PixelWidth, inH = frame.PixelHeight;

        // ---- 推論 ----
        InferenceSession session = RemoveBgModel.TryGetSession(resolvedModel, _log, out _)!;
        byte[] rgba;
        int outW, outH;
        // 張量尺寸要活到精修那一階段：放大倍率（＝瓶頸有多大）只能從它算（見 EdgeRefine.Apply）。
        int tensorW = 0, tensorH = 0;
        RemoveBgModel.MaskStats stats;
        string refineMode = EdgeRefine.CanonicalMode(refine);
        try
        {
            RemoveBgModel.MaskResult mask = RemoveBgModel.Infer(session, frame, _log);
            stats = RemoveBgModel.MaskStats.Of(mask.Mask);
            tensorW = mask.TensorWidth;
            tensorH = mask.TensorHeight;

            // ---- 第三種狀態：找不到人像主體（**絕不靜默輸出一張全透明的 PNG**）----
            if (stats.Max < EmptyMaskMax || stats.Mean < EmptyMaskMean)
            {
                _log($"remove-bg: {Path.GetFileName(sourcePath)} {inW}x{inH}: the mask is empty " +
                     $"(min={stats.Min:F4} max={stats.Max:F4} mean={stats.Mean:F4}, " +
                     $"thresholds max<{EmptyMaskMax} || mean<{EmptyMaskMean}) -> reporting " +
                     "NoSubjectFound and writing NOTHING (a fully transparent PNG would look like " +
                     "a broken tool, and the model is a PORTRAIT model -- spec 9.4)");
                return Fail(sourcePath, ConversionError.NoSubjectFound,
                    $"the portrait mask is empty (max={stats.Max:F4} mean={stats.Mean:F4}) -- " +
                    "MODNet is a portrait model, so a QR code / landscape / screenshot has no subject");
            }

            BitmapSource gray = RemoveBgModel.MaskToGrayFrame(mask.Mask, mask.TensorWidth, mask.TensorHeight);
            (rgba, outW, outH) = ExpandMaskToAlpha(gray, inW, inH);
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.Unknown,
                $"inference failed: {ex.GetType().Name}: {ex.Message}");
        }

        // ---- 邊緣精修（全解析度；模型已經跑完，這裡只有像素運算）----
        // 引導是**原圖的亮度**（不是輸出 PNG 的 R 通道：那裡裝的是遮罩值本身，用它當引導等於
        // 拿放大後的遮罩引導自己，什麼都修不回來）。
        try
        {
            double upscale = EdgeRefine.UpscaleFactor(outW, outH, tensorW, tensorH);
            if (!EdgeRefine.IsOn(refineMode))
            {
                _log($"remove-bg refine: {refineMode} -- the alpha is the raw upscaled mask " +
                     $"(the 512-edge bottleneck is NOT corrected; measured band " +
                     $"{BandPercent(rgba):F2}%; upscale x{upscale:F2})");
            }
            else if (!EdgeRefine.HasBottleneck(upscale))
            {
                // 遮罩幾乎沒有被放大（張量跟原圖差不多大）⇒ 這一顆工具要修的缺陷不夠明顯。
                // 實測：這時跑引導濾波換來的對齊，補不回它自己抹開的過渡帶（x0.80 的對照圖
                // 過渡帶 4.56% → 21.74%、梯度 18.56 → 4.56；x1.05 也是淨損失），
                // 所以**正確的動作是不動它**——輸出與 refine=off 逐位元組相同。
                _log($"remove-bg refine: {refineMode} -- no bottleneck worth fixing " +
                     $"(upscale x{upscale:F2} < {EdgeRefine.MinBottleneck:F1}: the mask was barely " +
                     $"enlarged, so the guided filter's own softening would cost more than the " +
                     $"alignment buys), the alpha stays exactly as the model returned it; " +
                     $"measured band {BandPercent(rgba):F2}%");
            }
            else
            {
                float[] guide = EdgeRefine.GuideLuminance(frame, out int gw, out int gh);
                if (gw != outW || gh != outH)
                {
                    // 到這裡理論上到不了（frame 的尺寸就是 inW／inH，而 outW／outH 被釘成同尺寸），
                    // 但「理論上」不是保證：對不上的引導會產生一張每個像素都錯位的遮罩。
                    throw new InvalidOperationException(
                        $"the guide is {gw}x{gh} but the alpha plane is {outW}x{outH}");
                }
                EdgeRefine.RefineStats refined =
                    EdgeRefine.Apply(rgba, outW, outH, guide, upscale);
                _log($"remove-bg refine: {refineMode} r={EdgeRefine.RadiusFor(upscale)} " +
                     $"eps={EdgeRefine.Epsilon:G} on {outW}x{outH}: " +
                     $"band {refined.BandBeforePercent:F2}% -> {refined.BandAfterPercent:F2}% " +
                     $"(upscale x{refined.Scale:F2}, transition sharpened x{refined.SharpenMean:F2} " +
                     $"on average, x{refined.Sharpen:F2} where the guide is flat) " +
                     $"in {refined.Milliseconds:F0} ms (guided filter, guide = source luminance; " +
                     "the model ran once -- this is pixels only)");
            }
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.Unknown,
                $"edge refinement failed: {ex.GetType().Name}: {ex.Message}");
        }

        // ---- 顏色回填：把來源的 B／G／R 放回去，只留下遮罩來的 alpha ----
        // WHY THIS STEP EXISTS (2026-10-14, 使用者回報): 到這裡為止 rgba 的 B／G／R 裝的是**遮罩自己的
        // 灰值**——MaskToGrayFrame 刻意把遮罩灌進四個通道（放大時取樣才會一致），ExpandMaskToAlpha
        // 只把 R 抄進 A，然後這份緩衝區就直接被編成輸出 PNG。於是使用者拿到的是一張
        // 「**遮罩＋alpha**」而不是去背成品：透明背景是對的、半透明毛髮也是對的，**錯的是主體的顏色**。
        // 他的原話是「去背不是應該把背景去掉留主體嗎？為何現在的 cutout 是做出一張 alpha 遮罩圖」，
        // 而他是對的。實測（1920x1281 狗毛照）：主體非透明像素的 B／G／R **100% 等於遮罩灰值**。
        // ⚠ 刻意**不做 premultiply**：PNG 存的是 straight alpha，先把顏色乘上 alpha 會讓半透明的
        // 毛髮合成到深色背景時出現黑邊。正確的成品＝顏色照抄來源、只換 alpha。
        int colourMismatched;
        try
        {
            colourMismatched = RestoreSourceColours(frame, rgba, outW, outH);
            _log($"remove-bg colour: source RGB restored over the matte " +
                 $"({colourMismatched} of {outW * outH} px were still showing the mask value in " +
                 "B/G/R when the matte was finished -- this counts what the step had to fix)");
        }
        catch (Exception ex)
        {
            return Fail(sourcePath, ConversionError.Unknown,
                $"colour restore failed: {ex.GetType().Name}: {ex.Message}");
        }

        long written = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        ConversionResult done = WriteWithCollisionRetry(sourcePath, targetPath, journal, stream =>
        {
            EncodeBgraPng(rgba, outW, outH, stream);
            written = stream.Length;
        }, CollisionCrop(sourcePath, extension, targetDirectory), out targetPath);
        timer.Stop();
        if (!done.Ok) return Fail(sourcePath, done.Error, done.Detail);

        // ⚠ 這一行的格式是**驗收斷言的一部分**（張量尺寸、遮罩統計、輸出尺寸）。
        // 統計數字必須印出來：它是「有沒有真的切到東西」唯一當場看得見的事實。
        _log($"remove-bg: {Path.GetFileName(sourcePath)} {inW}x{inH} -> {outW}x{outH} " +
             $"(mask min={stats.Min:F4} max={stats.Max:F4} mean={stats.Mean:F4}, png, " +
             $"colour from source, alpha from the matte) " +
             $"-> {Path.GetFileName(targetPath)} ({written}B in {timer.Elapsed.TotalMilliseconds:F0} ms)");
        return new ConversionResult(sourcePath, targetPath, ConversionError.None,
            $"{inW}x{inH} -> {outW}x{outH} (colour from the source, alpha from the mask, " +
            $"max={stats.Max:F4} mean={stats.Mean:F4}; " +
            $"{written}B in {timer.Elapsed.TotalMilliseconds:F0} ms) -> {targetPath}");
    }

    /// <summary>
    /// 「找不到人像主體」的門檻（見 <see cref="RemoveBg"/> 的判準說明）。
    /// 兩個都是**聯集**：任一條成立就拒絕。實測的兩個測資離這兩個數字都很遠。
    /// </summary>
    private const float EmptyMaskMax = 0.30f;

    /// <summary>見 <see cref="EmptyMaskMax"/>。（平均遮罩值）</summary>
    private const double EmptyMaskMean = 0.02;

    /// <summary>
    /// 這一輪已經在日誌裡報告過的模型路徑（同一顆模型只說一次；換一顆一定說）。
    /// 與 <c>_qrSourceName</c> 同一種作法：寫在引擎實例上，不進任何共享狀態。
    /// </summary>
    private string? _lastModelReported;

    /// <summary>
    /// alpha 的**過渡帶佔比**（％）：<c>0 &lt; alpha &lt; 255</c> 的像素佔整張圖的比例。
    /// 關掉精修那一條路要印一個**比得上**的數字——否則「關掉了」在日誌上只剩一句話，
    /// 看不出它與開著差在哪裡（feathering 的定義與量測方式見 <see cref="EdgeRefine"/>）。
    /// </summary>
    private static double BandPercent(byte[] bgra)
    {
        long band = 0;
        for (int p = 3; p < bgra.Length; p += 4)
        {
            byte a = bgra[p];
            if (a > 0 && a < 255) band++;
        }
        return bgra.Length == 0 ? 0 : 100.0 * band / (bgra.Length / 4);
    }

    /// <summary>
    /// 遮罩畫格 → **Bgra32 的 RGBA 位元組**：把遮罩放大回原尺寸，並把**灰階的 R 通道**
    /// 當成 alpha。
    ///
    /// ⚠ **放大一定要用遮罩自己的張量尺寸**當來源（<paramref name="gray"/> 的 PixelWidth／Height
    /// 就是它），**不可以**用「原圖尺寸 × 理想比例」回推——取整到 32 之後兩者不同
    /// （實測 3000×2250 → 張量 672×512，而理想值是 682.67×512），用理想值回推會讓整張遮罩位移。
    ///
    /// 輸出尺寸＝**來源畫格的尺寸**（不是拿縮放後的實際值）：這一顆工具的定義就是「同尺寸、
    /// 換掉背景」，所以尺寸是契約而不是計算結果；<c>TransformedBitmap</c> 的捨入必須與它一致
    /// （不一致就當場丟例外，而不是靜靜地交出一張尺寸不同的圖）。
    /// </summary>
    private static (byte[] Rgba, int Width, int Height) ExpandMaskToAlpha(
        BitmapSource gray, int sourceWidth, int sourceHeight)
    {
        BitmapSource up = gray;
        if (gray.PixelWidth != sourceWidth || gray.PixelHeight != sourceHeight)
        {
            var scaled = new TransformedBitmap(gray, new ScaleTransform(
                (double)sourceWidth / gray.PixelWidth, (double)sourceHeight / gray.PixelHeight));
            scaled.Freeze();
            if (scaled.PixelWidth != sourceWidth || scaled.PixelHeight != sourceHeight)
            {
                throw new InvalidOperationException(
                    $"the mask scaled to {scaled.PixelWidth}x{scaled.PixelHeight} but the output must be " +
                    $"{sourceWidth}x{sourceHeight} (the tool's contract is a same-size PNG)");
            }
            up = scaled;
        }

        BitmapSource bgra = up.Format == PixelFormats.Bgra32
            ? up
            : new FormatConvertedBitmap(up, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[(long)stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        // 取 R 當 alpha。灰階畫格的 B／G／R 是同一個值（見 RemoveBgModel.MaskToGrayFrame），
        // 所以取哪一個通道都一樣——**取 R** 只因為它與「遮罩值」在既有程式碼裡的說法一致。
        for (int p = 0; p + 3 < pixels.Length; p += 4) pixels[p + 3] = pixels[p + 2];
        return (pixels, bgra.PixelWidth, bgra.PixelHeight);
    }

    /// <summary>
    /// 把來源畫格的 **B／G／R 放回輸出**，只保留既有緩衝區裡的 alpha（＝遮罩）。
    ///
    /// **為什麼需要這一步（2026-10-14，使用者回報的缺陷）**：<see cref="ExpandMaskToAlpha"/> 交出來的
    /// 緩衝區，B／G／R **就是遮罩自己的灰值**（<see cref="RemoveBgModel.MaskToGrayFrame"/> 刻意把遮罩
    /// 灌進四個通道，好讓放大時的取樣一致），所以直接寫檔得到的是一張「**遮罩＋alpha**」——
    /// 使用者的說法是「去背不是應該把背景去掉留主體嗎？為何現在的 cutout 是做出一張 alpha 遮罩圖」，
    /// 而他是對的：透明背景正確、半透明毛髮正確，**錯的是主體的顏色**（等於一張白色剪影）。
    /// 實測（1920x1281 狗毛照）：主體的非全透明像素 B／G／R **100%** 等於遮罩灰值；
    /// 而這一刀之前，整套驗收都只在量 alpha 的統計，所以這個缺陷可以完全躲過所有斷言。
    ///
    /// ⚠ **逐像素對齊是契約，不是巧合**：<paramref name="frame"/> 是**模型看到的那一張畫格**
    /// （EXIF 方向已經烤進像素），而 alpha 平面已經被放大回同尺寸，所以兩者是同一格網——
    /// 不需要、也不可以做任何重取樣。對不上就當場丟例外，絕不交出一張錯位的圖。
    ///
    /// ⚠ **刻意不做 premultiply**：PNG 存的是 straight alpha，先把顏色乘上 alpha 會讓半透明的毛髮
    /// 合成到深色背景時出現黑邊（那正是這一顆工具最該保留的東西）。
    /// </summary>
    /// <returns>
    /// 回填**之前** B／G／R 就與來源不同的像素數——也就是這一步真的修掉了多少像素。
    /// 這個數字是**診斷**，不是斷言：它由本方法自己算，所以「有沒有把顏色放回去」必須由
    /// 套件用一份獨立的像素比對來斷言（見 <c>verify-m3d.ps1</c> S22.23）。
    /// </returns>
    private static int RestoreSourceColours(BitmapSource frame, byte[] bgra, int width, int height)
    {
        if (frame.PixelWidth != width || frame.PixelHeight != height)
        {
            throw new InvalidOperationException(
                $"the source frame is {frame.PixelWidth}x{frame.PixelHeight} but the matte is " +
                $"{width}x{height}; the colours cannot be put back pixel for pixel");
        }

        BitmapSource sourceFrame = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = sourceFrame.PixelWidth * 4;
        var source = new byte[(long)stride * sourceFrame.PixelHeight];
        sourceFrame.CopyPixels(source, stride, 0);
        if (source.Length != bgra.Length)
        {
            throw new InvalidOperationException(
                $"the source frame gave {source.Length} bytes but the matte has {bgra.Length}; " +
                "both planes are Bgra32 at the same size, so this cannot differ");
        }

        int mismatched = 0;
        for (int p = 0; p + 3 < bgra.Length; p += 4)
        {
            if (bgra[p] != source[p] || bgra[p + 1] != source[p + 1] || bgra[p + 2] != source[p + 2])
                mismatched++;
            bgra[p] = source[p];              // B
            bgra[p + 1] = source[p + 1];      // G
            bgra[p + 2] = source[p + 2];      // R
            // p + 3（alpha）**不動**：那是遮罩，這一顆工具的價值就在它。
        }
        return mismatched;
    }

    /// <summary>
    /// 寫出一個 Bgra32 的 PNG。
    ///
    /// ⚠ **刻意不走 <see cref="EncodeWithMetadataFallback"/>**：那一條會把來源的 metadata
    /// 搬過去（規格 §5 的原則），但這一顆工具**改了像素**——把 EXIF 方向帶進一張已經烤過方向的
    /// PNG，檢視器會**再轉一次**；而且 PNG 的 EXIF 支援本來就不一致（那正是 fallback 存在的理由）。
    /// 所以這裡明確「不夾帶 metadata」，並把這件事寫在日誌那一行（`png, alpha`）。
    /// 輸出格式固定 PNG，所以「品質」對它沒有意義（PNG 是無損容器）。
    /// </summary>
    private static void EncodeBgraPng(byte[] bgra, int width, int height, Stream output)
    {
        BitmapEncoder encoder = CodecRegistry.CreateEncoder("PNG", 100)
            ?? throw new NotSupportedException("no encoder for PNG");
        BitmapSource frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                                                 bgra, width * 4);
        frame.Freeze();
        encoder.Frames.Add(BitmapFrame.Create(frame, null, null, null));
        encoder.Save(output);
    }

    /// <summary>
    /// 工具：讀 QR（工具 id `read-qr`，M3 的第五顆扇形；規格 §9.4）。
    ///
    /// **這是唯一一個不產生檔案的引擎方法**：輸入是一張圖，產物是一段**文字**，
    /// 回在 <see cref="ConversionResult.QrText"/>（見那個參數的註解：新增欄位，不是新增通道）。
    /// 所以它**不碰** <see cref="NamingPolicy"/>、**不碰** <see cref="AtomicWriter"/>、
    /// 也**不碰** <see cref="TempJournal"/>（那個參數只是為了與其他工具同一個呼叫形狀，
    /// 這裡不會寫入任何位元組，所以沒有暫存檔要記）。來源一律唯讀開啟（規格 §5）——
    /// 這一條不是靠「記得不要寫」保證的，是**這個方法沒有任何寫入呼叫**。
    ///
    /// **解碼路線**：WIC（`BitmapImage`／`FormatConvertedBitmap` → `Bgra32` → `CopyPixels`）
    /// → `ZXing.RGBLuminanceSource` → `BarcodeReaderGeneric.Decode`。**不使用 System.Drawing**
    /// ——那個相依在本專案裡從來沒有被引用過（見 csproj 的註解），而 WIC 本來就是本產品的影像層。
    ///
    /// **兩段式策略**（ZXing.Net 核心**沒有**任何內建降採樣：`DecodingOptions` 沒有縮放選項，
    /// 所以「自己先縮圖」是唯一能讓大圖不慢的做法）：
    /// <list type="number">
    /// <item><b>第一輪：縮到約 <see cref="QrFirstPassPixels"/> px。</b>用
    /// <see cref="BitmapImage.DecodePixelWidth"/>——那是 **JPEG／PNG codec 的原生解碼尺寸**，
    /// 也就是「免費的降採樣」：整張 3000×2250 的圖不會先被展開成全尺寸再縮。
    /// 這一輪在實務上就是答案所在的那一輪（QR 的模組數 ≲ 177，1600 px 遠超過需要的取樣密度）。</item>
    /// <item><b>第二輪：第一輪失敗才用原尺寸再解一次。</b>原尺寸是**昂貴**的那一輪
    /// （整張點陣圖展開 ＋ ZXing 掃描），所以它必須是備援而不是預設。
    /// 兩輪都是同一個引擎方法，而且第二輪的失敗訊息會把兩輪都寫進日誌——
    /// 「試過幾次、每次多少像素、花多少毫秒」都要看得見，否則「解不出來」沒有可維修的資訊。</item>
    /// </list>
    ///
    /// ⚠ **縮圖是「解碼時縮」，不是「先縮再放大」**：小圖（例如 256×256）在第一輪就已經是原尺寸，
    /// 這時候第二輪會**自動跳過**（見 <see cref="QrReadRounds"/> 的 <c>RetryFullSize</c>）——
    /// 對同一張圖解兩次一樣的像素只是浪費時間，而且會讓日誌出現一輪假的「原尺寸重試」。
    ///
    /// <c>PossibleFormats = [QR_CODE]</c>、<c>TryHarder = true</c>；<c>AutoRotate</c> **保持關閉**
    /// （那是 `BarcodeReaderGeneric` 的預設值）。理由是成本：ZXing 的 <c>AutoRotate</c> 實作是
    /// 把整張圖再掃 90°／180°／270° 三個方向，等於**同一張圖解四次**；
    /// 而本產品的輸入是**螢幕截圖與圖片檔**，不是手機隨手拍的照片——截圖不會躺著。
    /// 若日後有實測顯示躺著的 QR 真的出現（例如使用者拿手機翻拍螢幕），
    /// 那時要開的是「四方向」這一整條路，並且必須附上量到的耗時與辨識率，不是現在先開著。
    ///
    /// **找不到 QR 時**回 <see cref="ConversionError.QrNotFound"/>：不丟例外、不產生檔案，
    /// 使用者訊息是「圖片中找不到 QR 碼」（見那個錯誤碼的註解：為什麼既有分類沒有一個合用）。
    /// 真正的解碼器例外（檔案壞掉、WIC 解不開）仍然走既有的解碼失敗分類，
    /// 因為那是**另一件事**：前者是「圖是好的、裡面沒有碼」，後者是「這張圖讀不出來」。
    ///
    /// 回傳碼語意（診斷入口 <c>--read-qr</c> 靠它分辨）：<see cref="ConversionError.None"/>＝解到文字，
    /// 其餘＝<see cref="ConversionError"/> 的分類。**沒有任何檔案被產生**。
    /// </summary>
    public ConversionResult ReadQr(string sourcePath, TempJournal? journal = null)
    {
        _ = journal;   // 這個工具不寫入任何位元組，所以沒有暫存檔要記（形狀一致，語意不變）

        string? sourceFormat = _codecs.FormatOfPath(sourcePath);
        if (sourceFormat == null)
            return Fail(sourcePath, ConversionError.Unsupported, "extension is not in the format table");
        if (_codecs.Capability(sourceFormat) is { Decode: DecodeSupport.No })
            return Fail(sourcePath, ConversionError.Unsupported, $"no decoder for {sourceFormat}");
        if (!AtomicWriter.Exists(sourcePath))
            return Fail(sourcePath, ConversionError.SourceMissing, "source disappeared before reading the QR");

        // 便宜的探測先做（只看檔頭）：壞檔要在這裡就被歸類成「無法解碼」，
        // 而不是等到 ZXing 找不到碼之後被誤報成「圖片中找不到 QR 碼」。
        DecodeOutcome probe = _codecs.ProbeDecode(sourcePath);
        if (probe.Status != DecodeStatus.Ok)
            return Fail(sourcePath, MapDecodeStatus(probe.Status), probe.Detail);

        _log($"read-qr: {Path.GetFileName(sourcePath)} ({sourceFormat}) " +
             $"first pass at <= {QrFirstPassPixels} px, full size only if that finds nothing");

        QrReadRounds rounds;
        try
        {
            rounds = DecodeQrRounds(sourcePath);
        }
        catch (Exception ex)
        {
            return Fail(sourcePath,
                MapDecodeStatus(CodecRegistry.ClassifyDecodeException(ex, sourceFormat, sourcePath)),
                $"{ex.GetType().Name}: {ex.Message}");
        }

        bool found = false;
        string? text = null;
        string foundIn = "";
        var total = System.Diagnostics.Stopwatch.StartNew();
        foreach (QrReadRound round in rounds.Rounds)
        {
            // 縮過的圖解不到才值得解原尺寸；對小圖而言「原尺寸」就是剛剛那一輪，
            // 再解一次同樣的像素只是浪費時間（而且會在日誌留下一次假的「原尺寸重試」）。
            if (round.Label == QrFullSizeLabel && !rounds.RetryFullSize)
            {
                _log($"read-qr: {Path.GetFileName(sourcePath)} skipped {QrFullSizeLabel} " +
                     "(the first pass was already the whole image)");
                break;
            }

            string? foundText;
            int w, h;
            QrReadRound current = round;   // 區域別名，供 catch 區塊使用（catch 不能捕捉 foreach 變數）
            try
            {
                BitmapSource frame = current.Frame();
                byte[] bgra = ToBgra32(frame, out w, out h);
                foundText = QrReaderFor().Decode(bgra, w, h, RGBLuminanceSource.BitmapFormat.BGRA32)?.Text;
            }
            catch (Exception ex)
            {
                // 解碼器自己的例外 = 「這張圖讀不出來」，不是「裡面沒有一碼」。
                // 分類沿用既有的解碼失敗，而且把輪次寫進去，否則維修的人只知道「失敗」。
                _log($"read-qr: {Path.GetFileName(sourcePath)} {current.Label} " +
                     $"threw {ex.GetType().Name}: {ex.Message}");
                return Fail(sourcePath,
                    MapDecodeStatus(CodecRegistry.ClassifyDecodeException(ex, sourceFormat, sourcePath)),
                    $"{current.Label}: {ex.GetType().Name}: {ex.Message}");
            }
            current.Timer.Stop();

            // ⚠ 這一行的格式是**驗收斷言的一部分**（哪一輪、多少像素、花了多久、有沒有解到）。
            _log($"read-qr: {current.Label} {w}x{h} in {current.Timer.Elapsed.TotalMilliseconds:F0} ms " +
                 $"-> {(foundText == null ? "(no qr)" : $"{foundText.Length} chars")} " +
                 $"({Path.GetFileName(sourcePath)})");
            if (foundText != null) { text = foundText; foundIn = current.Label; found = true; break; }
        }
        total.Stop();

        if (!found)
        {
            // **明確、可讀、不產生檔案的否定答案**（不是例外、不是崩潰）。
            _log($"read-qr: no QR found in {Path.GetFileName(sourcePath)} " +
                 $"({rounds.Rounds.Count} round(s): {rounds.Describe()}, " +
                 $"{total.Elapsed.TotalMilliseconds:F0} ms total); no file is written");
            return Fail(sourcePath, ConversionError.QrNotFound,
                $"no QR code found in a {rounds.FullWidth}x{rounds.FullHeight} {sourceFormat} image " +
                $"(rounds: {rounds.Describe()})");
        }

        _log($"read-qr: {Path.GetFileName(sourcePath)} decoded in the {foundIn}, " +
             $"{total.Elapsed.TotalMilliseconds:F0} ms total; NO file is written by this tool");
        // ⚠ OutputPath 一定是 null：這個工具不寫檔（見 ConversionResult.QrText 的互斥說明）。
        return new ConversionResult(sourcePath, null, ConversionError.None,
            $"{text!.Length} chars from the {foundIn} ({total.Elapsed.TotalMilliseconds:F0} ms)",
            text);
    }

    /// <summary>
    /// 一個 PDF 有幾頁（給排序視窗顯示「這一份會帶來幾頁」用）。
    ///
    /// **與 <see cref="MergePdf"/> 用同一個 API、同一個模式**（<c>PdfReader.Open</c> ＋
    /// <c>PdfDocumentOpenMode.Import</c>），所以視窗上顯示的頁數與合併時真正讀到的頁數是同一份
    /// 規則。刻意**不**在視窗裡自己 new 一個 <c>PdfDocument</c>：那會變成第二份「怎麼讀一個 PDF」
    /// 的實作，而兩份只要有一份改了模式（例如換成 Modify），視窗顯示的數字就會與實際合併的結果
    /// 不一致——而且那種不一致看起來完全合理，抓不到。
    ///
    /// ⚠ 它**只讀頁數，不碰任何一頁的內容**，所以 #388 那一類「開得起來、碰到頁面才炸」的檔在這裡
    ///   會回報一個看起來正常的頁數。這是刻意的：這一格是**顯示輔助**，權威仍然是合併本身
    ///   （失敗時由 fail-fast 指名是哪個檔、為什麼）。回傳 null ＝ 讀不出來（加密、壞檔、不存在），
    ///   呼叫端必須顯示「讀不到」而不是顯示 0——0 是一個具體的主張，而這裡並沒有主張。
    /// </summary>
    internal static int? TryReadPdfPageCount(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !AtomicWriter.Exists(path)) return null;
            using PdfDocument doc = PdfReader.Open(LongPath.Prepare(path), PdfDocumentOpenMode.Import);
            return doc.PageCount;
        }
        catch (Exception)
        {
            // 讀不到不是錯誤狀態，是「這一格沒有答案」；真正的原因由合併那一條路說出來。
            return null;
        }
    }

    /// <summary>
    /// 工具：合併 PDF（工具 id `merge-pdf`，M3 的第六顆扇形；規格 §9.4）。
    ///
    /// ⚠ **這是唯一一個「輸入是一組有序路徑、產物是一個檔」的引擎方法。** 前五顆都是
    /// 「一個檔進去、一個結果出來」（`read-qr` 的產物是文字，但仍然是單檔進、單結果出），
    /// 所以 worker 的逐檔迴圈對它們成立。合併不是：它是**一次工作、多個輸入、一個輸出**。
    /// 這一刀刻意**不動** worker 的逐檔迴圈，而是讓 `op=merge-pdf` 走**一次呼叫**
    /// （見 <see cref="WorkerServer"/>：`paths[0]` 觸發、其餘路徑整批傳進來），
    /// 理由是排序視窗（拖拉調整順序）是下一個切片——先把核心做成一個**原子操作**，
    /// 那個視窗之後只要換掉「送進來的順序」即可，不必再改這一層。
    ///
    /// **排序語意**：`sourcePaths` 的**陣列順序就是頁序**。這不是實作細節，是這個工具的定義
    /// ——`a.pdf;b.pdf` 與 `b.pdf;a.pdf` 是兩個不同的請求，而且必須產出兩個不同的檔。
    ///
    /// ## 失敗政策＝fail-fast（使用者 2026-10-07 裁定：「整批不產出，明確說出是哪個檔、為什麼」）
    ///
    /// ①②③ 都在**寫出之前**：整批先在**記憶體**裡合併成一份 `PdfDocument`，全部成功之後
    /// 才走**一次** <see cref="AtomicWriter"/>。所以「不產出半成品」是**結構上**成立的，
    /// 不是靠記得去清理暫存檔——中途任何失敗時，磁碟上根本還沒有這個輸出檔存在過
    /// （連 `.tmp-*` 都沒有，因為 <see cref="AtomicWriter"/> 還沒被呼叫）。
    ///
    /// 訊息**必定指名是哪一個檔、以及為什麼**（<see cref="ConversionResult.Position"/> ＝ 第幾個）：
    /// 只說「合併失敗」等於把使用者送去猜，而 fail-fast 讓他**一個檔都沒拿到**，猜的代價更大。
    ///
    /// ## 🔴 「開檔成功」不可以當成「合併得成」（PDFsharp Issue #388）
    ///
    /// 含有 `/XRefStm`（混合參照）的 PDF（**真實案例：Canva 產出的檔**）**開檔不會報錯**，
    /// 要等到 `AddPage` 才丟例外；Issue #388 在 Open、6.2.4 與 7.0.0-preview-1 都重現。
    /// 所以這裡**真的把每一頁加進暫存文件**，而且開檔與 AddPage **在同一個 try 裡**，
    /// 例外一律走 <see cref="ConversionError.MergeFailed"/>——這正是本專案最反對的那種
    /// 「靜默做錯事」的相反：**每一步都當場做，做不動就當場說**。
    ///
    /// ## 頁面尺寸**原樣保留**
    ///
    /// `PdfDocumentOpenMode.Import` ＋ `dst.AddPage(srcPage)` 是**整頁物件搬移**，
    /// 不是重新排版、也不是 fit-to-page：來源頁的 `/MediaBox`（因此寬高與原點）一個點都不會被動。
    /// ⚠ 這裡**刻意不用** `Modify` 模式——那個模式的頁面不能 `AddPage`（PDFsharp 的既有契約）。
    ///
    /// ## 加密 PDF
    ///
    /// 沒有密碼就讀不開（PDFsharp 丟 `PdfReaderException`，訊息含 "password"）。
    /// 本工具**沒有任何取得密碼的介面**（輪盤上沒有輸入框、協議上沒有欄位），所以它走 fail-fast
    /// 並**說出是加密／密碼問題**——而不是回一句看不出原因的「無法合併這個 PDF」。
    ///
    /// ## 誠實記明的既有套件限制（不必修，但不可以假裝不存在）
    ///
    ///   • xref stream **可讀，但輸出會被攤平成 top-level xref table** ⇒ 輸出檔**可能變大**；
    ///   • object stream **可讀，但不寫** ⇒ 輸出的物件是未壓縮的；
    ///   • `/Annots` **預設不 deep-copy** ⇒ 註解可能不會跟著走；
    ///   • **書籤（`/Outlines`）不會被保留**。
    ///
    /// ⚠ **`Save()` 之後那個 in-memory 文件會被鎖住**（2026-10-07 實測）：連讀 `PageCount`
    /// 都會丟 `InvalidOperationException: The document was already saved and cannot be modified
    /// anymore.`。所以下面**存檔之後不回頭讀任何屬性**；`--merge-pdf-inspect` 讀的是**已經寫出去
    /// 的那個檔**（重新開檔），不是這個物件。
    ///
    /// 回傳碼語意：<see cref="ConversionError.None"/>＝成功且**已經寫出**一個檔；
    /// 其餘＝整批放棄、**磁碟上沒有任何新檔**。
    /// </summary>
    /// <param name="sourcePaths">
    /// **依序**要合併的 PDF。順序＝頁序（見方法說明）。空清單或 null ＝ 沒有東西可以合併。
    /// </param>
    /// <param name="onProgress">
    /// 2026-10-16（四項優化②）：**這一輪已經處理完幾個檔**（`done`, `total`, 檔名）。
    ///
    /// **為什麼需要它**：合併是一顆「一次做完一整批」的工具，而兩百張圖要 ~24 秒。
    /// 在這之前，畫面與日誌在整段時間裡**完全沒有動靜**——使用者唯一能得到的結論是
    /// 「它當了」，於是再按一次（而第二次會撞上同一個輸出檔名）。
    ///
    /// ⚠ 這是一個**回呼**，不是這裡去碰管道或畫面：引擎不知道進度要送去哪裡（worker 的
    /// 管道、命令列、測試），那是呼叫端的決定。這一支只負責「誠實地說出走到第幾個檔」。
    /// `null`（預設）＝不報告，行為與這一刀之前**逐位元相同**。
    /// </param>
    public ConversionResult MergePdf(IReadOnlyList<string>? sourcePaths, TempJournal? journal = null,
                                     string? targetDirectory = null,
                                     PdfPageSize pageSize = PdfPageSize.Original,
                                     Action<int, int, string>? onProgress = null)
    {
        string[] paths = sourcePaths?.ToArray() ?? Array.Empty<string>();
        if (paths.Length == 0)
        {
            // 沒有第一個檔可以當「來源」放進結果，所以這裡的 sourcePath 是空字串。
            // 使用者訊息因此退回純原因（見 DescribeMessage：position <= 0 那條路），
            // 那是一句看得懂的話，而不是一個空檔名。
            _log("merge-pdf: no source file was given; nothing to merge, and no file is written");
            return new ConversionResult("", null, ConversionError.MergeFailed,
                "the request carried 0 source paths (merge-pdf needs at least one PDF)");
        }

        string first = paths[0];

        // ⓪ **這一批是什麼？**（2026-10-14：這一顆工具開始同時吃 PDF 與圖片）
        //
        // 判定知識**不在這裡**：`FormatCatalog.Classify` 是唯一擁有者，而它剛好回三種情形——
        //   Ready + CanConvert=false + PDF ⇒ 整批 PDF（走下面的整頁物件搬移，一行都沒變）
        //   Ready + CanConvert=true        ⇒ 整批圖片（走新的排版路徑）
        //   Mixed                          ⇒ 圖片與 PDF 混在一起，具名拒絕
        // ⚠ 為什麼混到必須拒絕，而不是「各做各的」：一份輸出不可能同時是「整頁物件複製」（PDF）
        //    與「重新排版」（圖片）。默默挑一邊做，就是安靜地做錯另一半——而使用者拿到的那個檔
        //    看起來完全正常。這一條與第一層的混合選取拒絕（規格 §4.3）是同一個道理。
        PayloadVerdict kind = new FormatCatalog(_codecs).Classify(paths);
        if (kind.Status == PayloadStatus.Mixed)
        {
            _log("merge-pdf: refusing the whole batch: it mixes images with PDFs (one output cannot be " +
                 "both a page-object copy and a re-layout); fail-fast -> NO file is written");
            return MergeFail(first, 1, ConversionError.Unsupported,
                "the batch mixes images with PDFs; merging PDFs copies whole pages and merging images " +
                "re-lays them out, so one output file cannot honestly be both");
        }
        if (kind.Status == PayloadStatus.Ready && kind.CanConvert)
            return MergeImagesToPdf(paths, journal, targetDirectory, pageSize, onProgress);

        // ① 先逐檔做**便宜的**檢查（存在嗎、是不是本產品認得的格式）：不必開檔就能說出原因。
        //    這一步刻意與其他工具的第一段同形（它們也是先看 AtomicWriter.Exists 與格式表）。
        foreach (string path in paths)
        {
            // ⚠ **`FormatOfPath` 回的是「格式名」，不是副檔名。** 格式表裡那一列是
            //    `("PDF", new[] { "pdf" }, PdfCategory)`：**左邊的 `"PDF"` 才是這個方法的回傳值**
            //    （右邊的 `"pdf"` 只是它接受的副檔名）。2026-10-07 這一刀第一次跑 `--merge-pdf`
            //    就是死在這裡——寫成 `is not "pdf"` 會讓**每一份合法的 PDF** 都被判成
            //    「不支援的格式」（實測症狀：`worker result: ok=False error=Unsupported
            //    message=不支援的格式`，而檔本身完全正常）。
            //    這一條之所以值得寫下來：**兩個字串長得像、而且只差大小寫**，所以它讀起來
            //    像對的；而症狀（一次都不成功）又很容易被誤讀成「PDFsharp 沒裝好」。
            //    型別相同、名字相近、失敗訊息指向別的地方——這是會再犯的那一種坑。
            if (_codecs.FormatOfPath(path) is not "PDF")
            {
                string given = _codecs.FormatOfPath(path) ?? "(unknown)";
                _log($"merge-pdf: refusing {Path.GetFileName(path)}: extension is not a PDF " +
                     $"(the format table says '{given}'); fail-fast -> NO file is written");
                return MergeFail(path, PositionOf(paths, path), ConversionError.Unsupported,
                    $"extension is not in the format table as PDF (it resolved to '{given}')");
            }
            if (!AtomicWriter.Exists(path))
            {
                _log($"merge-pdf: refusing {Path.GetFileName(path)}: it disappeared before the merge; " +
                     "fail-fast -> NO file is written");
                return MergeFail(path, PositionOf(paths, path), ConversionError.SourceMissing,
                    "source disappeared before the merge");
            }
        }

        // ② 整批在**記憶體**裡合併。這裡是唯一會碰到 PDF 內容的地方，也是 fail-fast 的判準所在。
        //
        // ⚠ `current` 與 `stage` 是**歸因用的**：例外一定要說得出「第幾個檔、哪一步」，
        //    而「猜哪一個檔」是最容易寫錯又最看不出來的一件事（一個指錯檔的訊息看起來
        //    仍然像一句正常的失敗訊息）。所以它們由**迴圈自己**在每一步之前設定，
        //    不是事後從例外訊息回推。
        byte[] merged;
        int totalPages = 0;
        int current = 1;
        string stage = "open";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var dst = new PdfDocument();
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                int position = i + 1;
                current = position;
                stage = "PdfReader.Open (Import)";
                // ⚠ Import 模式是硬性的：Modify 模式的頁面**不能** AddPage（PDFsharp 的既有契約），
                //   而在這裡用 Modify 的症狀是「每個檔都失敗」而不是一個安靜的錯誤——
                //   但契約仍然要寫在程式碼裡，不能只靠症狀。
                using PdfDocument src = PdfReader.Open(LongPath.Prepare(path), PdfDocumentOpenMode.Import);
                int pages = src.PageCount;
                _log($"merge-pdf: {Path.GetFileName(path)} (第 {position} 個檔) opened (Import), " +
                     $"{pages} page(s)");
                if (pages == 0)
                {
                    // 「開得了、但一頁都沒有」是一個具名的失敗，不是成功。
                    // 靜默跳過它會讓使用者拿到一個少了整份文件的合併檔，而且回報成功。
                    return MergeFail(path, position, ConversionError.MergeFailed,
                        "the document opens but reports 0 pages, so there is nothing to import " +
                        "(a merge that silently skips it would drop a whole document)");
                }
                for (int p = 0; p < pages; p++)
                {
                    // 🔴 #388：含有 /XRefStm 的 PDF 到**這一行**才會炸。所以每一頁都真的加，
                    //    不可以先用 PageCount 或開檔成功來推論「這一批合得成」。
                    stage = $"AddPage (page {p + 1}/{pages})";
                    PdfPage page = src.Pages[p];
                    double w = page.Width.Point, h = page.Height.Point;
                    dst.AddPage(page);
                    totalPages++;
                    _log($"merge-pdf: {Path.GetFileName(path)} page {p + 1}/{pages} added " +
                         $"({w:0.###}x{h:0.###} pt)");
                }

                // ②：**這一個檔的每一頁都真的搬進去了**才回報（不是「開始處理它」）。
                //    回報「開始」會讓最後一個檔看起來永遠差一步，而它其實已經做完了。
                onProgress?.Invoke(position, paths.Length, Path.GetFileName(path));
            }

            // 整批都加完了才序列化。序列化失敗也還在記憶體裡 ⇒ 一樣是「一個檔都不寫」。
            stage = "PdfDocument.Save (in memory)";
            using var buffer = new MemoryStream();
            dst.Save(buffer, closeStream: false);
            merged = buffer.ToArray();
        }
        catch (Exception ex)
        {
            // catch (Exception) 而不是特定型別，這是**刻意的**：這個陷阱的例外型別不穩定。
            // 2026-10-07 實測：同樣是「開得起來、AddPage 才炸」的檔，壞在頁面樹的參照上時丟
            // NullReferenceException，而換一個壞法就丟 InvalidOperationException
            // ("Page has no MediaBox.")。寫成特定型別＝某些壞法會逃出這個 catch，
            // 而逃出去的意思是**整批的 fail-fast 承諾消失**（例外會往上冒，變成一個沒有指名檔案的
            // 當機）。使用者裁定的政策是「整批不產出，明確說出是哪個檔、為什麼」，
            // 所以這裡寧可接住全部，再由 Detail 說出真正的型別與訊息。
            (ConversionError code, string reason) = DescribeMergeException(ex);
            _log($"merge-pdf: FAILED on the {OrdinalOf(current)} file ({stage}): " +
                 $"{ex.GetType().Name}: {ex.Message} " +
                 "(PDFsharp Issue #388: a PDF with /XRefStm opens fine and only throws at AddPage) " +
                 "-> giving up the whole batch; NO file is written");
            return MergeFail(paths[current - 1], current, code, $"stage {stage}: {reason}");
        }
        timer.Stop();

        // ③ **全部成功之後**才命名並原子寫出。到這一步為止磁碟上沒有任何新檔。
        //
        // 輸出檔名照既有慣例：**沿用第一個來源檔的主檔名**（`NamingPolicy.ResolveOutputPath`
        // 的規則，副檔名換成 pdf）。選「第一個來源」而不是發明一個固定的名字（例如 `merged.pdf`）
        // 的理由：命名規則只有一個擁有者（NamingPolicy），而它取的是**來源檔的主檔名**——
        // 五顆既有工具全部如此，這裡跟進就沒有一行新的命名規則；而「永不覆寫」的檔名衝突
        // 自動序號（`fx-a (1).pdf`…）也直接沿用，不必再寫第二套。
        //
        // ⚠ **「N 個輸入只有 1 個輸出」這件不對稱是刻意的，而它必須在這裡寫明**：
        //    合併的輸入是一批、輸出是一個，所以「沿用誰的主檔名」是一個**只有合併會遇到**的問題
        //    （其餘五顆是一進一出，那個問題不存在）。取 `paths[0]`＝**使用者排序後的第一個檔**
        //    ——也就是他在那一疊紙上看到的最上面那一張，所以 `fx-a.pdf` 合併三份之後變成
        //    `fx-a (1).pdf` 是**預期**的結果，不是撞名意外：`(1)` 來自既有的「永不覆寫」
        //    規則（`fx-a.pdf` 這個名字已經被來源自己佔著），而**來源因此絕對不會被覆蓋**
        //    ——這正是那一條紅線（規格 §5）在這裡的具體形狀。
        //    換句話說：`(1)` 的存在本身就是「沒有覆寫來源」的可觀測證據。
        string? extension = _codecs.PrimaryExtension("pdf");
        if (extension == null)
        {
            _log("merge-pdf: the format table has no extension for pdf -> refusing; no file is written");
            return MergeFail(first, 1, ConversionError.TargetUnavailable, "no extension for target pdf");
        }

        string targetPath;
        try
        {
            targetPath = NamingPolicy.ResolveOutputPath(first, extension, targetDirectory);
            if (!string.IsNullOrWhiteSpace(targetDirectory)
                && string.Equals(Path.GetDirectoryName(targetPath),
                                 Path.GetDirectoryName(Path.GetFullPath(first)),
                                 StringComparison.OrdinalIgnoreCase))
            {
                _log($"output dir: '{targetDirectory}' unavailable -> fell back to the source folder");
            }
        }
        catch (Exception ex)
        {
            return MergeFail(first, 1, ConversionError.WriteDenied, $"{ex.GetType().Name}: {ex.Message}");
        }

        ConversionResult done = WriteWithCollisionRetry(first, targetPath, journal,
            stream => stream.Write(merged, 0, merged.Length),
            tried => NamingPolicy.ResolveOutputPath(first, extension, targetDirectory, tried.ToList()),
            out targetPath);
        if (!done.Ok) return MergeFail(first, 1, done.Error, done.Detail);

        _log($"merge-pdf: merged {paths.Length} file(s) -> {Path.GetFileName(targetPath)} " +
             $"({totalPages} page(s), {merged.LongLength}B, " +
             $"{timer.Elapsed.TotalMilliseconds:F0} ms; sources untouched)");
        return new ConversionResult(first, targetPath, ConversionError.None,
            $"{paths.Length} file(s), {totalPages} page(s), {merged.LongLength}B in " +
            $"{timer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}", null, 1);
    }

    /// <summary>
    /// <summary>
    /// 一批**圖片** → **一份多頁** PDF（一頁一張，順序＝輸入順序）。
    ///
    /// 使用者 2026-10-14（原話）：「如果一次選多個圖檔，可以新增一個『合併成PDF』的功能嗎?」
    /// 入口是**擴大既有的 <see cref="MergePdf"/>**（他的裁定），所以這一支不新增扇形、不新增
    /// 工具 id：同一顆「合併 PDF」按下去，PDF 走整頁搬移、圖片走重新排版。
    ///
    /// ## 為什麼頁序不用問這裡
    /// 順序就是 `paths` 的順序，而它來自排序視窗確認的那一份（<c>MergeWindow.Order</c>）。
    /// 這一支**不得**自己排序（例如按檔名）：那會讓使用者在畫面上排的順序與產物不一致，
    /// 而那種錯只看檔名是看不出來的——頁數、尺寸全都對，只有內容的次序不對。
    ///
    /// ## 與 <see cref="ToPdf"/> 的關係
    /// 寫檔、命名、碰撞重試、失敗分類**全部沿用**（規格 §5 只有一個擁有者）：差別只在
    /// ①這裡是 N 張畫格寫進同一份文件、②頁面大小由 <paramref name="pageSize"/> 決定。
    /// </summary>
    private ConversionResult MergeImagesToPdf(string[] paths, TempJournal? journal,
                                               string? targetDirectory, PdfPageSize pageSize,
                                               Action<int, int, string>? onProgress = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();

        // ① 便宜的檢查先做（與 PDF 那條路同一條規矩）：不必解碼就能說出原因。
        for (int i = 0; i < paths.Length; i++)
        {
            if (!AtomicWriter.Exists(paths[i]))
            {
                _log($"merge-pdf: refusing {Path.GetFileName(paths[i])}: it disappeared before the merge; " +
                     "fail-fast -> NO file is written");
                return MergeFail(paths[i], i + 1, ConversionError.SourceMissing,
                    "source disappeared before the merge");
            }
        }

        // ② 逐檔解第一頁畫格。多頁 TIFF／動畫 GIF 只取第一格（規格 §6.1）——**這一條要留下痕跡**：
        //    否則「給了 5 頁的 TIFF、只拿到 1 頁」在日誌上完全看不出來，而它的產物看起來
        //    跟一張正常的圖一模一樣（頁數對、尺寸對、大小也對）。
        // ② 逐檔解第一頁畫格，**一次只有一張在記憶體裡**（2026-10-15：拖資料夾之後，批次動輒
        //    兩百張，先把每一張都解成 BitmapSource 收起來會吃到 GB 級）。
        //    多頁 TIFF／動畫 GIF 只取第一格（規格 §6.1）——**這一條要留下痕跡**：否則「給了 5 頁的
        //    TIFF、只拿到 1 頁」在日誌上完全看不出來，而它的產物看起來跟一張正常的圖一模一樣。
        // ⚠ 解不開的那張是在**寫入途中**才發現的（延遲序列），所以它不能像以前那樣當場 return：
        //    它丟一個帶著名分的例外出來，由下面那個 catch 分類。這樣契約不變——
        //    ①失敗仍然「具名 + 分類」（不是一句「PDF 寫入失敗」）②`WriteWithCollisionRetry`
        //    底下的 AtomicWriter **不會** commit，所以不會留下半成品。
        IEnumerable<BitmapSource> Frames()
        {
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                BitmapSource frame;
                int frameCount;
                try
                {
                    (frame, frameCount) = DecodeFirstFrame(path);
                }
                catch (Exception ex)
                {
                    throw new MergeFrameDecodeException(path, i + 1, ex);
                }

                if (frameCount > 1)
                    _log($"merge-pdf: {Path.GetFileName(path)} (第 {i + 1} 個檔) holds {frameCount} frame(s); " +
                         "only the FIRST one becomes a page (spec 6.1: multi-page TIFF / animated GIF)");
                yield return frame;

                // ②：**`yield return` 之後**才是「這一頁已經被寫進 PDF 了」——消費者是
                //    `PdfFromImage.WriteMany`，它把畫格拉走、寫成頁面、才回來要下一張。
                //    所以這一行就是誠實的頁進度，而不是「已經解碼了」。
                //    ⚠ 這也正是「一次只有一張在記憶體裡」那條不變式仍然成立的原因：
                //    回報完才回頭解下一張，沒有任何東西被留下來（S6 會量峰值）。
                onProgress?.Invoke(i + 1, paths.Length, Path.GetFileName(path));
            }
        }

        // ③ 排版與寫檔。輸出檔名沿用**第一個來源檔**（與 PDF 那條路一致：使用者看到的
        //    「合併結果」就是第一個檔的名字，而 `(1)` 序號本身是「來源沒被覆寫」的證據）。
        // ⚠ **不含點**：`NamingPolicy.ResolveOutputPath` 自己接上副檔名（單張那條路傳的是
        // `CodecRegistry` 給的 `"pdf"`）。第一版這裡寫了 `".pdf"`，產物於是叫
        // `AAA-first..pdf`——兩個點。那個名字本身還能開，所以它只會在使用者看到檔名時才被發現。
        string extension = "pdf";
        long written = 0;
        int pages = 0;
        string targetPath;
        ConversionResult done;

        // ③a 先把**第一次要用的檔名**算出來（與 PDF 那條路 1565 行一字不差的形狀）。
        //
        // 🔴 2026-10-16（②的現場）：這裡原本傳的是 `paths[0]`——也就是**來源檔自己的路徑**
        //    （`p0001.png`）。`WriteWithCollisionRetry` 的第一個嘗試因此是去寫那個**已經存在
        //    的來源檔**，必定回 `ERROR_ALREADY_EXISTS (0x800700B7)`，然後才換名重試。
        //    而 `writeContent` 是延遲序列：重試會**把整批重新解碼、重新排版一次**。
        //    實測（200 張圖）：第一次嘗試 11.7 秒全部白做，整批合併因此固定是 ~24 秒
        //    ——是應該的兩倍。日誌裡只看得到一行 `write retry`，而它讀起來像是「別人搶走了檔名」。
        //    這一條與 ① 同一個病：**症狀把責任指到別的地方**。
        try
        {
            targetPath = NamingPolicy.ResolveOutputPath(paths[0], extension, targetDirectory);
        }
        catch (Exception ex)
        {
            return MergeFail(paths[0], 1, ConversionError.WriteDenied,
                $"{ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            done = WriteWithCollisionRetry(paths[0], targetPath, journal,
                stream =>
                {
                    pages = PdfFromImage.WriteMany(Frames(), stream, pageSize);
                    written = stream.Length;
                },
                tried => NamingPolicy.ResolveOutputPath(paths[0], extension, targetDirectory, tried.ToList()),
                out string targetPath2);
            targetPath = targetPath2;
        }
        catch (MergeFrameDecodeException ex)
        {
            // 真正的原因（解碼失敗的那個例外）。型別上它一定有，但編譯器不知道，所以這裡收斂成
            // 一個非 null 的區域變數——不然就是一個 CS8604 警告，而本專案的建置門檻是 0 警告。
            Exception cause = ex.InnerException ?? ex;
            string format = _codecs.FormatOfPath(ex.Path) ?? "?";
            _log($"merge-pdf: refusing {Path.GetFileName(ex.Path)}: it cannot be decoded " +
                 $"({cause.GetType().Name}); fail-fast -> NO file is written");
            return MergeFail(ex.Path, ex.Ordinal,
                MapDecodeStatus(CodecRegistry.ClassifyDecodeException(cause, format, ex.Path)),
                $"{cause.GetType().Name}: {cause.Message}");
        }
        if (!done.Ok) return done;

        timer.Stop();
        _log($"merge-pdf: merged {paths.Length} image(s) -> {Path.GetFileName(targetPath)} " +
             $"({pages} page(s), {written}B, pageSize={pageSize}, " +
             $"{timer.Elapsed.TotalMilliseconds:F0} ms; sources untouched)");
        return new ConversionResult(paths[0], targetPath, ConversionError.None,
            $"{paths.Length} image(s), {pages} page(s), {written}B in " +
            $"{timer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}", null, 1);
    }

    /// <summary>
    /// 「這一批裡的某一個檔解不開」——帶著**是哪一個、第幾個**從延遲序列裡丟出來。
    /// 存在的理由：改成逐頁串流之後，解碼發生在寫入途中的深處，沒有辦法再像以前那樣當場
    /// <c>return MergeFail(...)</c>；但「失敗要具名 + 分類」這條契約不能因此鬆掉。
    /// </summary>
    private sealed class MergeFrameDecodeException : Exception
    {
        public MergeFrameDecodeException(string path, int ordinal, Exception inner)
            : base($"cannot decode {path}", inner) { Path = path; Ordinal = ordinal; }

        /// <summary>解不開的那個檔。</summary>
        public string Path { get; }

        /// <summary>它在這一整批裡是第幾個（1 起算）。</summary>
        public int Ordinal { get; }
    }

    /// 工具：**圖片 → 一頁 PDF**（切片「圖片 → 單頁 PDF」，2026-10-14）。
    ///
    /// 與 <see cref="MergePdf"/> 是同族的第二條「非 WIC 生產者」：兩者都不經過
    /// <see cref="CodecRegistry.CreateEncoder"/>（那一條的產物是 <c>BitmapEncoder</c>），
    /// 但**寫檔契約一字不差**——<see cref="AtomicWriter.Exists"/> →
    /// <see cref="CodecRegistry.ProbeDecode"/> → <see cref="NamingPolicy.ResolveOutputPath"/> →
    /// 生產 → <see cref="WriteWithCollisionRetry"/>（含三次重試與同一組失敗分類）。
    /// 規格 §5 只有一個擁有者，所以這裡**沒有**任何一句新的命名或寫檔規則。
    ///
    /// 與 <see cref="MergePdf"/> 的差別只有兩處，而且都是語意上的：①它吃**一個**檔案（一進一出，
    /// 與其餘五顆扇形相同），②它的來源必須**可解碼**（合併只讀 PDF 的物件模型，不需要像素；
    /// 這一條要把像素放進頁面，所以不可解碼的來源在上一關就被擋掉了）。
    /// </summary>
    private ConversionResult ToPdf(string sourcePath, string targetPath, TempJournal? journal,
                                   string? targetDirectory, string extension)
    {
        BitmapSource frame;
        try
        {
            (frame, _) = DecodeFirstFrame(sourcePath);
        }
        catch (Exception ex)
        {
            string format = _codecs.FormatOfPath(sourcePath) ?? "?";
            return Fail(sourcePath, MapDecodeStatus(CodecRegistry.ClassifyDecodeException(ex, format, sourcePath)),
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // 頁面尺寸＝來源的實際 DPI（見 PdfFromImage 的說明）。先在這裡算出來，因為日誌要寫它，
        // 而它是「這張圖印出來多大」的唯一答案。
        double width = PdfFromImage.PointsFromPixels(frame.PixelWidth, frame.DpiX);
        double height = PdfFromImage.PointsFromPixels(frame.PixelHeight, frame.DpiY);

        ConversionResult done = WriteWithCollisionRetry(sourcePath, targetPath, journal,
            stream => PdfFromImage.Write(frame, stream),
            tried => NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory, tried.ToList()),
            out targetPath);
        if (!done.Ok) return done;

        _log($"to-pdf: {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
             $"({frame.PixelWidth}x{frame.PixelHeight} px, {width:F2}x{height:F2} pt at " +
             $"{frame.DpiX:F0}x{frame.DpiY:F0} dpi; one page; source untouched)");
        return new ConversionResult(sourcePath, targetPath, ConversionError.None,
            $"one-page PDF {frame.PixelWidth}x{frame.PixelHeight} px -> {width:F2}x{height:F2} pt " +
            $"at {frame.DpiX:F0} dpi -> {targetPath}", null, 1);
    }

    /// <summary>一個檔案路徑在一批有序輸入裡的位置（1 起算）。同名的第二份檔仍然是合法輸入。</summary>
    private static int PositionOf(string[] paths, string path)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if (string.Equals(paths[i], path, StringComparison.Ordinal)) return i + 1;
        }
        return 0;
    }

    /// <summary>英文序數只在日誌裡用（那裡的既有文字全是英文）；使用者訊息用中文「第 N 個檔」。</summary>
    private static string OrdinalOf(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th"
    };

    /// <summary>
    /// 例外 → detail（**維修的人看得到的那一欄**；使用者訊息那一句是規格條文，見 MergeFailed）。
    ///
    /// 加密要**當場認出來**：PDFsharp 沒有給「這個檔加密了」的專用例外型別或欄位
    /// （`PdfReaderException` 只有三個建構子，沒有任何屬性），所以只能看訊息文字。
    /// 這是**字串比對**，所以它只影響 detail 那一句的**說法**，不影響分類，也不影響
    /// fail-fast 的決定——猜錯只會讓那句話少說「密碼」兩個字，不會讓行為改變。
    /// </summary>
    private static (ConversionError Code, string Reason) DescribeMergeException(Exception ex)
    {
        string type = ex.GetType().Name;
        string message = ex.Message;
        // ⚠ 這是**字串比對**，而且是刻意的（2026-10-07 實測：不給密碼是
        // "A password is required to open the PDF document."、給錯密碼是
        // "The specified password is invalid."——兩種都含 "password"；
        // PdfReaderException 沒有專門的屬性可以問，它只有三個建構子）。
        // 猜錯的代價只是「少說一句密碼」或「多說一句密碼」，**行為完全不變**
        // （一樣 fail-fast、一樣不寫任何檔），所以不需要更強的手段。
        bool passwordish = message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;
        return passwordish
            // **這一句是給使用者的**（ConversionError.MergeNeedsPassword）：加密檔的失敗原因
            // 決定了他的下一步（先解除密碼再來），所以它不能與「無法合併這個 PDF」共用
            // ——那句話讀完只會讓人重試同一個永遠不會成功的檔。
            // 訊息刻意不含「請輸入密碼」的暗示：本工具**沒有**取得密碼的介面（輪盤上沒有輸入框、
            // worker 協議上沒有欄位），說「請輸入密碼」等於把使用者指向一個不存在的功能。
            ? (ConversionError.MergeNeedsPassword,
               $"{type}: {message} -- the source is ENCRYPTED and this tool has no way to ask for a " +
               "password (there is no field for one in the wheel or in the worker protocol), so the " +
               "whole batch is given up rather than merged without it")
            : (ConversionError.MergeFailed, $"{type}: {message}");
    }

    /// <summary>
    /// 合併失敗的**唯一建構處**：位置 ＋ 檔名 ＋ 分類 ＋ detail。
    ///
    /// 為什麼要有它而不是各處自己 <c>new</c>：<see cref="ConversionResult.Position"/> 漏填的症狀
    /// 是「訊息少了『第幾個檔』」——而那句訊息正是使用者裁定要看的那一件事，缺了它看起來仍然
    /// 像一句正常的失敗訊息，不會有人發現。集中在這裡＝漏填不可能發生。
    /// </summary>
    private static ConversionResult MergeFail(string sourcePath, int position, ConversionError error,
                                              string detail) =>
        new(sourcePath, null, error, detail, null, position);

    /// <summary>
    /// 第一輪的縮圖上限（**寬或高較大的一邊**）。選 1600 而不是更大的理由：
    /// QR 的模組數上限是 177×177，加上 4 模組的安靜區＝185 模組；1600 px 給每個模組
    /// 8.6 px，遠高於 ZXing 需要的取樣密度，而掃描成本隨像素數走，所以再大只是變慢。
    /// 選更小的理由則相反：1200 px 在「碼只佔畫面一角」的照片上會讓模組掉到 3 px 以下。
    /// </summary>
    private const int QrFirstPassPixels = 1600;

    /// <summary>第一輪的標籤（縮圖）。**它是識別碼，不是排版**：驗收會斷言這幾個字。</summary>
    private const string QrFirstPassLabel = "first-pass";

    /// <summary>第二輪的標籤（原尺寸）。見 <see cref="QrReadRound"/>。</summary>
    private const string QrFullSizeLabel = "full-size";

    /// <summary>一輪解碼：它的標籤、**它的畫格（延遲取得）**，以及只有這一輪的碼表。</summary>
    /// <param name="Frame">
    /// ⚠ **是一個延遲工廠，不是一個已經解好的畫格**。原因就是這個工具存在的理由：
    /// 「第一輪解到就不解原尺寸」只有在**原尺寸那一張還沒被解碼**時才有意義。
    /// 先把原尺寸 <c>BitmapImage</c> 建好再放進迴圈，等於每一張圖都已經付過全尺寸解碼的成本
    /// （3000×2250 是一張 27 MB 的點陣圖），而降採樣帶來的好處就整個消失了。
    /// 每一輪在**第一次被用到**時才呼叫它；工廠只被呼叫一次（迴圈每輪最多跑一次）。
    /// </param>
    private sealed record QrReadRound(string Label, Func<BitmapSource> Frame)
    {
        /// <summary>
        /// 碼表在**建構時就開始**（不是迴圈裡才開始）：這樣它涵蓋的正是「這一輪真正花的時間」，
        /// 也就是把該輪的延遲解碼也算進去——不然日誌上會出現一輪「0 ms 解完 3000×2250」。
        /// 總計另外有一個 <c>total</c> 碼表，兩者都寫進日誌。
        /// </summary>
        public System.Diagnostics.Stopwatch Timer { get; } = System.Diagnostics.Stopwatch.StartNew();
    }

    /// <summary>
    /// 這一張圖要解幾輪、每一輪用哪個尺寸。<paramref name="RetryFullSize"/> 為 false 代表
    /// **沒有「原尺寸」那一輪**（第一輪本來就是原尺寸），呼叫端必須跳過它——
    /// 解兩次同樣的像素不是「更努力」，只是浪費時間。
    /// </summary>
    private sealed record QrReadRounds(IReadOnlyList<QrReadRound> Rounds, bool RetryFullSize,
                                       int FullWidth, int FullHeight)
    {
        /// <summary>
        /// 日誌與訊息用的「試過什麼」摘要。**尺寸取自記下來的數字，不取自畫格**——
        /// 讀畫格會**強迫解碼**（這正是延遲工廠要避免的事），而且會在「還沒輪到那一輪」時
        /// 就把成本提前付掉（實測過：那一種寫法讓「第一輪解到就不解原尺寸」變成一句空話）。
        /// </summary>
        public string Describe()
        {
            var parts = new List<string>();
            foreach (QrReadRound r in Rounds)
            {
                parts.Add($"{r.Label} {(r.Label == QrFullSizeLabel ? FullSizeText : ScaledSizeText)}");
            }
            return string.Join(", ", parts);
        }

        private string FullSizeText => $"{FullWidth}x{FullHeight}";

        /// <summary>
        /// 第一輪的尺寸。縮過的時候是「只設長邊、短邊依比例」（與 <see cref="BitmapImage"/> 的
        /// <c>DecodePixelWidth</c>／<c>DecodePixelHeight</c> 同一條規則）；**沒縮的時候就是原尺寸**
        /// （小圖走的就是這一條）——這裡只是把同一件事寫出來給日誌用，不重算一次解碼策略。
        /// </summary>
        private string ScaledSizeText => !RetryFullSize
            ? FullSizeText
            : FullWidth >= FullHeight
                ? $"{QrFirstPassPixels}x{Math.Max(1, (int)Math.Round((double)FullHeight * QrFirstPassPixels / FullWidth))}"
                : $"{Math.Max(1, (int)Math.Round((double)FullWidth * QrFirstPassPixels / FullHeight))}x{QrFirstPassPixels}";
    }

    /// <summary>
    /// 建出兩段式策略的輪次清單（第一輪縮圖、第二輪原尺寸）。
    ///
    /// 縮圖走 <see cref="BitmapImage.DecodePixelWidth"/>：**JPEG／PNG 的 codec 直接解到指定尺寸**，
    /// 所以整張大圖從來沒有被展開過（與 <see cref="DecodeFirstFrame"/> 的
    /// <c>BitmapCacheOption.OnLoad</c> 不同——那一條一定會展開全尺寸）。
    /// 只在「原尺寸真的比上限大」時才用它，否則第一輪就是原尺寸。
    ///
    /// ⚠ **不要改成 <c>TransformedBitmap</c> 縮圖**：那一條會先把全尺寸解出來再縮，
    /// 而 3000×2250 的展開正是這個工具要避開的成本（見兩段式策略的說明）。
    ///
    /// ⚠ **兩輪都以「延遲工廠」的形式交出**（見 <see cref="QrReadRound.Frame"/>）——
    /// 這裡**只讀檔頭**（<c>DelayCreation</c>）取得原始尺寸，然後把串流關掉；
    /// 真正解碼發生在工廠被呼叫時。原尺寸那一輪尤其不能提前解：
    /// 提前解＝每一張圖都付了全尺寸解碼的成本，降採樣就白做了。
    /// </summary>
    private static QrReadRounds DecodeQrRounds(string sourcePath)
    {
        // 只讀檔頭：DelayCreation 讓 EndInit 不解像素，PixelWidth／PixelHeight 由 metadata 回答。
        // 尺寸問完就把串流關掉，兩輪各自在工廠裡重新開檔（唯讀、可共用）。
        int fullW, fullH;
        using (var input = new FileStream(LongPath.Prepare(sourcePath), FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite))
        {
            var head = new BitmapImage();
            head.BeginInit();
            head.CacheOption = BitmapCacheOption.OnLoad;
            head.CreateOptions = BitmapCreateOptions.DelayCreation;
            head.StreamSource = input;
            head.EndInit();
            fullW = head.PixelWidth;
            fullH = head.PixelHeight;
        }

        bool scaled = Math.Max(fullW, fullH) > QrFirstPassPixels;
        var rounds = new List<QrReadRound>();
        // 第一輪的標籤一律是 `first-pass`，**不管它有沒有被縮**：它的身分是「第一輪」，
        // 而不是「縮圖那一輪」。小圖的 first-pass 就是原尺寸（而且沒有第二輪）。
        // ⚠ 這一條不是排版問題：跳過第二輪的判斷就看這個標籤（見 ReadQr 的迴圈），
        // 把一張 512x512 的圖標成 `full-size` 會讓它**連第一輪都不解**——
        // 2026-10-07 實測到這個 bug（乾淨的 512x512 QR 回 QrNotFound，日誌上
        // 只有 `skipped full-size`，一次解碼都沒跑）。
        rounds.Add(new QrReadRound(QrFirstPassLabel, scaled
            ? () => DecodeScaled(sourcePath, fullW, fullH)
            : () => DecodeFull(sourcePath)));
        if (scaled)
        {
            // 第二輪：原尺寸。**只在第一輪真的縮過時才存在**（見 RetryFullSize）；
            // 它也是一個工廠，所以第一輪解到時這一張從來沒有被解碼過。
            rounds.Add(new QrReadRound(QrFullSizeLabel, () => DecodeFull(sourcePath)));
        }

        return new QrReadRounds(rounds, scaled, fullW, fullH);
    }

    /// <summary>
    /// 把來源**在解碼時**縮到長邊 <see cref="QrFirstPassPixels"/> px。
    /// 兩個 <c>DecodePixel</c> 只有一個會被設（見呼叫端的註解）；<c>OnLoad</c> 讓檔案串流
    /// 可以在 <c>using</c> 結束後關掉——畫格已經在記憶體裡了（與 <see cref="DecodeFirstFrame"/>
    /// 同一個理由，只是尺寸不同）。<c>Freeze</c> 與其他解碼路徑一致：凍結過的畫格才可以跨執行緒交付。
    /// </summary>
    private static BitmapSource DecodeScaled(string sourcePath, int fullW, int fullH)
    {
        using var input = new FileStream(LongPath.Prepare(sourcePath), FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = fullW >= fullH ? QrFirstPassPixels : 0;
        image.DecodePixelHeight = fullH > fullW ? QrFirstPassPixels : 0;
        image.StreamSource = input;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 原尺寸那一輪的畫格。刻意**不用** <see cref="DecodeFirstFrame"/>：那一條會回一個
    /// <c>BitmapFrame</c>（帶著 metadata 與色彩內容），而這裡只要像素，而且
    /// 「原尺寸」的語意要與第一輪（<see cref="BitmapImage"/> 解到指定尺寸）成對——
    /// 兩輪走同一個型別，才不會出現「第一輪是 A 型別、第二輪是 B 型別」這種只有讀原始碼
    /// 才知道的差別。
    /// </summary>
    private static BitmapSource DecodeFull(string sourcePath)
    {
        using var input = new FileStream(LongPath.Prepare(sourcePath), FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = input;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 畫格 → **BGRA32 的原始位元組**（<c>Bgra32</c> ＝ 藍、綠、紅、alpha，就是
    /// <see cref="RGBLuminanceSource.BitmapFormat.BGRA32"/> 說的那一種排列）。
    ///
    /// <see cref="CopyPixels(Array, int, int)"/> 需要 stride，而且**要自己配置目的陣列**：
    /// stride 用 <c>width * 4</c>（Bgra32 每像素 4 位元組，而且 WIC 的 stride 就是緊湊的）。
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

    /// <summary>
    /// 解碼器。**這是全檔唯一一處 <c>BarcodeReaderGeneric</c>**，所以
    /// 「設定了哪些選項」只有一個地方要維護（見 <see cref="ReadQr"/> 對每個選項的理由）。
    ///
    /// ⚠ **`TryHarder` 在 `Options` 上，不在讀取器上**：`BarcodeReaderGeneric` 只有三個公開屬性
    /// （`Options`／`AutoRotate`／`TryInverted`），`TryHarder` 是
    /// <c>ZXing.Common.DecodingOptions</c> 的成員。這是 2026-10-07 用反射對
    /// `lib/net8.0/zxing.dll`（0.16.11）實測的結果（第一次寫成 `reader.TryHarder = true`
    /// 直接編譯失敗：CS0117）。同一次實測也確認了 `RGBLuminanceSource.BitmapFormat` 含 `BGRA32`，
    /// 以及 <c>Decode(byte[], int, int, BitmapFormat)</c> 這個多載存在。
    ///
    /// `CharacterSet` 不指定：ZXing 對 QR 的預設是 ISO-8859-1，而編碼端寫進碼裡的
    /// ECI／字元集指示會被解碼器自己讀出來覆蓋它，所以硬指定一個值只會在
    /// 「碼是 UTF-8」的時候幫倒忙（把多位元組序列解成亂碼）。
    /// </summary>
    private static BarcodeReaderGeneric QrReaderFor()
    {
        // AutoRotate 刻意留在預設值（false）並寫出來：開它＝同一張圖解四次（見 ReadQr）。
        var reader = new BarcodeReaderGeneric { AutoRotate = false };
        reader.Options.PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE };
        // 旋轉／低對比的碼要更努力掃；成本只落在「本來就會失敗」的那一輪。
        reader.Options.TryHarder = true;
        return reader;
    }

    /// <summary>「壓縮到」的搜尋結果。</summary>
    /// <param name="Reachable">最低品質是否就進得了上限。false 時 <see cref="Bytes"/> 是 null。</param>
    /// <param name="Quality">找到的最佳品質（<see cref="Reachable"/> 為 false 時沒有意義）。</param>
    /// <param name="Bytes">那個品質編出來的位元組（原封不動寫出，不再重編一次）。</param>
    /// <param name="SizeAtFloor">最低品質的位元組數（做不到時，這是「實話」的那個數字）。</param>
    private sealed record SearchOutcome(bool Reachable, int Quality, byte[]? Bytes, long SizeAtFloor);

    /// <summary>
    /// 「壓縮到」的核心：在**可用的品質區間**上二分搜尋，取**不超過上限裡最好的那一版**。
    ///
    /// 為什麼是二分搜尋而不是線性掃描：JPEG 的輸出大小對品質是（實務上）單調不減的，
    /// 而區間只有 41 個整數值——但每一個值都代表一次完整的編碼（解碼後的畫格 × 熵編碼），
    /// 在 2816×2816 上是一次幾百毫秒的工作。二分搜尋把最壞情況從 41 次降到 6 次。
    ///
    /// **「最好的那一版」怎麼定義、怎麼證明**：
    ///   • 定義：在 {60…100} 之中，**輸出 ≤ 上限**的最大品質。
    ///   • 證明：跑完之後**再編一次 q+1**（若 q &lt; 100）並量它的實際位元組數，
    ///     它必須 &gt; 上限。那不是推論，是同一次執行裡量到的數字，而且會寫進日誌
    ///     （`compress: target-size proof ...`）。所以「再升一級就會超過上限」是當場驗過的，
    ///     不是靠單調性的假設。單調性本身也留了痕跡：每一次探測的 (品質, 位元組數) 都逐行寫進日誌。
    ///
    /// ⚠ 區間下界是 <see cref="CompressionPolicy.SmallQuality"/>（60），因為那是規格 §8 品質
    /// 滑桿的下限，也是本產品認可的最低品質；`JpegBitmapEncoder` 自己也會把值夾到 60。
    /// 連 60 都超過上限時回 <c>Reachable=false</c>——呼叫端必須**不寫檔**並回報實話。
    /// </summary>
    private SearchOutcome SearchByQuality(BitmapSource frame, string sourcePath, string sourceFormat,
                                           bool bakedOrientation, long target)
    {
        const int lo0 = CompressionPolicy.SmallQuality;
        const int hi0 = 100;

        byte[] atFloor = EncodeToBytes(frame, sourcePath, sourceFormat, lo0, bakedOrientation);
        _log($"compress search: quality={lo0} -> {atFloor.LongLength}B " +
             $"({(atFloor.LongLength <= target ? "<=" : ">")} target {target}B)");
        if (atFloor.LongLength > target) return new SearchOutcome(false, 0, null, atFloor.LongLength);

        int lo = lo0, hi = hi0, best = lo0;
        byte[] bestBytes = atFloor;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (mid == lo0) { lo = mid + 1; continue; }   // 下限已經量過了，不要再編一次
            byte[] bytes = EncodeToBytes(frame, sourcePath, sourceFormat, mid, bakedOrientation);
            bool fits = bytes.LongLength <= target;
            _log($"compress search: quality={mid} -> {bytes.LongLength}B " +
                 $"({(fits ? "<=" : ">")} target {target}B)");
            if (fits)
            {
                best = mid;
                bestBytes = bytes;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // 當場的證據：再升一級就會超過上限（q = 100 時沒有「再升一級」這種東西）。
        if (best < hi0)
        {
            byte[] next = EncodeToBytes(frame, sourcePath, sourceFormat, best + 1, bakedOrientation);
            _log($"compress: target-size proof quality={best} -> {bestBytes.LongLength}B <= {target}B; " +
                 $"quality={best + 1} -> {next.LongLength}B " +
                 $"{(next.LongLength > target ? ">" : "<=")} {target}B " +
                 (next.LongLength > target
                     ? "(so this is the best version that fits the limit)"
                     : "(WARNING: the next step also fits, so the search is not optimal -- the encoder is " +
                       "not monotone in quality here; the probed sizes above are the evidence)"));
        }
        else
        {
            _log($"compress: target-size proof quality={hi0} is the maximum the encoder accepts; " +
                 $"{bestBytes.LongLength}B <= {target}B");
        }

        return new SearchOutcome(true, best, bestBytes, atFloor.LongLength);
    }

    /// <summary>
    /// 用與寫檔**完全相同**的那條編碼路徑編到記憶體裡（<see cref="EncodeWithMetadataFallback"/>），
    /// 以便在寫檔之前先量大小。metadata 的取捨（保不住就退回不夾帶）因此與真正寫出的那一版一致。
    /// </summary>
    private byte[] EncodeToBytes(BitmapSource frame, string sourcePath, string sourceFormat,
                                 int quality, bool bakedOrientation)
    {
        using var buffer = new MemoryStream();
        EncodeWithMetadataFallback(frame, sourcePath, sourceFormat, quality, buffer, bakedOrientation);
        return buffer.ToArray();
    }

    /// <summary>
    /// 去 metadata 的第二條路：解碼 → **不夾帶任何 metadata／ICC** → 以來源格式重編碼。
    ///
    /// <c>keepMetadata: false</c> 同時丟掉 <see cref="BitmapMetadata"/> 與 <c>ColorContexts</c>，
    /// 而後者就是 ICC profile 的來源——「保留 ICC」在這是**刻意沒有**的（規格 §9.4：
    /// 去識別化的場合 ICC 與 EXIF 同等級）。
    /// </summary>
    private void StripByReencode(string sourcePath, string sourceFormat, Stream stream)
    {
        BitmapSource frame;
        try
        {
            (frame, int frameCount) = DecodeFirstFrame(sourcePath);
            if (frameCount > 1)
                _log($"note: {Path.GetFileName(sourcePath)} has {frameCount} frames; only the first is stripped (spec 6.1)");
        }
        catch (Exception ex)
        {
            throw new StripDecodeFailure(
                $"decode failed while stripping {Path.GetFileName(sourcePath)} " +
                $"({ex.GetType().Name}: {ex.Message})", ex);
        }

        int orientation = ReadOrientation(frame);
        if (orientation >= 2)
        {
            frame = ApplyOrientation(frame, orientation);
            _log($"strip-metadata: EXIF={orientation} baked into pixels before the tag was dropped " +
                 $"({frame.PixelWidth}x{frame.PixelHeight})");
        }

        // 品質 100：重編碼路徑最接近無損的一檔（與 Rotate90 同一個值，同一個理由）。
        Encode(frame, sourcePath, sourceFormat, 100, stream, keepMetadata: false,
               bakedOrientation: orientation >= 2);
    }

    /// <summary>
    /// 備援路徑（重編碼）的解碼失敗。存在的理由是**分類**：沒有它，一個「備援路上才發現的
    /// 解碼失敗」會在 <see cref="ClassifyWriteFailure"/> 眼裡變成 Unknown，
    /// 使用者看到「未知的錯誤」而不是「無法解碼」（規格 §7.1）。
    /// </summary>
    private sealed class StripDecodeFailure(string message, Exception inner) : Exception(message, inner);

    /// <summary>
    /// 讀一個檔案的 EXIF 方向（tag 274），**不解整個檔**（<see cref="BitmapCacheOption.None"/>：
    /// 只讀標頭與中繼資料，規格 §5 的「大檔不整檔載入記憶體」）。
    /// 讀不到一律回 1（正常）：那是「沒有方向宣告」的意思，不是「不知道」。
    /// </summary>
    private static int ReadOrientationOfFile(string path)
    {
        try
        {
            using var input = new FileStream(LongPath.Prepare(path), FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite);
            BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
                                                         BitmapCacheOption.None);
            return decoder.Frames.Count == 0 ? 1 : ReadOrientation(decoder.Frames[0]);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// 解碼第一頁／第一格。來源**唯讀**開啟且允許他人共用——
    /// 這一條是規格 §7.2 的第一名（原檔被修改／被刪／被搬移），不能靠「記得不要寫」來保證。
    /// </summary>
    /// <summary>
    /// 解碼第一頁／第一格。來源**唯讀**開啟且允許他人共用——
    /// 這一條是規格 §7.2 的第一名（原檔被修改／被刪／被搬移），不能靠「記得不要寫」來保證。
    ///
    /// 可見度同 <see cref="ReadOrientation"/>：**裁切視窗**要用它顯示「引擎等一下會拿去裁的那一張
    /// 畫格」。實作仍然只有這一份（解碼選項 <c>PreservePixelFormat</c> ＋ <c>OnLoad</c> 也是），
    /// 所以視窗看到的與引擎裁的保證是同一張。
    /// </summary>
    internal static (BitmapSource Frame, int FrameCount) DecodeFirstFrame(string sourcePath)
    {
        using var input = new FileStream(LongPath.Prepare(sourcePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
                                                     BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new FileFormatException("decoder returned no frames");
        return (decoder.Frames[0], decoder.Frames.Count);
    }

    /// <summary>
    /// 編碼。metadata／ICC **盡量原樣保留**（規格 §5）；保不住就退回「不夾帶」並**記在日誌**——
    /// 不同容器對 metadata 的接受度不同（EXIF 要塞進 PNG 不一定成立），
    /// 靜默丟掉會讓使用者以為 metadata 還在。
    /// </summary>
    private void EncodeWithMetadataFallback(BitmapSource frame, string sourcePath, string targetFormat,
                                            int quality, Stream output, bool bakedOrientation)
    {
        try
        {
            Encode(frame, sourcePath, targetFormat, quality, output, keepMetadata: true,
                   bakedOrientation: bakedOrientation);
        }
        catch (Exception ex)
        {
            _log($"metadata: dropped for {Path.GetFileName(sourcePath)} -> {targetFormat} " +
                 $"({ex.GetType().Name}: {ex.Message})");
            output.SetLength(0);
            output.Position = 0;
            Encode(frame, sourcePath, targetFormat, quality, output, keepMetadata: false,
                   bakedOrientation: bakedOrientation);
        }
    }

    private static void Encode(BitmapSource frame, string sourcePath, string targetFormat, int quality,
                               Stream output, bool keepMetadata, bool bakedOrientation)
    {
        BitmapSource pixels = frame;
        if (targetFormat == "BMP" && pixels.Format != PixelFormats.Bgra32)
            pixels = new FormatConvertedBitmap(pixels, PixelFormats.Bgra32, null, 0);   // 規格 §6.2：BMP 32-bit

        BitmapEncoder? encoder = CodecRegistry.CreateEncoder(targetFormat, quality);
        if (encoder == null) throw new NotSupportedException($"no encoder for {targetFormat}");

        BitmapMetadata? metadata = null;
        ReadOnlyCollection<ColorContext>? colors = null;
        if (keepMetadata && frame is BitmapFrame asFrame)
        {
            metadata = asFrame.Metadata as BitmapMetadata;
            colors = asFrame.ColorContexts;
        }
        // 方向已經烤進像素了，tag 必須中和，否則會轉第二次
        if (bakedOrientation) metadata = NeutralizeOrientation(metadata);

        encoder.Frames.Add(BitmapFrame.Create(pixels, null, metadata, colors));
        encoder.Save(output);
    }

    /// <summary>
    /// **EXIF 方向**（tag 274）。實測：來源 8×4、方向 6，轉成 PNG 後仍是 8×4 且標籤沒有帶過去，
    /// 也就是手機直拍的照片轉出來會是躺著的——而 PNG／BMP／GIF 的檢視器幾乎都不看 EXIF 方向。
    ///
    /// 因此規則分兩種（規格 §5「metadata 原樣保留」是原則，但這裡兩種錯的代價不同）：
    ///   • JPG／TIFF：檢視器會遵循方向 → **原樣保留**（像素不動、tag 不動），真正「原樣」。
    ///   • PNG／BMP／WebP／GIF：檢視器不遵循 → **把方向烤進像素**，並把 tag 中和成 1
    ///     （否則會被會看 tag 的檢視器轉第二次）。
    /// </summary>
    private static bool TargetHonoursExifOrientation(string targetFormat) => targetFormat is "JPG" or "TIF";

    private static readonly string[] OrientationQueries = { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" };

    /// <summary>
    /// 讀 EXIF 方向；讀不到回 1（正常）。
    ///
    /// ⚠ 2026-10-06：由 <c>private</c> 改為 <c>internal</c> 的唯一理由是**裁切視窗**——
    /// 它要顯示的必須是「引擎等一下會拿去裁的那張畫格」，否則使用者在躺著的圖上框出來的矩形
    /// 會落在錯的地方。**沒有任何行為改變**（可見度不影響 IL 行為），而且擁有者仍然只有這裡一份。
    /// </summary>
    internal static int ReadOrientation(BitmapSource frame)
    {
        if (frame is not BitmapFrame asFrame || asFrame.Metadata is not BitmapMetadata metadata) return 1;
        foreach (string query in OrientationQueries)
        {
            try
            {
                if (!metadata.ContainsQuery(query)) continue;
                object? value = metadata.GetQuery(query);
                if (value is ushort u && u >= 1 && u <= 8) return u;
                if (value is short s && s >= 1 && s <= 8) return s;
            }
            catch { }
        }
        return 1;
    }

    /// <summary>把 EXIF 方向套用到像素上（1–8 的完整對應）。可見度同 <see cref="ReadOrientation"/>：裁切視窗要顯示同一張畫格。</summary>
    internal static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => Group(new RotateTransform(90), new ScaleTransform(-1, 1)),
            6 => new RotateTransform(90),
            7 => Group(new RotateTransform(90), new ScaleTransform(1, -1)),
            8 => new RotateTransform(270),
            _ => null
        };
        return transform == null ? source : ApplyTransform(source, transform);
    }

    /// <summary>
    /// 把一個 WIC transform 套到畫格上。**這是全檔唯一一處 new TransformedBitmap**——
    /// EXIF 方向（<see cref="ApplyOrientation"/>）與工具旋轉（<see cref="Rotate90"/>）都走這裡，
    /// 免得兩條路各自記得（或忘記）Freeze。Freeze 是必要的：凍結後的畫格才能跨執行緒
    /// 交給編碼器，少了它會在 worker 的執行緒上炸開。
    /// </summary>
    private static BitmapSource ApplyTransform(BitmapSource source, Transform transform)
    {
        var transformed = new TransformedBitmap();
        transformed.BeginInit();
        transformed.Source = source;
        transformed.Transform = transform;
        transformed.EndInit();
        transformed.Freeze();
        return transformed;
    }

    private static TransformGroup Group(params Transform[] children)
    {
        var group = new TransformGroup();
        foreach (Transform child in children) group.Children.Add(child);
        return group;
    }

    /// <summary>烤進像素之後把 tag 中和成 1，免得被會遵循 tag 的檢視器轉第二次。</summary>
    private static BitmapMetadata? NeutralizeOrientation(BitmapMetadata? metadata)
    {
        if (metadata == null) return null;
        try
        {
            var copy = (BitmapMetadata)metadata.Clone();
            foreach (string query in OrientationQueries)
            {
                try { if (copy.ContainsQuery(query)) copy.SetQuery(query, (ushort)1); } catch { }
            }
            return copy;
        }
        catch
        {
            return metadata;
        }
    }

    private static ConversionError MapDecodeStatus(DecodeStatus status) => status switch
    {
        DecodeStatus.Ok => ConversionError.None,
        DecodeStatus.Corrupt => ConversionError.DecodeFailed,
        DecodeStatus.NeedsExtension => ConversionError.DecodeFailed,   // §6.3 的訊息由 UI 呈現「需要 HEIF 擴充」
        DecodeStatus.Unsupported => ConversionError.Unsupported,
        DecodeStatus.Unreadable => ConversionError.Unreadable,
        _ => ConversionError.Unknown
    };

    /// <summary>可重試的暫時性失敗（被鎖住）。</summary>
    private static bool IsTransient(Exception ex) => ClassifyWriteFailure(ex) == ConversionError.Locked;

    /// <summary>寫入失敗 → §7.1 的分類。用 Win32 錯誤碼判斷，不看訊息文字。</summary>
    private static ConversionError ClassifyWriteFailure(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ConversionError.WriteDenied,
        IOException io when io.HResult == unchecked((int)0x80070005) => ConversionError.WriteDenied,  // ACCESS_DENIED
        IOException io when io.HResult == unchecked((int)0x80070070) => ConversionError.NoSpace,       // DISK_FULL
        IOException io when io.HResult == unchecked((int)0x80070027) => ConversionError.NoSpace,       // HANDLE_DISK_FULL
        IOException io when io.HResult == unchecked((int)0x80070020) => ConversionError.Locked,        // SHARING_VIOLATION
        IOException io when io.HResult == unchecked((int)0x80070021) => ConversionError.Locked,        // LOCK_VIOLATION
        IOException => ConversionError.Unreadable,
        _ => ConversionError.Unknown
    };

    /// <summary>
    /// 寫入迴圈——**五條轉檔路徑唯一的重試擁有者**。
    ///
    /// 兩種失敗必須分開處理，混在一起就是 2026-10-06 量到的那個缺陷：
    /// <list type="number">
    /// <item><b>名字被搶走</b>（`ERROR_ALREADY_EXISTS`，0x800700B7）：另一個行程在
    /// <see cref="NamingPolicy"/> 選名與 <c>File.Move</c> 之間的那個窗口裡拿走了同一個檔名。
    /// 這是**可修復**的——把那個名字記進 <paramref name="exclude"/>，換下一個候選名再試一次。
    /// 修不好也不可以說成「無法讀取來源檔案」：來源好好的，說錯原因會把使用者送去
    /// 檢查一個沒有問題的檔案。</item>
    /// <item><b>暫時被鎖住</b>（`SHARING_VIOLATION`／`LOCK_VIOLATION`，0x20／0x21）：
    /// 防毒與同步軟體會短暫鎖檔，等一下就過了（規格 §7.1）。</item>
    /// </list>
    ///
    /// ⚠ 沒有 <paramref name="exclude"/> 就沒有重試可言：下一次
    /// <paramref name="resolve"/> 會**再選回同一個名字**，然後永遠失敗。
    /// </summary>
    /// <param name="resolve">
    /// 由「已經試過的名字」算出下一個候選完整路徑。每一個 op 有自己的命名規則
    /// （`crop` 保持來源格式、其餘走 <see cref="NamingPolicy"/>），所以由呼叫端帶進來；
    /// **重試的規則本身則留在這裡一份**。
    /// </param>
    /// <param name="classify">
    /// 失敗分類。預設是 <see cref="ClassifyWriteFailure"/>；`strip-metadata` 例外，它的備援
    /// 路徑（容器層失敗 → 解碼＋重編碼）會拋 <c>StripDecodeFailure</c>，那一種要沿用**解碼**
    /// 的分類（規格 §7.1 的訊息不可以因為「這一條是備援」就降級）。預設值讓這條規則只有
    /// 一個地方要維護，而不是每個 op 各抄一份。
    /// </param>
    private ConversionResult WriteWithCollisionRetry(
        string sourcePath, string firstTargetPath, TempJournal? journal,
        Action<Stream> writeContent, Func<IReadOnlyCollection<string>, string> resolve,
        out string finalPath, Func<Exception, (ConversionError Error, string Detail)>? classify = null)
    {
        Func<Exception, (ConversionError Error, string Detail)> failure =
            classify ?? (ex => (ClassifyWriteFailure(ex),
                                $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {ex.Message}"));
        var tried = new List<string>();
        string targetPath = firstTargetPath;
        int transient = 0;

        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                AtomicWriter.Write(targetPath, writeContent, journal);
                finalPath = targetPath;
                return new ConversionResult(sourcePath, targetPath, ConversionError.None, targetPath);
            }
            catch (Exception ex) when (IsNameTaken(ex) && attempt < MaxWriteAttempts)
            {
                tried.Add(targetPath);
                // ⚠ 2026-10-16（獨立稽核）：這句原本寫成「was taken by another process … (a second
                // --convert, or the resident Confern, is writing the same folder)」——那個「誰」是**猜的**。
                // 它當時是在掩蓋一個 bug（圖片合併把來源檔當成第一版輸出名 ⇒ 每次必定碰撞），
                // 那個 bug 修掉之後，走到這裡只剩兩種真原因：有另一個行程同時在寫，或那個名字
                // 本來就躺著一個檔。訊息只講我們**知道**的事（名字在寫入當下已經存在），
                // 要指名「誰」就得有證據。進度側自己的重啟另有 `progress restarted` 那條（不同事件）。
                _log($"write retry {attempt}/{MaxWriteAttempts} for {Path.GetFileName(sourcePath)}: " +
                     $"the output name '{Path.GetFileName(targetPath)}' already existed at write time " +
                     $"({ex.GetType().Name} 0x{ex.HResult:X8}) -> picking another name");
                string next = resolve(tried);
                if (string.Equals(next, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    // 算不出別的名字就別裝作還在重試——重試同一個名字一百次也不會過。
                    finalPath = targetPath;
                    return Fail(sourcePath, ConversionError.WriteConflict,
                        $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {ex.Message} " +
                        "(the name resolver returned the same name again)");
                }
                targetPath = next;
            }
            catch (Exception ex) when (IsTransient(ex) && transient < MaxTransientRetries)
            {
                transient++;
                _log($"write retry {transient}/{MaxTransientRetries} for {Path.GetFileName(targetPath)}: " +
                     $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
                Thread.Sleep(150 * transient);
            }
            catch (Exception ex)
            {
                finalPath = targetPath;
                (ConversionError error, string detail) = failure(ex);
                return Fail(sourcePath, error, detail);
            }
        }

        finalPath = targetPath;
        // name conflicts and lock waits share one budget, so say which one ran out.
        return transient >= MaxTransientRetries
            ? Fail(sourcePath, ConversionError.Locked, "still locked after " + MaxTransientRetries + " attempts")
            : Fail(sourcePath, ConversionError.WriteConflict,
                   ConversionErrorNameConflictDetail(targetPath));
    }

    private static string ConversionErrorNameConflictDetail(string lastTried) =>
        $"every candidate name up to '{Path.GetFileName(lastTried)}' was taken by another process";

    /// <summary>
    /// 「這個檔名剛剛被別人拿走了」——**用 Win32 錯誤碼判斷，不看訊息文字**。
    ///
    /// <c>File.Move(overwrite: false)</c> 在目標已存在時給的是 0x800700B7
    /// （`ERROR_ALREADY_EXISTS`）；0x80070050 是 `ERROR_FILE_EXISTS`，是同一件事的另一個入口。
    /// 這兩個都**必須**走「換名字重試」，而不是走 <see cref="IsTransient"/> 的「等一下再試同一個名字」。
    /// </summary>
    private static bool IsNameTaken(Exception ex) =>
        ex is IOException io && (io.HResult == unchecked((int)0x800700B7)
                              || io.HResult == unchecked((int)0x80070050));

    /// <summary>重試預算。名字衝突會把每一個候選都試過一輪，所以它比暫時性重試寬。</summary>
    private const int MaxWriteAttempts = 8;
    private const int MaxTransientRetries = 3;

    /// <summary>
    /// `crop` 的命名規則與 <see cref="Convert"/> 不同：輸出**保持來源格式**
    /// （副檔名與來源相同），所以不能用目標格式的副檔名再算一次。
    /// 這裡仍然只呼叫 <see cref="NamingPolicy"/>——命名規則不得有第二份實作。
    ///
    /// <paramref name="extension"/> 由呼叫端把它**已經算好的那一個**傳進來：
    /// 這裡自己再從格式推一次就是第二份「來源格式 → 副檔名」的規則，而兩份一旦不同步，
    /// 重試時算出來的名字就會與第一次不同（例如 `.jpg` vs `.JPG`），使用者會看到兩個不同的檔。
    /// </summary>
    private static Func<IReadOnlyCollection<string>, string> CollisionCrop(
        string sourcePath, string extension, string? targetDirectory) =>
        tried => NamingPolicy.ResolveOutputPath(sourcePath, extension, targetDirectory, tried.ToList());

    private static ConversionResult Fail(string sourcePath, ConversionError error, string detail) =>
        new(sourcePath, null, error, detail);
}
