namespace Sidecar;

/// <summary>轉檔失敗的分類。每一類對應規格 §7.1 的一個使用者訊息——**訊息由這一層決定，UI 不得自己編字串**。</summary>
internal enum ConversionError
{
    None,
    Unsupported,
    DecodeFailed,
    TargetUnavailable,
    SourceMissing,
    Unreadable,
    WriteDenied,
    NoSpace,
    Locked,
    Timeout,
    Unknown,

    /// <summary>
    /// 工具 `compress`（規格 §9.4 的第三顆扇形）專用：「壓縮到」指定的上限**做不到**——
    /// 已達本產品認可的最低品質（<see cref="CompressionPolicy.SmallQuality"/>）仍然超過上限。
    ///
    /// 為什麼要有它而不是隨便挑一個既有分類：這個情況**不是**失敗於環境（權限、空間、鎖定），
    /// 而是失敗於**使用者的要求本身**。使用者訊息若不說出這一件事，他看到的會是「轉檔失敗」，
    /// 然後一直重試同一個做不到的上限。規格 §7.1 的精神就是「原因要說得出來」。
    /// </summary>
    TargetTooSmall,

    /// <summary>
    /// 工具 `crop`（M3 第四顆扇形）專用：選取的矩形**是空的**（換算後寬或高為 0）。
    ///
    /// 為什麼是拒絕而不是夾成 1 px：一個「零面積」的矩形沒有任何區塊可以指認，
    /// 夾成 1 px 是**替使用者發明一個他沒有選的區域**（還可能是畫面外的 1 px）。
    /// 相對地「不到 1 px 但大於 0」是真的指了一小塊，那一種才夾成 1 px（見 <see cref="CropMath"/>）。
    /// 兩者的界線就畫在「面積是不是 0」，這一條不是口味問題。
    /// </summary>
    CropRectEmpty,

    /// <summary>
    /// 工具 `crop` 專用：選取的矩形（在**這一張圖的尺寸上**）**等於整張圖**。
    ///
    /// 為什麼是拒絕而不是「原樣輸出」：本產品的寫出是「解碼 → 處理 → **重新編碼**」。
    /// 全圖裁切是**可證明沒有任何處理**的一步，對 JPEG 卻是**一次有損的重新編碼**——
    /// 付了畫質、換到一個位元組不同的同名檔案。用一個會損失畫質的動作去執行一個「什麼都沒做」
    /// 的請求，是這個產品最不該做的事。所以**大聲拒絕**：使用者拖到全圖時會看到
    /// 「選取範圍等於整張圖」，而不是拿到一個看起來一樣、其實已經重壓過的檔案。
    /// （⚠ 對照組：`strip-metadata` 對「沒有 metadata 的圖」是照樣輸出並在日誌說明，
    /// 因為那一條路是**逐位元組相同**的，代價是 0。差別在這裡。）
    /// </summary>
    CropNothingToDo,

    /// <summary>
    /// 寫入時「這個名字剛剛被**另一個行程**拿走」，而且重試用完。
    ///
    /// 為什麼不沿用 <see cref="Unreadable"/>（`Unreadable` 是它今天的歸類）：那句話是
    /// 「無法讀取**來源**檔案」，但來源好好的——真正發生的事是兩個行程（兩支 `--convert`、
    /// 或使用者自己開著的 Confern 與同時跑的命令列）選到了**同一個輸出檔名**，
    /// 而 <c>File.Move(overwrite: false)</c> 讓後到的那一方失敗（`ERROR_ALREADY_EXISTS`）。
    /// 分類錯的訊息會把使用者送去檢查一個沒有問題的檔案（2026-10-06 量到：40/40 全失敗、
    /// 每一行都寫「無法讀取來源檔案」，而來源一個位元都沒變）。
    /// </summary>
    WriteConflict,

    /// <summary>
    /// 工具 `read-qr`（讀 QR，M3 第五顆扇形）專用：圖**讀得動、但裡面沒有 QR**——
    /// 來源檔完好、解碼成功、只是每一輪都在畫面上找不到 QR 碼。
    ///
    /// **為什麼既有分類沒有一個合用**（逐個對過）：
    ///   • <see cref="DecodeFailed"/>——它的規格訊息是「**檔名** 無法解碼」，說的是「這個檔案壞了」。
    ///     這裡的檔案一點都沒壞，說那句話等於把使用者送去檢查一個完好的檔案，
    ///     正是 <see cref="WriteConflict"/> 那一條註解記下來的同一種錯。
    ///   • <see cref="Unsupported"/>——格式在支援表裡，decoder 也真的解開了。
    ///   • <see cref="CropRectEmpty"/> 那一族——它們是「你的要求本身沒有東西可以做」，
    ///     而這裡的要求（在這張圖上找 QR）完全可以做，只是結果是「沒有」。
    ///
    /// **為什麼「沒有」必須是一個回報而不是失敗的例外**：找不到 QR 是這個工具最常見的正常結果
    /// （使用者拿錯圖、截圖裡沒有碼）。它必須是一句可讀的訊息，而且**不產生任何檔案**——
    /// 產生一個空檔案會讓使用者以為「讀到了什麼」。
    ///
    /// ⚠ 這一條**不是**失敗於環境，也**不是**失敗於使用者的要求；它是「查詢型工具的否定答案」。
    /// 這也是唯一一個**成功執行但 `Ok == false`** 的分類：`read-qr` 的結果是文字，
    /// 沒有文字就沒有東西可以回，所以它走既有的失敗回報路徑（訊息 ＋ 不寫檔）而不是另開一條。
    /// </summary>
    QrNotFound
}

/// <summary>單一檔案的轉檔結果。</summary>
/// <param name="QrText">
/// **讀 QR 的產物是文字，不是檔案**（工具 id `read-qr`；2026-10-07 M3 第五顆扇形）。
/// 這是**新增的欄位**，不是新增的通道——理由與 <c>crop</c> 當年新增五個協議欄位逐條相同：
///
///   • 既有的結果通道（<see cref="ConversionResult"/> ＋ 規格 §9.1 的 <c>result</c> 封包）已經
///     是「一個檔案進去、一個結果出來」的唯一路徑，失敗分類、使用者訊息、日誌、
///     跨管道回報全部掛在它上面。另開一條「文字通道」等於讓第二個擁有者去重寫這四件事。
///   • 缺席語意不會變成靜默錯誤：`read-qr` 之外的每一個 op 都**不讀這個欄位**，
///     所以它對既有四個工具的行為是**零**（建構子尾端多一個可選參數，既有呼叫一字未改）。
///   • <c>null</c> ＝「這個結果沒有文字」：成功時只有 `read-qr` 會給值；失敗時一律 <c>null</c>
///     （包含 <see cref="ConversionError.QrNotFound"/>——**沒有文字就是沒有文字，不填空字串**，
///     否則「找不到」與「找到空字串」在呼叫端會長得一樣）。
///
/// ⚠ 它與 <see cref="OutputPath"/> 的關係是**互斥的**：`read-qr` 不寫任何檔案，
/// 所以它回的結果 <see cref="OutputPath"/> 必定是 <c>null</c>、<see cref="QrText"/> 才有值。
/// 兩者同時有值＝有東西壞了，不是一種可以接受的組合。
/// </param>
internal sealed record ConversionResult(string SourcePath, string? OutputPath, ConversionError Error,
                                        string Detail, string? QrText = null)
{
    public bool Ok => Error == ConversionError.None;

    /// <summary>
    /// **規格 §7.1 的原因（繁體中文），不含檔名**；成功時為空字串。
    ///
    /// 這是原因的**單一擁有者**：<see cref="UserMessage"/> 由它加上檔名組出來
    /// （見 <see cref="DescribeMessage"/>），失敗清單（§7.3）直接拿它當「原因」那一欄。
    /// 拆開的理由是畫面需求不同：藥丸那一行要「檔名 ＋ 原因」連著讀（`broken.png 無法解碼`），
    /// 而清單是**檔名一欄、原因一欄**——若原因裡再夾一次檔名，每一列就會出現兩次檔名。
    /// </summary>
    public string Reason => Error switch
    {
        ConversionError.None => "",
        ConversionError.Unsupported => "不支援的格式",
        ConversionError.DecodeFailed => "無法解碼",
        ConversionError.TargetUnavailable => "此格式目前無法輸出",
        ConversionError.SourceMissing => "來源檔案已不存在",
        ConversionError.Unreadable => "無法讀取來源檔案",
        ConversionError.WriteDenied => "無法寫入此資料夾",
        ConversionError.NoSpace => "空間不足",
        ConversionError.Locked => "檔案被鎖定",
        ConversionError.Timeout => "雲端檔案下載逾時",
        // 工具 compress 專用（規格 §9.4）。說的是「你的上限太低」，不是「轉檔壞了」。
        ConversionError.TargetTooSmall => "已達最低品質仍超過指定大小",
        // 工具 crop 專用（規格 §9.4）。同樣是「你的要求本身沒有東西可以做」，不是「轉檔壞了」。
        ConversionError.CropRectEmpty => "選取範圍是空的",
        ConversionError.CropNothingToDo => "選取範圍等於整張圖",
        // 另一個行程搶走了每一個候選檔名（含 `name (1)`…`name (7)`）。罕見到幾乎不會出現，
        // 但出現時必須說得出真正的原因，不能叫使用者去檢查一個完好的來源檔。
        ConversionError.WriteConflict => "輸出檔名被其他行程占用",
        // 工具 read-qr 專用（規格 §9.4）。說的是「這張圖裡沒有 QR」，不是「這張圖壞了」
        // （見 QrNotFound 的逐條比較）。
        ConversionError.QrNotFound => "圖片中找不到 QR 碼",
        _ => "轉檔失敗"
    };

    /// <summary>規格 §7.1 的使用者訊息（繁體中文），＝檔名 ＋ <see cref="Reason"/>。</summary>
    public string UserMessage => DescribeMessage(Error, SourcePath, OutputPath, Reason);

    /// <summary>
    /// 規格 §7.1 訊息的**唯一組裝處**：檔名 ＋ 原因。原因由呼叫端的 <see cref="Reason"/> 帶進來，
    /// 所以同一個原因不可能在兩個地方各有一份字串（把原因再抄一份進這個方法就是那個陷阱）。
    ///
    /// ⚠ **這一組訊息是規格條文，不是排版**：`m1d`／`m1f` 直接斷言它們（＝現成的回歸網），
    /// 任何一個字元變動都會讓那些套件轉紅。要改訊息，先改規格。
    /// </summary>
    internal static string DescribeMessage(ConversionError error, string sourcePath, string? outputPath, string reason)
    {
        if (error == ConversionError.None) return $"✓ {FileNameOf(outputPath ?? sourcePath)}";
        // 只有 DecodeFailed 的規格訊息帶檔名（先有「哪個檔案」才讀得懂「無法解碼」）；
        // 其餘是純原因，使用者在清單那一列本來就看得到檔名。
        return error == ConversionError.DecodeFailed
            ? $"{FileNameOf(sourcePath)} {reason}"
            : reason;
    }

    /// <summary>取檔名。空路徑或尾端斜線不拋例外（來源可能是使用者亂打的字串）。</summary>
    private static string FileNameOf(string? path) => ProgressDetailRows.FileNameOf(path);
}

/// <summary>
/// 失敗清單的**一列**（規格 §7.3：`檔名 ＋ 原因`）。
///
/// 列的資料只有一個擁有者：這裡。繪製端（<c>ProgressPresenter.ComposeDetail</c>）拿到的是
/// 已經拆好的兩欄，**不得**自己再從 `UserMessage` 切字串或拼字串——兩欄的界線是規格訊息
/// 的結構（`broken.png 無法解碼`），用字串比對去猜界線會在訊息一改就默默錯位。
/// </summary>
internal sealed record ConversionFailureRow(string FileName, string Reason)
{
    /// <summary>畫面上實際顯示的那一行（給 dump 的日誌與未來的繪製端共用同一種寫法）。</summary>
    public string Text => Reason.Length == 0 ? FileName : $"{FileName} {Reason}";
}

/// <summary>整批的摘要（規格 §7.3：`✓ 8 / ✗ 2`，清單不自動彈出）。</summary>
internal sealed record ConversionSummary(IReadOnlyList<ConversionResult> Results)
{
    public int Ok => Results.Count(r => r.Ok);
    public int Failed => Results.Count - Ok;
    public bool AllOk => Failed == 0;

    /// <summary>
    /// 完成後那一行的**唯一規則**（畫面＝輪盤收起處）。兩個呼叫端共用它，
    /// 不允許任何地方再自己拼一次：
    ///   全成功   → `✓ N`
    ///   全失敗   → 規格 §7.1 的訊息（例：`broken.png 無法解碼`）——原因一定要到畫面上，
    ///              只給 `✓ 0 / ✗ 1` 等於使用者看不到原因，踩到「不得靜默失敗」的紅線
    ///   部分失敗 → 規格 §7.3 的 `✓ N / ✗ M`
    ///              （清單本身是**使用者點擊後**才展開的另一條路：<see cref="FailureRows"/>）
    /// </summary>
    public static string ComposeLine(int ok, int failed, IReadOnlyList<string> failures)
    {
        if (failed == 0) return $"✓ {ok}";
        if (ok == 0 && failures.Count > 0)
            return failures.Count == 1 ? failures[0] : $"{failures[0]}（共 {failed} 個）";
        return $"✓ {ok} / ✗ {failed}";
    }

    public string Line => ComposeLine(Ok, Failed, Failures.Select(r => r.UserMessage).ToList());

    public IEnumerable<ConversionResult> Failures => Results.Where(r => !r.Ok);

    /// <summary>
    /// 失敗清單的列資料（規格 §7.3），順序＝轉檔順序。**畫面不得自己組這些字串**：
    /// 檔名取自規格訊息的第一欄，原因取自 <see cref="ConversionResult.Reason"/>。
    /// </summary>
    public IReadOnlyList<ConversionFailureRow> FailureRows =>
        Failures.Select(r => new ConversionFailureRow(
            ProgressDetailRows.FileNameOf(r.SourcePath), r.Reason)).ToList();
}

/// <summary>
/// 「檔名 ＋ 原因」這一組列資料的**共同推導處**（單一擁有者）。
///
/// 兩條路徑都需要它，而它們的輸入不同：
///   • <see cref="ConversionSummary.FailureRows"/>——無頭路徑（`--convert`）手上是完整的
///     <see cref="ConversionResult"/>（檔名與原因分開）。
///   • <see cref="ConversionSnapshot.DetailRows"/>——UI 路徑跨越 worker 管道，只拿得到
///     規格 §7.1 的訊息（`broken.png 無法解碼`）與來源檔名，所以要在這裡把它切回兩欄。
///
/// 切成兩欄**只做一次**：`UserMessage` 是「檔名 空格 原因」組出來的（見
/// <see cref="ConversionResult.DescribeMessage"/>），所以去掉檔名前綴就是原因；
/// 認不出前綴時（例如管道另一端是別的版本）寧可整串當原因，也不要顯示出一個錯的檔名。
/// </summary>
internal static class ProgressDetailRows
{
    /// <summary>規格訊息裡「檔名」與「原因」之間的分隔字元（<see cref="ConversionResult.DescribeMessage"/> 用的就是空白）。</summary>
    private const string Separator = " ";

    internal static string FileNameOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFileName(path); }
        catch (ArgumentException) { return path; }
    }

    /// <summary>從規格 §7.1 的訊息與來源檔名推導出一列。檔名認不出來時，整串訊息當原因。</summary>
    internal static ConversionFailureRow FromMessage(string? sourcePath, string? message)
    {
        string fileName = FileNameOf(sourcePath);
        string text = message ?? "";
        if (fileName.Length == 0) return new ConversionFailureRow("", text);
        if (text.StartsWith(fileName + Separator, StringComparison.Ordinal))
        {
            string reason = text.Substring(fileName.Length + Separator.Length);
            if (reason.Length > 0) return new ConversionFailureRow(fileName, reason);
        }
        // 完全相等（訊息就是檔名本身）也當作「沒有原因」——不要捏造一個原因出來。
        if (text == fileName) return new ConversionFailureRow(fileName, "");
        return new ConversionFailureRow(fileName, text);
    }
}
