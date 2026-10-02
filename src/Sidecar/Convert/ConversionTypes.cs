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
    Unknown
}

/// <summary>單一檔案的轉檔結果。</summary>
internal sealed record ConversionResult(string SourcePath, string? OutputPath, ConversionError Error, string Detail)
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
