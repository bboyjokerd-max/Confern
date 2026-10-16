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

    /// <summary>
    /// **有工具適用**的 FORMAT（不是類別）——與「可轉成圖片」是**兩個不同的判定**
    /// （2026-10-07，切片 B 補）。
    ///
    /// 為什麼非分開不可（這是**量出來的**，不是推論）：在這一條存在之前，一份 PDF 的輪盤路徑是
    /// <code>
    /// wheel from OLE payload: files=3 status=Unsupported source=UNKNOWN targets=0 notice=不支援的格式 tools=0
    /// wheel-tool 'merge-pdf': not in the catalog []
    /// </code>
    /// 也就是說 <see cref="ToolCatalog.ToolsFor"/> 因為 `Status != Ready` 回空清單，於是
    /// **輪盤上選不到 merge-pdf，那個工具的提交分支成了死碼**——工具做完了但使用者按不到。
    ///
    /// ⚠ 比對的是**格式名**（`CodecRegistry.FormatOfPath` 的回傳值，例如 `"PDF"`），
    /// 不是 `Classify` 內部那個類別鍵。原因是量到的：PDF 的類別鍵是
    /// `"UNDECODABLE:PDF"`（因為 `DeclarePdf` 宣告 `Decode: No`），拿 `"PDF"` 去比類別鍵
    /// 會**永遠不成立**，症狀就是這條修正在第一版看起來「沒生效」
    /// （`--tools-list --wheel-payload <3 pdf>` 仍然回 `count=0 status=Unsupported`）。
    ///
    /// 兩個判定的差別：
    ///  ①**可轉成圖片**（類別 `"IMAGE"`）：第一層格式扇形會出現，且 `--convert` 會收下它。
    ///    PDF 是這一類的**反面**，所以 `--convert` 仍然拒絕 PDF、第一層格式扇形仍然不出現。
    ///  ②**有工具適用**（`"PDF"`）：**不給第一層格式扇形**（沒有可轉的目標），但把工具層打開，
    ///    讓 `merge-pdf` 這一顆扇形真的畫得出來、選得到、按下去真的走那條提交路徑。
    ///
    /// 為什麼要留這張表、而不是「有工具就放行」：一個**目錄認識、卻連解碼器都裝不起來**的格式
    /// （例如 `HEIC` 在沒有解碼器的機器上）也會「有工具適用」，但那些工具的每一次執行都會失敗。
    /// 本專案的原則是「畫面上有的東西必須是真的」（見 <see cref="IToolCatalog"/>），
    /// 所以這裡明列**確定做得到事**的格式，不靠推論。
    ///
    /// ⚠ 這一條**不會**把 PDF 變成可轉換的格式：`--convert &lt;pdf&gt;` 的拒絕語意、
    /// `MvpInputs`／`MvpOutputs` 與規格 §6.1／§6.2 都不動。使用者凍結的是輪盤的**視覺**
    /// （`WheelDesign`／方向 D 色票／7px 幾何），不是「哪些扇形該出現」——
    /// 讀 QR 與 merge-pdf 的扇形本來就是這兩刀加上去的。
    /// </summary>
    private static readonly string[] ToolFormats = { "PDF" };

    private readonly CodecRegistry _codecs;

    public FormatCatalog(CodecRegistry codecs) => _codecs = codecs;

    /// <summary>背後的實測能力表（診斷與交叉驗證用）。</summary>
    public CodecRegistry Codecs => _codecs;

    public string? SourceFormat(IReadOnlyList<string> paths) => Classify(paths).SourceFormat;

    /// <summary>
    /// 規格 §4.3：**同類別**才給一組選項；不同類別＝混合選取（拒絕）。
    ///
    /// `[pdf, pdf]` ⇒ <see cref="PayloadStatus.Ready"/>（PDF 有工具適用，輪盤開工具層）——
    /// **與「可以轉成圖片」是兩件事**，見 <see cref="ToolFormats"/>。
    /// `[jpg, pdf]` ⇒ <see cref="PayloadStatus.Mixed"/>（「請選同類別」）：圖片要轉、PDF 要合併，
    /// 兩者放同一批沒有共同的可執行語意，所以仍然拒絕，而且仍然不給任何工具。
    /// 一個連 <see cref="CodecRegistry"/> 都不認識的類別才是不支援。
    /// </summary>
    public PayloadVerdict Classify(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return PayloadVerdict.Empty;

        string? category = null;
        string? firstFormat = null;
        foreach (string path in paths)
        {
            string? format = _codecs.FormatOfPath(path);
            string cat = CategoryKeyOf(path);

            if (category == null) { category = cat; firstFormat = format; }
            else if (!string.Equals(category, cat, StringComparison.Ordinal))
                return new PayloadVerdict(PayloadStatus.Mixed, null, paths.Count, false);
        }

        // 判定 ①：可以轉成圖片 ⇒ Ready，而且第一層格式扇形會出現。
        if (ConvertibleCategories.Contains(category!, StringComparer.Ordinal))
            return new PayloadVerdict(PayloadStatus.Ready, firstFormat, paths.Count, CanConvert: true);

        // 判定 ②：不能轉成圖片，但這個格式**有工具適用** ⇒ 也 Ready（`SourceFormat` 必須填，
        // 否則 `ToolsFor` 無從判斷適用性），而且**不給第一層格式扇形**——呼叫端看到
        // `TargetsFor` 回空清單就知道「這一層沒有東西可選」，於是把工具層打開。
        // 適用性的判定仍然**只有一個來源**：`ToolDescriptor.AppliesTo`；
        // 這裡問的是工具目錄本身（`Tools`），不是自己再寫一份「PDF 有什麼工具」。
        if (!string.IsNullOrEmpty(firstFormat)
            && ToolFormats.Contains(firstFormat, StringComparer.OrdinalIgnoreCase)
            && HasApplicableTool(firstFormat))
            return new PayloadVerdict(PayloadStatus.Ready, firstFormat, paths.Count, CanConvert: false);

        return new PayloadVerdict(PayloadStatus.Unsupported, null, paths.Count, false);
    }

    /// <summary>
    /// 一個檔案的**類別鍵**（規格 §4.3）：同一個鍵 = 同一類別 = 可以放在同一批。
    ///
    /// ⚠ **只有這一份**：<see cref="Classify"/> 用它判定，<see cref="DescribeMix"/> 用它解釋
    /// 「是哪個檔在亂」。兩者若各有一份，就會出現「判定說混合、說明卻指著同類別的檔」這種
    /// 自相矛盾的訊息——而使用者只會看到後面那一句。
    /// </summary>
    private string CategoryKeyOf(string path)
    {
        string? format = _codecs.FormatOfPath(path);
        if (format == null)
        {
            // 不認得的副檔名：**每一個各自成為一個類別**。
            // 若把它們全摺成同一個「未知」類別，[jpg, pdf] 會被判成同類別而不是混合選取
            // ——那是兩種不同的使用者訊息（「請選同類別」vs「不支援的格式」），
            // 而且 M1b 已經修過同一個缺陷，不可以再犯一次。
            return "EXT:" + UnknownKey(path);
        }
        if (_codecs.Capability(format) is { Decode: DecodeSupport.No })
        {
            // 表中已知、但實測證明**無法解碼**的格式：同格式仍算同類別，但永遠不會是 IMAGE
            return "UNDECODABLE:" + format;
        }
        return _codecs.CategoryOf(format) ?? "EXT:" + format;
    }

    /// <summary>
    /// 「混合選取」時，說出**是哪個檔在亂**：`PNG ×3、PDF ×1（例如 scan1.pdf）`。
    ///
    /// 為什麼需要它：使用者 2026-10-15 拖一個裝著掃描圖的資料夾做合併，隔天同一個資料夾再拖一次，
    /// 看到的是「混合選取，請選同類別」——**而那個資料夾裡多出來的 PDF 是我們自己上一次的產物**。
    /// 判定本身沒有錯（一個資料夾同時有圖片與 PDF 本來就該拒絕），錯的是**它沒說是誰**。
    ///
    /// ⚠ 這裡**只解釋，不判定**：回傳 <c>null</c> 代表「沒有什麼好說的」（不是混合選取）。
    /// **不得**拿這個結果去改變呼叫端的判定。
    /// </summary>
    public string? DescribeMix(IReadOnlyList<string> paths)
    {
        if (paths.Count < 2) return null;

        var order = new List<string>();                                  // 類別，依**首次出現**的順序
        var names = new Dictionary<string, string>(StringComparer.Ordinal);   // 類別鍵 -> 顯示名
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sample = new Dictionary<string, string>(StringComparer.Ordinal);  // 類別鍵 -> 該類別的第一個檔

        foreach (string path in paths)
        {
            string cat = CategoryKeyOf(path);
            if (!counts.ContainsKey(cat))
            {
                order.Add(cat);
                names[cat] = DisplayFormatOf(path);
                sample[cat] = path;
                counts[cat] = 0;
            }
            counts[cat]++;
        }
        if (order.Count < 2) return null;                                // 同類別：沒有混合

        var parts = new List<string>();
        foreach (string cat in order.Take(3))
            parts.Add($"{names[cat]} ×{counts[cat]}");
        if (order.Count > 3) parts.Add($"等 {order.Count} 種");

        // 「是哪個檔在亂」＝**最少數**那一類（同數時取最後出現的，也就是新來的那個）。
        string rarest = order[0];
        foreach (string cat in order)
            if (counts[cat] <= counts[rarest]) rarest = cat;

        // 回傳**整句話**：這樣「混合選取要怎麼講」只有這一個地方決定。原本輪盤與 --tools-list
        // 各抄了一份「混合選取，請選同類別」，兩份就會各自演化（而使用者只會看到其中一句）。
        return "混合選取，請選同類別：" + string.Join("、", parts)
             + $"（例如 {Path.GetFileName(sample[rarest])}）";
    }

    /// <summary>類別要顯示給人看的名字：認得的格式用格式名（`PNG`），不認得的用副檔名。</summary>
    private string DisplayFormatOf(string path)
    {
        string? format = _codecs.FormatOfPath(path);
        return format ?? Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
    }

    /// <summary>
    /// 這個來源格式有沒有**任何**工具適用。問的是 <see cref="ToolCatalog"/> 自己的判定
    /// （`ToolsFor` → `ToolDescriptor.AppliesTo`），不是這裡的一張表。
    /// </summary>
    private static bool HasApplicableTool(string format) =>
                // FileCount 填 1：這一問只發生在判定②（`ToolFormats`＝PDF），而 merge-pdf 對 PDF
        // 的分支只看格式、不看張數，所以這個數字在這裡不影響答案；但它不可以留 0 假裝
        // 「沒有檔案」——那會讓「≥2 張」那條新規則在未來被誤用到這一條路上。
        new ToolCatalog().ToolsFor(new PayloadVerdict(PayloadStatus.Ready, format, 1, false)).Count > 0;

    /// <summary>不認得的副檔名 → 自己的類別鍵（小寫、不含點）。</summary>
    private static string UnknownKey(string path)
    {
        string ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? "(none)" : ext.TrimStart('.').ToLowerInvariant();
    }

    /// <summary>
    /// 規格 §4.3：扇區＝所有**實際提供**的輸出格式排除來源。這條不變式屬於本介面的契約，
    /// 因此實作必須自己遵守——**不得把責任推回 UI**。
    ///
    /// 「實際提供」＝規格宣告的 ∩ 實測可用的（見 <see cref="CodecRegistry.OfferedOutputs"/>），
    /// 不是單看能力：GIF 編得出來但 §6.2 沒宣告，所以不出現。
    /// </summary>
    public IReadOnlyList<string> TargetsFor(string? sourceFormat)
    {
        IReadOnlyList<string> offered = _codecs.OfferedOutputs;
        if (string.IsNullOrEmpty(sourceFormat)) return offered;

        // ⚠ 2026-10-07（切片 B，選項 (b)）：**不能轉成圖片的來源格式，第一層一個扇區都不給。**
        //
        // 為什麼非擋不可（這是**量出來的**）：`Classify` 現在會讓 PDF 也到 Ready（它有一個真的
        // 做得成的工具），而呼叫端只有在 `TargetsFor` 回空清單時才知道「這一層沒有東西可選、
        // 該把工具層打開」。少了這一擋，一份 PDF 的輪盤會出現
        // `targets=[ROTATE,CLEAN,COMPRESS,CROP]` ——那四顆的語意會變成
        // 「把 PDF 轉成 PNG／JPG／…」，而 `--convert <pdf>` **做不到這件事**（PDF 不可解碼）。
        // 畫面上有一顆按下去只會失敗的扇形，正是本專案禁止的那種東西。
        // 實測（未擋之前）：`wheel from OLE payload: … source=PDF targets=4 notice=- layer=format`。
        //
        // 這個判定與 `Classify` 用的是同一份 `ConvertibleCategories`：**可轉成圖片的類別才有
        // 目標格式**。「有工具適用」是另一個判定，它開的是工具層，不是這一層。
        string category = _codecs.CategoryOf(sourceFormat) ?? "";
        if (!ConvertibleCategories.Contains(category, StringComparer.Ordinal))
            return Array.Empty<string>();

        var list = new List<string>(offered.Count);
        foreach (string format in offered)
            if (!string.Equals(format, sourceFormat, StringComparison.OrdinalIgnoreCase)) list.Add(format);
        return list;
    }

    /// <summary>目標格式的輸出副檔名（小寫、不含點；規格 §5 的檔案契約會用到）。</summary>
    public string ExtensionOf(string targetFormat) =>
        _codecs.PrimaryExtension(targetFormat) ?? targetFormat.ToLowerInvariant();
}
