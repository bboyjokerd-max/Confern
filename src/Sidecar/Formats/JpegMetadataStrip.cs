using System.IO;

namespace Sidecar;

/// <summary>
/// JPEG 的**容器層**去 metadata（工具 `strip-metadata`，規格 §9.4）。
///
/// 這個檔案存在的唯一理由：**去 metadata 不該動到像素**，而 JPEG 重編碼一定會動到像素。
/// 2026-10-06 當場量到：把同一張 48×28 的 JPEG 用 Q100 重編碼，5376 個解碼位元組裡有 949 個
/// 變了、單通道最大差 3。EXIF 又正好只長在 JPEG 上，所以「照旋轉工具那樣解碼再寫回」等於
/// **在最需要這個工具的格式上做不到它的驗收**。
///
/// 做法：走過 SOI 之後的每一個 marker，**丟掉中繼資料區段**，其餘位元組**原封不動照抄**。
/// 熵編碼的掃描資料（SOS 之後到檔尾）是整段 copy，所以解出來的像素與來源**逐位元組相同**——
/// 這是結構上的保證，不是「應該差不多」。
///
/// **保留哪些**（刻意的例外，理由一起寫在這裡）：
///   • `APP0`（JFIF）：容器自己的格式宣告，不是使用者資料。
///   • `APP14`（Adobe）：**色彩轉換旗標**。移除會讓某些 JPEG 的色版判定改變（YCCK／CMYK），
///     那不是「去識別化」，那是弄壞圖。它是解碼器要用的，不屬於使用者中繼資料。
/// **移除哪些**：`APP1`（EXIF／XMP）、`APP2`（ICC／MPF／FlashPix）、`APP3`–`APP13`
/// （含 `APP13` 的 Photoshop IRB／IPTC）、`APP15`、`COM`（文字註解）。
///
/// ⚠ **這個類別不解碼，也拿不到 EXIF 方向**。方向 ≠ 1 的來源必須走另一條路徑（把方向烤進像素），
/// 由 <see cref="ConversionEngine.StripMetadata"/> 決定——不要在這裡偷偷假設方向一定是 1。
/// </summary>
internal static class JpegMetadataStrip
{
    /// <summary>
    /// 移除的結果。<see cref="Parsed"/> 為 false 代表「這不是我能安全處理的 JPEG」
    /// （沒有 SOI／走到一半就沒了／長度欄位不合理）——呼叫端**必須退回重編碼路徑**，
    /// 不得把半成品當成完成品。<see cref="ScanDataCopied"/> 只有在掃描資料整段照抄時才是 true，
    /// 而那正是「像素逐位元相同」這句話的依據。
    /// </summary>
    internal sealed record Result(bool Parsed, bool ScanDataCopied, int RemovedSegments, long RemovedBytes,
                                   string RemovedSummary, string Detail);

    private const int App0 = 0xE0;   // JFIF：保留
    private const int App14 = 0xEE;  // Adobe 色彩轉換：保留
    private const int Sos = 0xDA;
    private const int Eoi = 0xD9;

    /// <summary>這個 marker 是不是「使用者中繼資料」——是就整段丟掉。</summary>
    private static bool IsMetadataMarker(int marker) =>
        marker == 0xFE                       // COM（文字註解）
        || (marker >= 0xE1 && marker <= 0xED)   // APP1..APP13
        || marker == 0xEF;                      // APP15

    private static string NameOf(int marker) => marker switch
    {
        0xE1 => "APP1",
        0xE2 => "APP2",
        0xED => "APP13",
        0xFE => "COM",
        _ => $"APP{marker - 0xE0}",
    };

    public static Result Strip(string sourcePath, Stream output)
    {
        using var input = new FileStream(LongPath.Prepare(sourcePath), FileMode.Open, FileAccess.Read,
                                         FileShare.ReadWrite);
        return Strip(input, output);
    }

    /// <summary>
    /// 逐段搬移。**來源唯讀**（呼叫端開的 FileStream 是 Read/ReadWrite 共用，符合規格 §5）。
    /// 大檔不會整檔進記憶體：只有單一區段的標頭會進緩衝，掃描資料是 64 KB 的迴圈 copy。
    /// </summary>
    public static Result Strip(Stream input, Stream output)
    {
        var removed = new List<string>();
        long removedBytes = 0;

        if (input.ReadByte() != 0xFF || input.ReadByte() != 0xD8)
            return Fail("no SOI marker: not a JPEG");

        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        while (true)
        {
            int lead = input.ReadByte();
            if (lead < 0) return Fail("end of file before SOS/EOI");
            if (lead != 0xFF) return Fail($"expected a marker, got 0x{lead:X2}");

            int marker = input.ReadByte();
            if (marker < 0) return Fail("end of file inside a marker");
            while (marker == 0xFF)      // 填充位元組：多個 0xFF 是合法的
            {
                marker = input.ReadByte();
                if (marker < 0) return Fail("end of file inside fill bytes");
            }

            // 無長度欄位的 marker：照抄
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                output.WriteByte(0xFF);
                output.WriteByte((byte)marker);
                continue;
            }

            if (marker == Eoi || marker == Sos)
            {
                // SOS 的標頭與 EOI 都是要保留的；SOS 之後（掃描資料）或 EOI 之後（尾端）
                // 一律整段照抄、一個位元組都不解讀。
                if (marker == Sos)
                {
                    (int length, byte[] payload, string? error) = ReadSegment(input);
                    if (error != null) return Fail(error);
                    output.WriteByte(0xFF);
                    output.WriteByte((byte)marker);
                    output.WriteByte((byte)(length >> 8));
                    output.WriteByte((byte)(length & 0xFF));
                    output.Write(payload, 0, payload.Length);
                }
                else
                {
                    output.WriteByte(0xFF);
                    output.WriteByte((byte)marker);
                }

                CopyRest(input, output);
                return new Result(true, marker == Sos, removed.Count, removedBytes,
                    Summarize(removed),
                    $"{removed.Count} metadata segment(s), {removedBytes}B removed, " +
                    $"entropy-coded scan data copied verbatim (pixels are byte-identical)");
            }

            (int len, byte[] body, string? bad) = ReadSegment(input);
            if (bad != null) return Fail(bad);

            if (IsMetadataMarker(marker))
            {
                removed.Add(NameOf(marker));
                removedBytes += 2 + len;      // marker(2) + 長度欄位本身也已讀掉
                continue;                      // ← 這裡就是「丟掉」：不寫進輸出
            }

            output.WriteByte(0xFF);
            output.WriteByte((byte)marker);
            output.WriteByte((byte)(len >> 8));
            output.WriteByte((byte)(len & 0xFF));
            output.Write(body, 0, body.Length);
        }
    }

    /// <summary>讀一個「有長度欄位」的區段。<paramref name="error"/> 非 null 時不得使用其他回傳值。</summary>
    private static (int Length, byte[] Body, string? Error) ReadSegment(Stream input)
    {
        int hi = input.ReadByte(), lo = input.ReadByte();
        if (hi < 0 || lo < 0) return (0, Array.Empty<byte>(), "end of file inside a segment length");
        int length = (hi << 8) | lo;
        if (length < 2) return (0, Array.Empty<byte>(), $"illegal segment length {length}");

        int bodyLength = length - 2;
        var body = new byte[bodyLength];
        int read = 0;
        while (read < bodyLength)
        {
            int n = input.Read(body, read, bodyLength - read);
            if (n <= 0) return (0, Array.Empty<byte>(), "end of file inside a segment payload");
            read += n;
        }
        return (length, body, null);
    }

    private static void CopyRest(Stream input, Stream output)
    {
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
    }

    private static string Summarize(List<string> removed)
    {
        if (removed.Count == 0) return "(none)";
        var counts = new List<string>();
        foreach (string name in removed.Distinct())
            counts.Add($"{name}x{removed.Count(r => r == name)}");
        return string.Join(",", counts);
    }

    private static Result Fail(string detail) => new(false, false, 0, 0, "(none)", detail);
}
