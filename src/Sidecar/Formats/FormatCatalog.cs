namespace Sidecar;

/// <summary>
/// 真正的格式目錄——**唯一的格式知識查詢點**（規格 §9.1：UI 不得自行判斷副檔名）。
///
/// 所有答案都來自 <see cref="CodecRegistry"/> 的**實測能力**，不是寫死的清單：
/// 可輸出的目標＝「這台機器上真的編碼成功的格式」－原格式。
/// 做不到的格式不出現扇區，而不是出現之後才失敗（規格 §7.2 禁止靜默失敗）。
/// </summary>
internal sealed class FormatCatalog : IFormatCatalog
{
    /// <summary>MVP 可轉換的類別（規格 §4.4：圖片優先）。其他類別一律視為不支援。</summary>
    private static readonly string[] ConvertibleCategories = { "IMAGE" };

    private readonly CodecRegistry _codecs;

    public FormatCatalog(CodecRegistry codecs) => _codecs = codecs;

    /// <summary>背後的實測能力表（診斷與交叉驗證用）。</summary>
    public CodecRegistry Codecs => _codecs;

    public string? SourceFormat(IReadOnlyList<string> paths) => Classify(paths).SourceFormat;

    /// <summary>
    /// 規格 §4.3：**同類別**才給一組選項；不同類別＝混合選取（拒絕）。
    /// 因此 [jpg, pdf] 是 <see cref="PayloadStatus.Mixed"/>（「請選同類別」），
    /// 而 [pdf] 單獨一個才是 <see cref="PayloadStatus.Unsupported"/>（「不支援的格式」）——
    /// 這是兩種不同的使用者訊息，不可以混為一談。
    /// </summary>
    public PayloadVerdict Classify(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return PayloadVerdict.Empty;

        string? category = null;
        string? firstFormat = null;
        foreach (string path in paths)
        {
            string? format = _codecs.FormatOfPath(path);
            string cat;
            if (format == null)
            {
                // 不認得的副檔名：**每一個各自成為一個類別**。
                // 若把它們全摺成同一個「未知」類別，[jpg, pdf] 會被判成同類別而不是混合選取
                // ——那是兩種不同的使用者訊息（「請選同類別」vs「不支援的格式」），
                // 而且 M1b 已經修過同一個缺陷，不可以再犯一次。
                cat = "EXT:" + UnknownKey(path);
            }
            else if (_codecs.Capability(format) is { Decode: DecodeSupport.No })
            {
                // 表中已知、但實測證明**無法解碼**的格式：同格式仍算同類別，但永遠不會是 IMAGE
                cat = "UNDECODABLE:" + format;
            }
            else
            {
                cat = _codecs.CategoryOf(format) ?? "EXT:" + format;
            }

            if (category == null) { category = cat; firstFormat = format; }
            else if (!string.Equals(category, cat, StringComparison.Ordinal))
                return new PayloadVerdict(PayloadStatus.Mixed, null);
        }

        return ConvertibleCategories.Contains(category!, StringComparer.Ordinal)
            ? new PayloadVerdict(PayloadStatus.Ready, firstFormat)
            : new PayloadVerdict(PayloadStatus.Unsupported, null);
    }

    /// <summary>不認得的副檔名 → 自己的類別鍵（小寫、不含點）。</summary>
    private static string UnknownKey(string path)
    {
        string ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? "(none)" : ext.TrimStart('.').ToLowerInvariant();
    }

    /// <summary>
    /// 規格 §4.3：扇區＝所有**可輸出**格式排除來源。這條不變式屬於本介面的契約，
    /// 因此實作必須自己遵守——**不得把責任推回 UI**。
    /// </summary>
    public IReadOnlyList<string> TargetsFor(string? sourceFormat)
    {
        IReadOnlyList<string> encodable = _codecs.EncodableFormats;
        if (string.IsNullOrEmpty(sourceFormat)) return encodable;

        var list = new List<string>(encodable.Count);
        foreach (string format in encodable)
            if (!string.Equals(format, sourceFormat, StringComparison.OrdinalIgnoreCase)) list.Add(format);
        return list;
    }

    /// <summary>目標格式的輸出副檔名（小寫、不含點；規格 §5 的檔案契約會用到）。</summary>
    public string ExtensionOf(string targetFormat) =>
        _codecs.PrimaryExtension(targetFormat) ?? targetFormat.ToLowerInvariant();
}
