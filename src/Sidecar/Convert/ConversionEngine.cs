using System.Collections.ObjectModel;
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

    public ConversionResult Convert(string sourcePath, string targetFormat, int quality, TempJournal? journal = null)
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
            targetPath = NamingPolicy.ResolveOutputPath(sourcePath, extension);
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
    /// 解碼第一頁／第一格。來源**唯讀**開啟且允許他人共用——
    /// 這一條是規格 §7.2 的第一名（原檔被修改／被刪／被搬移），不能靠「記得不要寫」來保證。
    /// </summary>
    private static (BitmapSource Frame, int FrameCount) DecodeFirstFrame(string sourcePath)
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

    /// <summary>讀 EXIF 方向；讀不到回 1（正常）。</summary>
    private static int ReadOrientation(BitmapSource frame)
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

    /// <summary>把 EXIF 方向套用到像素上（1–8 的完整對應）。</summary>
    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
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
        if (transform == null) return source;

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
