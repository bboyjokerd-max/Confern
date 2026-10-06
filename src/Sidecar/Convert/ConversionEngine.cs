using System.Collections.ObjectModel;
using System.Windows;                 // Int32Rect（裁切矩形用；WindowsBase）
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        // 5) 解碼一次，然後寫入（最多 3 次：防毒／同步軟體會短暫鎖檔，規格 §7.1）
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

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                long written = 0;
                var convertTimer = System.Diagnostics.Stopwatch.StartNew();
                AtomicWriter.Write(targetPath, stream =>
                {
                    EncodeWithMetadataFallback(frame, sourcePath, targetFormat, quality, stream, bakedOrientation);
                    written = stream.Length;
                }, journal);
                convertTimer.Stop();

                // 逐檔耗時要看得見：規格 §9.3 有「5 MB JPG→PNG < 800 ms」的預算，
                // 沒有這個數字就只能靠感覺，而感覺沒辦法發現退化。
                _log($"converted {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
                     $"({sourceFormat}->{targetFormat}, {written}B, quality={quality}, " +
                     $"{convertTimer.Elapsed.TotalMilliseconds:F0} ms)");
                return new ConversionResult(sourcePath, targetPath, ConversionError.None,
                    $"{written}B in {convertTimer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}");
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                _log($"write retry {attempt}/3 for {Path.GetFileName(targetPath)}: " +
                     $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
                Thread.Sleep(150 * attempt);
            }
            catch (Exception ex)
            {
                return Fail(sourcePath, ClassifyWriteFailure(ex),
                    $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {ex.Message}");
            }
        }

        return Fail(sourcePath, ConversionError.Locked, "still locked after 3 attempts");
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

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                long written = 0;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                AtomicWriter.Write(targetPath, stream =>
                {
                    // bakedOrientation: true → 方向已烤進像素，所以 EXIF tag 要被中和成 1，
                    // 否則會遵循 tag 的檢視器會再轉一次（旋轉工具尤其致命）。
                    EncodeWithMetadataFallback(frame, sourcePath, sourceFormat, 100, stream, bakedOrientation: true);
                    written = stream.Length;
                }, journal);
                timer.Stop();

                _log($"rotated {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
                     $"({written}B, {timer.Elapsed.TotalMilliseconds:F0} ms)");
                return new ConversionResult(sourcePath, targetPath, ConversionError.None,
                    $"{written}B in {timer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}");
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                _log($"write retry {attempt}/3 for {Path.GetFileName(targetPath)}: " +
                     $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
                Thread.Sleep(150 * attempt);
            }
            catch (Exception ex)
            {
                return Fail(sourcePath, ClassifyWriteFailure(ex),
                    $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {ex.Message}");
            }
        }

        return Fail(sourcePath, ConversionError.Locked, "still locked after 3 attempts");
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

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                long written = 0;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                AtomicWriter.Write(targetPath, stream =>
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
                }, journal);
                timer.Stop();

                _log($"stripped {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
                     $"({written}B, {(lossless ? "container" : "re-encode")}, {timer.Elapsed.TotalMilliseconds:F0} ms)");
                return new ConversionResult(sourcePath, targetPath, ConversionError.None,
                    $"{written}B in {timer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}");
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                _log($"write retry {attempt}/3 for {Path.GetFileName(targetPath)}: " +
                     $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
                Thread.Sleep(150 * attempt);
            }
            catch (Exception ex)
            {
                // 備援路徑上的**解碼**失敗要沿用解碼的分類（規格 §7.1 的訊息不可以因為
                // 「這一條是備援」就降級成 Unknown）。
                if (ex is StripDecodeFailure decodeFailure && decodeFailure.InnerException is { } inner)
                {
                    return Fail(sourcePath,
                        MapDecodeStatus(CodecRegistry.ClassifyDecodeException(inner, sourceFormat, sourcePath)),
                        $"{inner.GetType().Name}: {inner.Message}");
                }
                return Fail(sourcePath, ClassifyWriteFailure(ex),
                    $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {ex.Message}");
            }
        }

        return Fail(sourcePath, ConversionError.Locked, "still locked after 3 attempts");
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

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                long written = 0;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                AtomicWriter.Write(targetPath, stream =>
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
                }, journal);
                timer.Stop();

                _log($"compressed {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} " +
                     $"({sourceFormat}, mode={policy.Mode}, quality={quality}, " +
                     $"{beforeBytes}B -> {written}B" +
                     $"{(target > 0 ? $", target={target}B" : "")}, {timer.Elapsed.TotalMilliseconds:F0} ms)");
                return new ConversionResult(sourcePath, targetPath, ConversionError.None,
                    $"{beforeBytes}B -> {written}B in {timer.Elapsed.TotalMilliseconds:F0} ms -> {targetPath}");
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                _log($"write retry {attempt}/3 for {Path.GetFileName(targetPath)}: " +
                     $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
                Thread.Sleep(150 * attempt);
            }
            catch (Exception ex)
            {
                return Fail(sourcePath, ClassifyWriteFailure(ex),
                    $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {ex.Message}");
            }
        }

        return Fail(sourcePath, ConversionError.Locked, "still locked after 3 attempts");
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

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                long written = 0;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                AtomicWriter.Write(targetPath, stream =>
                {
                    EncodeWithMetadataFallback(cropped, sourcePath, sourceFormat, 100, stream, bakedOrientation);
                    written = stream.Length;
                }, journal);
                timer.Stop();

                // ⚠ 這一行的格式是**驗收斷言的一部分**（輸入尺寸 → 輸出尺寸）。改它要先改計畫。
                _log($"crop: {Path.GetFileName(sourcePath)} {inW}x{inH} -> {cropped.PixelWidth}x{cropped.PixelHeight} " +
                     $"(rect={rect.X},{rect.Y},{rect.Width},{rect.Height}, {spec.Describe()}, " +
                     $"{sourceFormat}, quality=100, re-encode)");
                return new ConversionResult(sourcePath, targetPath, ConversionError.None,
                    $"{inW}x{inH} -> {cropped.PixelWidth}x{cropped.PixelHeight} " +
                    $"({written}B in {timer.Elapsed.TotalMilliseconds:F0} ms) -> {targetPath}");
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                _log($"write retry {attempt}/3 for {Path.GetFileName(targetPath)}: " +
                     $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
                Thread.Sleep(150 * attempt);
            }
            catch (Exception ex)
            {
                return Fail(sourcePath, ClassifyWriteFailure(ex),
                    $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}: {ex.Message}");
            }
        }

        return Fail(sourcePath, ConversionError.Locked, "still locked after 3 attempts");
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

    private static ConversionResult Fail(string sourcePath, ConversionError error, string detail) =>
        new(sourcePath, null, error, detail);
}
