using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidecar;

/// <summary>
/// UI ↔ Worker 的協定（規格 §9.1）：**具名管道 + 版本化 JSON**，一行一則訊息。
///
/// 關鍵性質：版本不合時 UI **重啟 worker**，永不崩潰；
/// 管道斷了也不能讓 UI 卡死（worker 死了要把剩下的檔案標成失敗，不是永遠等下去）。
/// </summary>
internal static class WorkerProtocol
{
    /// <summary>
    /// 協議版本。**2026-10-05 由 1 升到 2**：新增 <c>op</c> 欄位（工作種類）。
    ///
    /// 為什麼非升不可：`op` 缺席時的語意是 `convert`（見 <see cref="WorkerMessage.op"/>），
    /// 所以一個**舊 worker** 收到帶 `op=rotate90` 的封包時，會把它當成一般的格式轉換照做——
    /// 使用者按了「轉 90 度」卻得到一個換格式的檔案，而且沒有任何錯誤。升版本讓這種配對
    /// 在連線階段就被擋掉（<see cref="ConversionClient"/> 偵測到版本不符會重啟 worker），
    /// 而不是變成一個靜默的錯誤結果。
    ///
    /// **2026-10-06 由 2 升到 3**：新增 <c>outputDir</c> 欄位（輸出資料夾）。
    ///
    /// 為什麼非升不可：`outputDir` 缺席時的語意是「來源檔旁邊」（見
    /// <see cref="WorkerMessage.outputDir"/>），而這**正好是舊版的行為**——所以一個舊 worker
    /// 收到帶資料夾的封包時不會報錯，它只會**默默把檔案寫到來源旁邊**。那正是本欄位要修掉的事：
    /// 使用者設定了輸出資料夾、按下 Enter，卻什麼都沒發生，畫面上還顯示成功。缺席語意與新功能
    /// 的意圖在這裡剛好相反，靠預設值擋不住，只能靠版本配對在連線階段拒絕。
    ///
    /// **2026-10-06 新增 `op=strip-metadata`——刻意「不」由 3 升到 4。**
    ///
    /// 理由：升版本的用途是擋掉**靜默做錯事**的配對。`op` 缺席會落到 `convert`（舊 worker 會把
    /// 旋轉當成換格式照做，而且回報成功），所以那一次非升不可；`outputDir` 的缺席語意與新功能
    /// 意圖相反，所以那一次也非升不可。**但這個新 op 不屬於那一類**：一個還不認識
    /// <c>strip-metadata</c> 的舊 worker 會走到 <see cref="WorkerServer.RejectUnknownOp"/>，
    /// 回一個具名的失敗（`unknown op 'strip-metadata' (this worker speaks convert/rotate90)`），
    /// 使用者看到的是「這個工具在這個 worker 上做不到」而不是「按了 CLEAN 卻轉了格式」。
    /// 換句話說：**未知 op 已經有明確的失敗路徑，版本閘門在這裡是多的**，
    /// 而多升一次版本會讓「舊 exe 配新 exe」的升級情境無謂地多斷一次線。
    ///
    /// <summary>
    /// **2026-10-06 新增 `op=compress`（第三顆扇形）——同樣不升，理由逐條相同。**
    /// 另外檢查過它帶來的新欄位（<c>compressMode</c>／<c>compressTargetBytes</c>／
    /// <c>standardJpgQuality</c>／<c>standardWebpQuality</c>）：一個舊 worker **根本走不到**
    /// 讀它們的那一行，因為 `op=compress` 會在它身上變成 <see cref="WorkerServer.RejectUnknownOp"/>。
    /// 「欄位被忽略」這種靜默錯誤需要「op 被接受但欄位被丟掉」這個前提，而那個前提不成立。
    ///
    /// **2026-10-06 新增 `op=crop`（第四顆扇形）——同樣不升，理由逐條相同。**
    /// 它帶來五個新欄位（<c>cropX</c>／<c>cropY</c>／<c>cropW</c>／<c>cropH</c>／<c>cropRatio</c>），
    /// 五個都是 <c>null</c> 且 <c>WhenWritingNull</c>，所以**既有 op 的封包逐位元不變**；
    /// 而舊 worker 收到 `op=crop` 一樣走到 <see cref="WorkerServer.RejectUnknownOp"/>，
    /// 到不了讀那五個欄位的那一行。
    ///
    /// **2026-10-07 新增 `op=read-qr`（第五顆扇形，讀 QR）——同樣不升，理由逐條相同。**
    /// 它帶來**一個新的結果欄位**（<c>text</c>，見 <see cref="WorkerMessage.text"/>）而不是新的
    /// 請求欄位：缺席是 <c>null</c> 且 <c>WhenWritingNull</c>，所以既有的 <c>result</c> 封包
    /// **逐位元不變**；而一個不認識 `read-qr` 的舊 worker 走
    /// <see cref="WorkerServer.RejectUnknownOp"/>，根本到不了填那個欄位的那一行。
    /// 「欄位被忽略」這種靜默錯誤需要「op 被接受但欄位被丟掉」這個前提，而那個前提不成立。
    ///
    /// **2026-10-07 新增 `op=merge-pdf`（第六顆扇形，合併 PDF）——同樣不升，而且這次更強。**
    /// 它**沒有帶來任何新欄位**（順序就是既有的 `sourcePaths` 陣列本身），所以「欄位被忽略」
    /// 這個前提連成立都不可能；而一個不認識 `merge-pdf` 的舊 worker 一樣走
    /// <see cref="WorkerServer.RejectUnknownOp"/>，回一個具名的失敗。
    ///
    /// ⚠ 這個 op 與前六個有一個**結構上**的不同：它是**多個輸入、一個輸出**
    /// （見 <see cref="WorkerProtocol.OpMergePdf"/>），所以它不能被逐檔執行——
    /// 逐檔執行同一批路徑的症狀是「檔案被覆寫成同一個名字」或「產生 N 個合併檔」，
    /// 兩者都是靜默的錯。分派處因此讓它**只跑一次**。
    /// </summary>
    public const int Version = 3;

    /// <summary>
    /// `WorkerMessage.op` 的合法值：這件工作**要做什麼**。
    ///
    /// 放在 <see cref="WorkerProtocol"/> 而不是 <see cref="WorkerMessageTypes"/>：
    /// 後者是「封包是哪一種」（job／progress／result…），這裡是「工作種類」——兩套詞彙。
    /// 混在一個類別裡，下一個讀的人會以為 `op` 也是一種封包類型。
    /// </summary>
    public const string OpConvert = "convert";

    /// <summary>工具 `rotate90`（順時針 90 度，M3）。輸出格式＝來源格式，見 <see cref="ConversionEngine.Rotate90"/>。</summary>
    public const string OpRotate90 = "rotate90";

    /// <summary>
    /// 工具 `strip-metadata`（去 metadata，M3 第二個工具）。輸出格式＝來源格式，
    /// 見 <see cref="ConversionEngine.StripMetadata"/> 與規格 §9.4。
    /// ⚠ 新增這個 op **不升 <see cref="Version"/>**，理由寫在 <see cref="Version"/> 的註解裡。
    /// </summary>
    public const string OpStripMetadata = "strip-metadata";

    /// <summary>
    /// 工具 `compress`（壓縮，M3 第三顆扇形）。輸出格式＝來源格式，
    /// 等級由 <see cref="CompressionPolicy"/> 決定（設定頁是唯一來源），
    /// 見 <see cref="ConversionEngine.Compress"/> 與規格 §9.4。
    /// ⚠ 新增這個 op **不升 <see cref="Version"/>**，理由寫在 <see cref="Version"/> 的註解裡。
    /// </summary>
    public const string OpCompress = "compress";

    /// <summary>
    /// 工具 `crop`（裁切，M3 第四顆扇形）。輸出格式＝來源格式，矩形是一個**正規化**的
    /// 0..1 矩形（<see cref="WorkerMessage.cropX"/>…）＋一個比例預設（<see cref="WorkerMessage.cropRatio"/>），
    /// 每一張圖各自換算，見 <see cref="ConversionEngine.Crop"/> 與 <see cref="CropMath"/>。
    ///
    /// ⚠ 新增這個 op **不升 <see cref="Version"/>**，理由與前兩顆逐條相同：舊 worker 走
    /// <see cref="WorkerServer.RejectUnknownOp"/>，而且它**根本走不到**讀那五個新欄位的那一行。
    /// </summary>
    public const string OpCrop = "crop";

    /// <summary>
    /// 工具 `read-qr`（讀 QR，M3 第五顆扇形）。**這是唯一一個不產生檔案的 op**：
    /// 它的產物是文字，回在結果封包的 <see cref="WorkerMessage.text"/>，
    /// 見 <see cref="ConversionEngine.ReadQr"/> 與規格 §9.4。
    ///
    /// ⚠ 新增這個 op **不升 <see cref="Version"/>**，理由與前三顆逐條相同：舊 worker 走
    /// <see cref="WorkerServer.RejectUnknownOp"/>（它會回一個具名的失敗，而不是默默做別的事），
    /// 而且它**根本走不到**填 <c>text</c> 的那一行。
    /// </summary>
    public const string OpReadQr = "read-qr";

    /// <summary>
    /// 工具 `merge-pdf`（合併 PDF，M3 第六顆扇形）。**這是唯一一個「多個輸入、一個輸出」的 op**
    /// ——前六個都是「一個檔進去、一個結果出來」，所以 <see cref="WorkerServer.RunJob"/> 的逐檔
    /// 迴圈對它們成立；合併不是（見該方法的 `op=merge-pdf` 那一臂：它只跑**一次**，
    /// 由 <c>paths[0]</c> 觸發，而其餘路徑整批傳進引擎）。
    ///
    /// **`sourcePaths` 的陣列順序就是頁序**（`a;b` 與 `b;a` 是兩個不同的請求），
    /// 這是這個工具的定義而不是實作細節，所以在協議上沒有任何額外的「順序」欄位——
    /// 順序就是那個既有的欄位本身。
    ///
    /// ⚠ 新增這個 op **不升 <see cref="Version"/>**，理由與前三顆逐條相同：舊 worker 走
    /// <see cref="WorkerServer.RejectUnknownOp"/>（它會回一個具名的失敗，而不是默默做別的事），
    /// 而且它**根本走不到**那一臂。它也**沒有帶來任何新欄位**——`sourcePaths` 早就在協議上，
    /// 所以既有的封包逐位元不變，連「欄位被忽略」這種靜默錯誤的前提都不成立。
    /// </summary>
    public const string OpMergePdf = "merge-pdf";

    /// <summary>
    /// 工具 `remove-bg`（去背，M3 **第七顆**扇形，也是 M3 的最後一顆）。輸出是**與來源同尺寸**的
    /// PNG（背景變透明），見 <see cref="ConversionEngine.RemoveBg"/> 與規格 §9.4。
    ///
    /// ⚠ 它是**唯一一個需要外部資源檔的 op**：人像模型
    /// （<c>models\model_fp16.onnx</c>，apache-2.0、不進版控）不在出貨檔裡，
    /// 由 <c>tools/get-rembg-model.ps1</c> 取得。所以它帶來**一個新的請求欄位**
    /// （<see cref="WorkerMessage.removeBgModel"/>：「要去哪裡找那顆模型」）。
    ///
    /// ⚠ **新增這個 op 仍然不升 <see cref="Version"/>**，理由與前六顆逐條相同：
    /// 一個不認識 `remove-bg` 的舊 worker 走 <see cref="WorkerServer.RejectUnknownOp"/>
    /// （回一個具名的失敗，而不是默默做別的事），而且它**根本走不到**讀
    /// <c>removeBgModel</c> 的那一行；該欄位是 <c>string?</c> 且 <c>WhenWritingNull</c>，
    /// 所以既有 op 的封包**逐位元不變**。最後檢查過一次它有沒有「缺席語意與意圖相反」的問題：
    /// 欄位缺席＝用預設位置（執行檔旁邊的 `models\`），那**正是**不指定時該有的行為，
    /// 而不是「使用者設定了卻默默沒生效」（那才是 outputDir 當年非升版本不可的形狀）。
    /// </summary>
    public const string OpRemoveBg = "remove-bg";

    public static string NewPipeName() => ProductInfo.Slug + "-" + Guid.NewGuid().ToString("N");

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(WorkerMessage message) => JsonSerializer.Serialize(message, Json);

    /// <summary>讀不懂的行回 null（不拋）：壞訊息不該讓任何一邊死掉。</summary>
    public static WorkerMessage? Deserialize(string line)
    {
        try { return JsonSerializer.Deserialize<WorkerMessage>(line, Json); }
        catch { return null; }
    }
}

internal static class WorkerMessageTypes
{
    public const string Ready = "ready";         // worker → UI
    public const string Job = "job";             // UI → worker
    public const string Progress = "progress";   // worker → UI（原地更新用，不是逐行洗版）
    public const string Result = "result";       // worker → UI（單檔結果）
    public const string Summary = "summary";     // worker → UI（整批結束）
    public const string Error = "error";         // 任一邊 → 另一邊
    public const string Cancel = "cancel";       // UI → worker
    public const string Shutdown = "shutdown";   // UI → worker
}

internal static class WorkerErrorCodes
{
    public const string ProtocolMismatch = "ProtocolMismatch";
    public const string JobTimeout = "JobTimeout";
    public const string WorkerGone = "WorkerGone";
    public const string WorkerStartFailed = "WorkerStartFailed";
}

/// <summary>一行一則的訊息。欄位名稱刻意與規格 §9.1 一致（`jobId`／`sourcePaths`／`targetFormat`／`quality`）。</summary>
internal sealed class WorkerMessage
{
    public int protocolVersion { get; set; } = WorkerProtocol.Version;
    public string type { get; set; } = "";
    public int jobId { get; set; }
    public string[]? sourcePaths { get; set; }
    public string? targetFormat { get; set; }
    /// <summary>
    /// 工作種類（2026-10-05 M3 新增）。<see cref="WorkerProtocol.OpConvert"/>＝格式轉換，
    /// <see cref="WorkerProtocol.OpRotate90"/>＝工具旋轉。
    ///
    /// **null／空字串的語意是 `convert`**：那是「這個欄位還不存在的年代」的封包該有的意思，
    /// 不是猜測。但配對錯誤**不能靠這個預設值擋**——一個舊 worker 會把 rotate90 當成 convert
    /// 默默做掉，所以協議版本同時升到 2（見 <see cref="WorkerProtocol.Version"/>）。
    /// </summary>
    public string? op { get; set; }
    /// <summary>
    /// 輸出資料夾（2026-10-06 M4 新增），由 <see cref="Settings.ResolveOutputDirectory"/> 解析而來。
    ///
    /// **null／空字串＝寫在來源檔旁邊**：那是「這個欄位還不存在的年代」的封包該有的意思，
    /// 也正是 <see cref="ConversionEngine"/> 的 <c>targetDirectory</c> 預設值——worker 直接把它
    /// 原封不動傳下去，不在這裡再解析一次（解析規則只有 Settings 那一份）。
    ///
    /// 與 `op` 不同的是，這裡的缺席語意**與新功能的意圖相反**（缺席＝來源旁邊，而使用者要的是
    /// 別的資料夾），所以配對錯誤不可能靠預設值擋，必須靠協議版本（見
    /// <see cref="WorkerProtocol.Version"/>）。
    /// </summary>
    public string? outputDir { get; set; }

    /// <summary>
    /// 壓縮等級（2026-10-06 M3 第三顆扇形，<see cref="WorkerProtocol.OpCompress"/> 專用）。
    /// 正規值見 <see cref="CompressionPolicy.Modes"/>。
    ///
    /// **null／空字串的缺席語意 ＝ <see cref="CompressionPolicy.ModeStandard"/>**：那是
    /// 「這個欄位還不存在的年代」的封包該有的意思，也正是**這個工具出現之前的行為**
    /// （照既有的品質設定走）。與 <c>outputDir</c> 不同的是，這裡的缺席語意**與新功能的意圖
    /// 一致**（新功能的預設值就是標準），所以配對錯誤不會變成靜默做錯事——而一個不認識
    /// `op=compress` 的舊 worker 本來就會走到 <see cref="WorkerServer.RejectUnknownOp"/>。
    /// </summary>
    public string? compressMode { get; set; }

    /// <summary>
    /// 「壓縮到」的上限（**位元組**，2026-10-06）。<c>0</c>＝沒有上限。
    /// 由 UI 端的 <see cref="Settings.ToCompressionPolicy"/> 換算（設定檔存的是 KB），
    /// worker 不再自己換算一次——單位換算只有一個擁有者。
    /// </summary>
    public long? compressTargetBytes { get; set; }

    /// <summary>
    /// 標準模式要用的 JPG／WebP 品質（2026-10-06）。**與 <c>quality</c> 刻意分開**：
    /// <c>quality</c> 是「這一次工作指定的品質」（格式轉換就是用它），而這兩個是
    /// 「設定檔裡的品質設定」，壓縮工具的標準模式要的是後者，且**逐檔依格式選**
    /// （一批裡可能同時有 JPG 與 PNG，JPG 用 jpg 品質、其餘品質無意義）。
    ///
    /// ⚠ 刻意是 <c>int?</c> 而不是 <c>int</c>：值型別的 0 會被序列化進每一個封包，
    /// 那會讓**既有 op 的訊息多出兩個欄位**（`quality` 那一組是逐位元比對的）。
    /// 四個壓縮欄位一起出現、一起不出現——半套的政策比沒有政策危險。
    /// null／超出 60–100 ＝ 沒有這個值 → 用規格預設 85。
    /// </summary>
    public int? standardJpgQuality { get; set; }
    public int? standardWebpQuality { get; set; }

    /// <summary>
    /// 裁切矩形（**正規化 0..1**，2026-10-06 M3 第四顆扇形，<see cref="WorkerProtocol.OpCrop"/> 專用）。
    ///
    /// 為什麼是正規化而不是像素：使用者 2026-10-06 拍板「批次＝同一比例套用到每一張」。
    /// 像素座標只能在一個尺寸上有意義，送像素等於把「用第一張的尺寸套到每一張」寫進協議。
    /// 每一個檔各自乘上自己的尺寸（<see cref="CropMath.TryToPixels"/>）。
    ///
    /// ⚠ 五個欄位**一起出現、一起不出現**（與壓縮那一組同一個規矩）：null 一律不進訊息
    /// （<c>WhenWritingNull</c>），所以既有的 job 封包逐位元不變。其中四個是 <c>double?</c>
    /// 而不是 <c>double</c>：值型別的 0 會被序列化進**每一個**封包（<c>x=0</c> 是完全合法的矩形左緣），
    /// 那會讓既有的封包多出四個欄位——而「既有封包逐位元不變」是這一顆扇形唯一的相容性契約。
    /// </summary>
    public double? cropX { get; set; }
    public double? cropY { get; set; }
    public double? cropW { get; set; }
    public double? cropH { get; set; }

    /// <summary>
    /// 比例預設 id（2026-10-06，<see cref="WorkerProtocol.OpCrop"/> 專用）。正規值見
    /// <see cref="CropRatios"/>；**null／空／認不得＝<see cref="CropRatios.IdFree"/>（自由）**，
    /// 那是「這個欄位還不存在的年代」的封包該有的意思，也正是**不做比例修飾**的那條路。
    /// </summary>
    public string? cropRatio { get; set; }

    /// <summary>
    /// 去背要用的**人像模型檔路徑**（2026-10-08 M3 第七顆扇形，<see cref="WorkerProtocol.OpRemoveBg"/>
    /// 專用）。
    ///
    /// **為什麼這個欄位非有不可**：這個 op 是唯一一個需要**外部資源檔**的 op，而那顆模型
    /// （12.98 MB）不進版控。解析「模型在哪裡」只有一份規則
    /// （<see cref="RemoveBgModel.ResolvePath"/>，預設＝執行檔旁邊的 `models\`），
    /// 但**診斷必須能指向一個不存在／被截斷的檔**，好讓「缺模型」與「模型損壞」這兩種狀態
    /// 在驗收裡真的被走過；同一顆模型也要能被使用者的設定或未來的 `--remove-bg-model` 指到別處。
    /// 少了這個欄位，worker 那一側只能自己重讀命令列——那會變成第二個擁有者（而且 worker 的
    /// 命令列上什麼都沒有）。
    ///
    /// ⚠ **缺席語意（<c>null</c>）＝預設位置**，也就是不指定時該有的行為——所以它對既有 op
    /// 的封包影響是**零**（<c>WhenWritingNull</c>），而且它與 `outputDir` 那種
    /// 「缺席語意與新功能意圖相反」的形狀不同（見 <see cref="WorkerProtocol.Version"/>）。
    /// 呼叫端一定送**絕對路徑**（<see cref="RemoveBgModel.ResolvePath"/> 負責轉）：
    /// 相對路徑會對上 worker 的工作目錄而不是 UI 的，那會變成兩邊各說各話。
    /// </summary>
    public string? removeBgModel { get; set; }
    public int quality { get; set; }
    public int done { get; set; }
    public int total { get; set; }
    public string? current { get; set; }
    public bool ok { get; set; }
    /// <summary>整批的成功檔數（summary 用；與單檔的 <see cref="ok"/> 分開，免得型別打架）。</summary>
    public int okCount { get; set; }
    public int failedCount { get; set; }
    public string? outputPath { get; set; }
    public string? errorCode { get; set; }
    public string? message { get; set; }
    /// <summary>
    /// 讀 QR 的產物（<see cref="WorkerProtocol.OpReadQr"/> 專用，2026-10-07 M3 第五顆扇形）。
    ///
    /// **為什麼是新的欄位而不是新的封包型別／新的通道**：理由與 `crop` 那五個欄位是同一條，
    /// 而這裡更強一點——<c>result</c> 封包已經是「一個檔案進去、一個結果出來」的唯一路徑，
    /// 失敗分類（<see cref="errorCode"/>）、規格 §7.1 的訊息（<see cref="message"/>）、
    /// 來源檔（<see cref="sourcePath"/>）、詳細原因（<see cref="detail"/>）全部掛在它上面；
    /// 另開一個 `text` 封包型別等於讓第二個擁有者去重新回答「進度怎麼報、失敗怎麼報」。
    ///
    /// **缺席語意（<c>null</c>）＝「這個結果沒有文字」**：那是「這個欄位還不存在的年代」的封包
    /// 該有的意思，也正是**其餘每一個 op 的行為**——它們不讀這個欄位，所以它們的封包
    /// 逐位元不變（<c>WhenWritingNull</c> 讓 null 根本不進 JSON）。配對錯誤不會變成靜默做錯事，
    /// 因為一個不認識 <c>read-qr</c> 的舊 worker 走 <see cref="WorkerServer.RejectUnknownOp"/>。
    ///
    /// ⚠ 它與 <see cref="outputPath"/> **互斥**：`read-qr` 不寫檔，所以它的結果
    /// <see cref="outputPath"/> 一定是 null、<c>text</c> 才有值。
    /// </summary>
    public string? text { get; set; }
    /// <summary>
    /// 單檔結果的來源檔案。`message` 是規格 §7.1 的**排版結果**（`broken.png 無法解碼`），
    /// 把檔名當資料用等於在解析自己的排版；UI 的失敗清單（§7.3）要檔名與原因兩欄，
    /// 所以檔名必須是欄位而不是字串裡的一段。
    /// </summary>
    public string? sourcePath { get; set; }
    public string? detail { get; set; }
    public string? code { get; set; }
}

/// <summary>
/// Worker 行程（規格 §9.1）：自己連回 UI 建立的具名管道，逐檔轉換，逐檔回報。
///
/// 為什麼要有獨立行程：解碼／編碼會吃 CPU 與記憶體，而且大型檔案可能卡在雲端下載。
/// UI 行程**不可以**被這些事影響（輪盤要能立刻反應、保險要能隨時收層）。
/// </summary>
internal static class WorkerServer
{
    public static int Run(string pipeName, Evidence evidence)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(10000);
        }
        catch (Exception ex)
        {
            evidence.Line($"worker: connect to pipe '{pipeName}' failed: {ex.GetType().Name}: {ex.Message}");
            return 11;
        }

        // 管道的一切都交給 PipeLink（每個方向只有一個非同步擁有者）
        using var link = new PipeLink(pipe, evidence.Line);
        var inbox = new BlockingCollection<WorkerMessage>();
        var pipeClosed = new ManualResetEventSlim(false);
        link.Received += message =>
        {
            try { inbox.Add(message); } catch { /* 已關閉 */ }
        };
        link.Closed += () => pipeClosed.Set();
        link.Start();

        var registry = new CodecRegistry();
        var engine = new ConversionEngine(registry, evidence.Line);
        var journal = new TempJournal(TempJournal.DefaultPath());
        int recovered = journal.RecoverStale();
        evidence.Line($"worker: ready protocolVersion={WorkerProtocol.Version} " +
                      $"offered=[{string.Join(",", registry.OfferedOutputs)}] " +
                      $"staleTempsRecovered={recovered} (spec 7.1)");

        link.Send(new WorkerMessage
        {
            type = WorkerMessageTypes.Ready,
            detail = $"offered=[{string.Join(",", registry.OfferedOutputs)}]"
        });

        while (true)
        {
            if (!inbox.TryTake(out WorkerMessage? message, 50))
            {
                if (pipeClosed.IsSet) { evidence.Line("worker: UI closed the pipe; exiting"); return 0; }
                continue;
            }

            if (message.protocolVersion != WorkerProtocol.Version)
            {
                evidence.Line($"worker: protocol mismatch (got {message.protocolVersion}, " +
                              $"expected {WorkerProtocol.Version}) -> refusing");
                link.Send(new WorkerMessage
                {
                    type = WorkerMessageTypes.Error,
                    code = WorkerErrorCodes.ProtocolMismatch,
                    detail = $"worker speaks {WorkerProtocol.Version}, UI sent {message.protocolVersion}"
                });
                Thread.Sleep(200);      // 讓那則錯誤真的送出去再退出
                return 12;
            }

            switch (message.type)
            {
                case WorkerMessageTypes.Job:
                    RunJob(message, link, engine, journal, evidence, inbox);
                    break;
                case WorkerMessageTypes.Cancel:
                    evidence.Line($"worker: cancel for job {message.jobId} with no active job");
                    break;
                case WorkerMessageTypes.Shutdown:
                    evidence.Line("worker: shutdown requested");
                    return 0;
                default:
                    evidence.Line($"worker: unknown message type '{message.type}' ignored");
                    break;
            }
        }
    }

    private static void RunJob(WorkerMessage job, PipeLink link, ConversionEngine engine,
                               TempJournal journal, Evidence evidence, BlockingCollection<WorkerMessage> inbox)
    {
        string[] paths = job.sourcePaths ?? Array.Empty<string>();
        string target = (job.targetFormat ?? "").ToUpperInvariant();
        int quality = job.quality <= 0 ? 85 : job.quality;
        // op 缺席＝convert：那是「這個欄位還不存在的年代」的封包該有的意思。到這裡一定同版本，
        // 因為 protocolVersion 不符的封包在連線階段就被擋掉了（見 WorkerProtocol.Version）。
        string op = string.IsNullOrEmpty(job.op) ? WorkerProtocol.OpConvert : job.op!;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 裁切的矩形/比例先解析一次：它同時要進 job 那一行日誌與每一張圖的分派。
        // 解析兩次會讓「認不得的比例 id」那句警告被寫兩次（而其中一次還是被丟掉的），
        // 也會讓「送出去的是什麼」與「用的是什麼」有機會不一致。
        CropSpec? cropSpec = op == WorkerProtocol.OpCrop ? CropOf(job, evidence.Line) : null;

        // ⚠ op= 只**附加在行尾**：既有套件比對的是 `worker job N: files=... target=...`，
        // 插在中間會直接弄紅它們（與 wheel shown 那行同一條規矩）。
        // 裁切的矩形同樣只追加在**尾巴**：它是這一顆扇形唯一跨管道的事實，不放進日誌就只剩
        // 「UI 說它送出去了」可以相信。
        evidence.Line($"worker job {job.jobId}: files={paths.Length} target={target} quality={quality} op={op}" +
                      (op == WorkerProtocol.OpCrop ? $" crop={cropSpec?.Describe() ?? "(none)"}" : "") +
                      // ⚠ 只有 op=merge-pdf 會多這一段（附加在**行尾**，與 crop 同一條規矩：
                      // 既有套件比對的是那一行的前綴，插在中間會弄紅它們）。
                      // 為什麼非印不可：合併**唯一的輸入就是順序**，而順序有沒有跨過管道
                      // 在另一端完全看不出來——一個「順序被弄丟」的實作會產生一個
                      // 頁序全錯但每一個數字都對的檔（頁數對、尺寸集合對），
                      // 所以那一行必須把送出的順序原封不動寫下來，讓驗收有東西可比。
                      (op == WorkerProtocol.OpMergePdf
                          ? $" order=[{string.Join(",", paths.Select(Path.GetFileName))}]" : "") +
                      // 去背同理：**模型路徑是這一顆扇形唯一跨管道的新事實**，不放進日誌就只剩
                      // 「UI 說它送出去了」可以相信（而「UI 找到模型、worker 找不到」是這一條
                      // 最現實的失效——兩邊的工作目錄不一樣）。null 印成 `(default beside exe)`
                      // 而不是空字串：那是「沒有指定」與「指定成空字串」的分別。
                      (op == WorkerProtocol.OpRemoveBg
                          ? $" model={job.removeBgModel ?? "(default beside exe)"}" : ""));
        link.Send(new WorkerMessage
        {
            type = WorkerMessageTypes.Progress, jobId = job.jobId, done = 0, total = paths.Length,
            current = paths.Length > 0 ? Path.GetFileName(paths[0]) : null
        });

        int succeeded = 0, failed = 0;

        // ⚠ **`paths` 是空的**（同版本的 UI 送了空清單＝bug）而 op 是 `merge-pdf` 時，
        // 下面的迴圈一次都不會跑，於是**不會有任何 result 封包**——呼叫端看到的是
        // 「這一輪結束了、卻一個結果都沒有」，那是一種靜默的失敗。
        // 所以合併這條路要**當場回一個具名的失敗**（引擎自己會說出「0 個來源路徑」），
        // 而且**不寫任何檔**。其餘 op 的空清單維持既有行為（summary 為 0/0），
        // 因為那不是這一刀要動的事。
        if (paths.Length == 0 && op == WorkerProtocol.OpMergePdf)
        {
            ConversionResult empty = engine.MergePdf(paths, journal, job.outputDir);
            failed++;
            link.Send(new WorkerMessage
            {
                type = WorkerMessageTypes.Result, jobId = job.jobId, ok = empty.Ok,
                outputPath = empty.OutputPath, errorCode = empty.Error.ToString(),
                message = empty.UserMessage, sourcePath = empty.SourcePath, detail = empty.Detail,
                done = 0, total = 0, current = null
            });
            sw.Stop();
            evidence.Line($"worker job {job.jobId} done: ok={succeeded} failed={failed} " +
                          $"in {sw.Elapsed.TotalMilliseconds:F0} ms");
            link.Send(new WorkerMessage
            {
                type = WorkerMessageTypes.Summary, jobId = job.jobId,
                okCount = succeeded, failedCount = failed, done = failed, total = 0
            });
            return;
        }

        for (int i = 0; i < paths.Length; i++)
        {
            // 取消在**檔案邊界**生效（一個檔案轉到一半不中斷：中斷會留下沒意義的半成品）
            if (DrainCancel(inbox, job.jobId, evidence))
            {
                evidence.Line($"worker job {job.jobId}: cancelled at file boundary {i}/{paths.Length}");
                break;
            }

            link.Send(new WorkerMessage
            {
                type = WorkerMessageTypes.Progress, jobId = job.jobId, done = i, total = paths.Length,
                current = Path.GetFileName(paths[i])
            });

            // 工作的分派就在這一處。**未知的 op 大聲失敗**：不丟例外、也絕不默默當成 convert
            // 來做（那會讓「按了工具卻得到一個格式轉換」變成靜默錯誤）。
            // outputDir 原封不動傳下去（null＝來源旁邊）：解析規則只有 Settings 那一份，
            // worker 不是第二個擁有者。資料夾不存在時的回退由 NamingPolicy 負責。
            ConversionResult result = op switch
            {                WorkerProtocol.OpConvert => engine.Convert(paths[i], target, quality, journal,
                                                           job.outputDir),
                WorkerProtocol.OpRotate90 => engine.Rotate90(paths[i], journal, job.outputDir),
                WorkerProtocol.OpStripMetadata => engine.StripMetadata(paths[i], journal, job.outputDir),
                WorkerProtocol.OpCompress => engine.Compress(paths[i], PolicyOf(job, evidence.Line), journal,
                                                             job.outputDir),
                WorkerProtocol.OpCrop => cropSpec is { } spec
                    ? engine.Crop(paths[i], spec, journal, job.outputDir)
                    // 矩形整個缺席：那是同版本的 UI 送出了不該送的東西＝bug。**不猜**、也不默默
                    // 用一個全圖矩形去裁（那會變成 CropNothingToDo 的拒絕，訊息還會誤導人）。
                    : RejectCropWithoutRect(paths[i]),
                // 讀 QR：**唯一一個不寫檔的 op**，產物是 result 封包上的 `text`（下一行）。
                // outputDir 對它沒有意義（沒有檔案可寫），所以刻意不傳——傳了就是讓一個
                // 沒有效果的欄位看起來像有效果。
                WorkerProtocol.OpReadQr => engine.ReadQr(paths[i], journal),
                // 合併 PDF：⚠ **唯一一個多輸入、單輸出的 op**，所以它不走逐檔那一條。
                // 它只在 i == 0 跑一次（`MergePdfBatch`），而下面的 `break` 立刻結束這一輪
                // ——所以整批只會產生一個 result 封包與一個輸出檔，而 `total` 仍然是那一批的
                // 檔案數（見 totalForResult：總數必須說出「這一輪處理了幾個檔」）。
                WorkerProtocol.OpMergePdf => MergePdfBatch(paths, i, engine, journal, job.outputDir,
                                                           evidence.Line),
                // 去背：模型路徑**原封不動**傳下去（null＝執行檔旁邊的預設位置）。
                // 解析規則只有 RemoveBgModel.ResolvePath 那一份，worker 不是第二個擁有者
                // （與 outputDir 交給 Settings／NamingPolicy 同一條規矩）。
                WorkerProtocol.OpRemoveBg => engine.RemoveBg(paths[i], job.removeBgModel, journal,
                                                             job.outputDir),
                _ => RejectUnknownOp(paths[i], op)
            };
            if (result.Ok) succeeded++; else failed++;

            // ⚠ 合併（多輸入、單輸出）的 `total` **必須是那一批的檔案數**，不是 1：
            // 「這一輪處理了幾個檔」是使用者要看的那件事，而它是 3。
            int totalForResult = paths.Length;

            link.Send(new WorkerMessage
            {
                type = WorkerMessageTypes.Result,
                jobId = job.jobId,
                ok = result.Ok,
                outputPath = result.OutputPath,
                errorCode = result.Error.ToString(),
                message = result.UserMessage,
                sourcePath = result.SourcePath,
                // `text` 只有 read-qr 會有值（其餘 op 一律 null ⇒ 不進 JSON，見 WorkerMessage.text）。
                // **原封不動**傳下去：這個欄位是產物本身，不是排版——呼叫端要斷言的是
                // 「解出來的每一個字元」，所以中間不可以有任何加工（沒有 trim、沒有截斷）。
                text = result.QrText,
                detail = result.Detail,
                done = i + 1,
                total = totalForResult,
                current = Path.GetFileName(paths[i])
            });

            // 合併已經在 i == 0 那一輪把**整批**做完了（一個輸出檔、一個 result）。
            // 剩下的 i 只是讓這一輪的收尾統計走到位，**不可以再送 result**：
            // 再送就是對同一次工作回報第二次，而呼叫端會把它當成第二個檔案的結果
            // ——那正是「同一批路徑被逐檔執行」的症狀（N 個合併檔、或同一個名字被搶）。
            if (op == WorkerProtocol.OpMergePdf) break;
        }

        sw.Stop();
        evidence.Line($"worker job {job.jobId} done: ok={succeeded} failed={failed} " +
                      $"in {sw.Elapsed.TotalMilliseconds:F0} ms");
        link.Send(new WorkerMessage
        {
            type = WorkerMessageTypes.Summary, jobId = job.jobId,
            okCount = succeeded, failedCount = failed,
            done = succeeded + failed, total = paths.Length
        });
    }

    /// <summary>
    /// 封包上的壓縮欄位 → <see cref="CompressionPolicy"/>（<c>op=compress</c> 的唯一輸入）。
    ///
    /// ⚠ 這裡**不是第二個政策擁有者**：合法值的清單來自 <see cref="CompressionPolicy.CanonicalMode"/>，
    /// 而「什麼是合法的上限」由 UI 端（設定頁 ＋ <see cref="SettingsStore.TryApply"/>）把關。
    /// worker 只做一件防禦性的收斂：缺席／認不得的一律回標準模式並**大聲記 log**——
    /// 到得了這裡代表同版本的 UI 送出了不該送的東西，那是 bug，不該靜默。
    /// </summary>
    private static CompressionPolicy PolicyOf(WorkerMessage job, Action<string> log)
    {
        string given = job.compressMode ?? "";
        string mode = CompressionPolicy.CanonicalModeOrEmpty(given);
        if (mode.Length == 0 && given.Length > 0)
        {
            mode = CompressionPolicy.ModeStandard;
            log($"compress: unknown compressMode '{given}' -> '{mode}' " +
                $"(this worker speaks {string.Join("/", CompressionPolicy.Modes)})");
        }
        else if (mode.Length == 0)
        {
            mode = CompressionPolicy.ModeStandard;   // 缺席＝這個欄位還不存在的年代的意思
        }

        long target = job.compressTargetBytes ?? 0;
        if (target < 0)
            log($"compress: negative compressTargetBytes {target} -> 0 (no limit)");

        return new CompressionPolicy(
            mode,
            Math.Max(0, target),
            job.standardJpgQuality is >= 60 and <= 100 ? job.standardJpgQuality.Value : 85,
            job.standardWebpQuality is >= 60 and <= 100 ? job.standardWebpQuality.Value : 85);
    }

    /// <summary>
    /// 封包上的裁切欄位 → <see cref="CropSpec"/>（<c>op=crop</c> 的唯一輸入）；整個矩形缺席時回 null。
    ///
    /// ⚠ 這裡**不是第二個政策擁有者**：合法值的清單來自 <see cref="CropRatios"/>，
    /// 「什麼是合法的矩形」由 <see cref="CropMath"/> 把關。worker 只做一件防禦性的收斂：
    /// 認不得的比例 id 一律回自由裁切並**大聲記 log**——到得了這裡代表同版本的 UI 送出了
    /// 不該送的東西，那是 bug，不該靜默（與 <see cref="PolicyOf"/> 同一條規矩）。
    /// </summary>
    private static CropSpec? CropOf(WorkerMessage job, Action<string> log)
    {
        if (job.cropX is not { } x || job.cropY is not { } y
            || job.cropW is not { } w || job.cropH is not { } h)
        {
            return null;
        }

        string given = job.cropRatio ?? "";
        if (given.Length > 0 && !CropRatios.IsCanonical(given))
        {
            // 訊息列的是**合法值集合**（All，八個），不是按鈕列的五顆：`9:16`／`3:4`／
            // `original-flipped` 沒有按鈕但**合法**——它們是裁切視窗的方向鈕產生的 id。
            // 拿按鈕列來當「我聽得懂哪些」會在日誌上說出假話（明明支援卻說不支援）。
            log($"crop: unknown cropRatio '{given}' -> '{CropRatios.DefaultId}' " +
                $"(this worker speaks {string.Join("/", CropRatios.All.Select(p => p.Id))})");
        }

        // 逐值防護：NaN／Infinity 會在 CropMath 的夾制裡變成 0（夾制本身就是那份規則），
        // 但先在這裡說出來——「收到一個非數字的欄位」是另一件事，不該混進夾制的日誌裡。
        foreach ((string name, double value) in
                 new[] { ("cropX", x), ("cropY", y), ("cropW", w), ("cropH", h) })
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                log($"crop: {name}={value} is not a finite number -> treated as 0 by the clamp " +
                    "(a same-version UI should never send this)");
        }

        return new CropSpec(x, y, w, h, CropRatios.CanonicalId(given));
    }

    /// <summary>
    /// `op=crop` 但封包上沒有矩形：**具名失敗**，不猜、也不默默裁一張全圖。
    /// 訊息沿用規格 §7.1 既有的「無法讀取來源檔案」那一類嗎？不——那不是原因。
    /// 這裡用 <see cref="ConversionError.Unknown"/>（「轉檔失敗」）配上真正的細節在 detail，
    /// 因為對使用者而言這確實是「不該發生的失敗」，而維修的人看得到 detail。
    /// </summary>
    private static ConversionResult RejectCropWithoutRect(string sourcePath) =>
        new(sourcePath, null, ConversionError.Unknown,
            $"op=crop without cropX/cropY/cropW/cropH (this worker speaks {CropRatios.DefaultId} " +
            "only when all four are present; nothing is guessed here)");

    /// <summary>
    /// 未知的 op：回一個**失敗的結果**（不丟例外，讓既有的失敗回報路徑接手），
    /// 使用者訊息走 <see cref="ConversionError.Unknown"/> 的規格字串（UI 不必為它多寫一條路徑），
    /// 真正的細節放在 detail 給日誌——那才是維修的人看得到的地方。
    ///
    /// **列出認識的 op 是刻意的**：這一行就是「未知 op 不會靜默變成 convert」這句話的可觀測證據，
    /// 也是不升協議版本的理由（見 <see cref="WorkerProtocol.Version"/>）。加工具時**要一起更新它**，
    /// 否則這句失敗訊息會說謊。
    ///
    /// ⚠ **這一串必須與上面分派 <c>switch</c> 的臂一一對應、順序相同**——
    /// 兩份清單不同步＝這句訊息開始說謊，而它正是「舊 worker 不會默默做錯事」的證據。
    /// 這條規則不是靠自律：`tools/verify-m3d.ps1` 的 S19.06 直接讀**原始碼**把兩份清單對起來。
    /// </summary>
    private static ConversionResult RejectUnknownOp(string sourcePath, string op) =>
        new(sourcePath, null, ConversionError.Unknown,
            $"unknown op '{op}' (this worker speaks " +
            $"{WorkerProtocol.OpConvert}/{WorkerProtocol.OpRotate90}/{WorkerProtocol.OpStripMetadata}/" +
            $"{WorkerProtocol.OpCompress}/{WorkerProtocol.OpCrop}/{WorkerProtocol.OpReadQr}/" +
            $"{WorkerProtocol.OpMergePdf}/{WorkerProtocol.OpRemoveBg})");

    /// <summary>
    /// `op=merge-pdf` 的**整批**執行：這一個 op 只有一個工作，而它吃**整份路徑清單**。
    ///
    /// 為什麼寫成一個方法而不是塞進分派臂：那一臂要能一眼看出「它與其他七個形狀不同」，
    /// 而 30 行的說明與防禦會把 <see cref="RunJob"/> 的分派表變成一團。
    ///
    /// 三件事在這裡定案：
    ///   ① **只有 <paramref name="index"/> == 0 才真的做事**——其餘回一個「已被同一批處理」
    ///      的佔位結果，而 <see cref="RunJob"/> 不會把它送出去（那裡的 <c>break</c>）。
    ///      所以同一次工作只會產生**一個** result 封包與**一個**輸出檔。
    ///   ② **空清單不是「成功但沒做事」**：回一個具名的失敗（引擎負責那句話），
    ///      而且**不寫任何檔**。一個空的請求靜默回 ok 會讓呼叫端以為合併了一個空集合。
    ///   ③ 順序**原封不動**傳進引擎：`paths` 的順序就是頁序，這一層不重排、不去重、
    ///      不排序（去重會讓 `a;b;a` 少一份，而那是完全合法的請求——使用者可能刻意要兩份）。
    /// </summary>
    private static ConversionResult MergePdfBatch(string[] paths, int index, ConversionEngine engine,
                                                  TempJournal? journal, string? outputDir,
                                                  Action<string> log)
    {
        if (index != 0)
        {
            // 只有 i == 0 會做事，所以這一行**在正常流程下不會被執行**（RunJob 在那之後 break）。
            // 留著它是為了讓「為什麼其餘的 i 沒有結果」在日誌上是一句看得到的話，
            // 而不是一個要讀原始碼才推得出來的事實。
            log($"merge-pdf: batch already handled at file 1/1; file {index + 1} of this batch " +
                "does not produce its own result or its own output file");
            return new ConversionResult(paths[index], null, ConversionError.None,
                "handled as part of the batch (no separate output)");
        }
        return engine.MergePdf(paths, journal, outputDir);
    }

    private static bool DrainCancel(BlockingCollection<WorkerMessage> inbox, int jobId, Evidence evidence)
    {
        bool cancelled = false;
        while (inbox.TryTake(out WorkerMessage? pending, 0))
        {
            if (pending.type == WorkerMessageTypes.Cancel && pending.jobId == jobId) cancelled = true;
            else if (pending.type == WorkerMessageTypes.Job)
                evidence.Line($"worker: job {pending.jobId} arrived while {jobId} was running; " +
                              "queued jobs are not implemented yet (one job at a time)");
        }
        return cancelled;
    }
}
