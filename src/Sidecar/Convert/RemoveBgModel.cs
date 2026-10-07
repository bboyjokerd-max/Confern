using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Sidecar;

/// <summary>
/// 去背（工具 id <c>remove-bg</c>，M3 第七顆扇形）的**推論引擎**：載入人像模型、前處理、
/// 跑一次推論、把遮罩交回去。**這裡只做模型的事**——解碼、寫檔、命名都在
/// <see cref="ConversionEngine.RemoveBg"/>。
///
/// ============================ 模型（誰、哪一份、什麼授權） ============================
/// <list type="bullet">
/// <item><b>來源</b>：`Xenova/modnet` 的 `onnx/model_fp16.onnx`（Hugging Face）。</item>
/// <item><b>授權</b>：**apache-2.0**（該 repo 的 README 明文涵蓋 code／models／demos）。</item>
/// <item><b>體積</b>：**12,984,781 bytes**（實測，本機檔案）。</item>
/// <item><b>sha256</b>：<c>25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6</c>
///       ——**這個值是實測的，不是抄來的**（2026-10-08，對本機的
///       <c>%TEMP%\onnxprobe\models\model_fp16.onnx</c> 跑 `Get-FileHash -Algorithm SHA256`）。
///       取得腳本 <c>tools/get-rembg-model.ps1</c> 拿它驗下載完的檔，驗不過就刪掉並回非零。</item>
/// <item><b>為什麼是 fp16</b>：與 fp32 的遮罩逐值幾乎相同（合成圖平均差 0.00001、真實照片 0.00010，
///       實測），體積卻只有一半（12.98 MB vs 24.69 MB）。
///       **quantized 已否決**：主體 alpha 0.507 → 0.088（等於把主體吃掉）、平均差 0.206，而且更慢
///       （197 ms vs 115 ms）。三個變體的輸入型別**都是 float**，所以不需要 `DenseTensor&lt;Half&gt;`。</item>
/// <item><b>不進版控</b>：模型是二進位大檔，`.gitignore` 含 <c>models/</c>。
///       要不要把它一起出貨是**使用者的決定**——這裡兩種都跑得動（缺檔＝具名狀態，見下）。</item>
/// </list>
///
/// ============================ 為什麼 session 一定要快取 ============================
/// 實測（本機，Release x64）：`new InferenceSession` = **139–190 ms**，而跑一次推論 = **72–115 ms**。
/// **建 session 比跑一次還貴**，所以它必須在行程內只建一次、重複使用（見 <see cref="Session"/>）；
/// 每一個檔案都重建一次會讓批次的第一個成本乘以檔案數。
///
/// ============================ 三種必須說得出來的狀態 ============================
/// <list type="number">
/// <item><b>模型不存在</b>（<c>File.Exists</c> 為 false）：回 <see cref="ModelLoad.Missing"/>。
///       ⚠ 先自己判 <c>File.Exists</c>，**不靠 ORT 的例外**：實測 ORT 會丟
///       <c>OnnxRuntimeException [ErrorCode:NoSuchFile]</c>，但那個字串是套件內部的實作細節
///       （版本一換就可能變），而 <c>File.Exists</c> 是我們自己的判準。</item>
/// <item><b>模型損壞／下載不完整</b>：`new InferenceSession` 丟 <c>OnnxRuntimeException</c>，
///       實測訊息含 <c>[ErrorCode:InvalidProtobuf] Protobuf parsing failed</c>
///       （下載到 1/3 的檔就是這個形狀）。回 <see cref="ModelLoad.Corrupt"/>——
///       **與「不存在」是不同的狀態、不同的訊息**：叫使用者去下載一個他已經有的檔案，
///       等於給了一句沒有下一步的話。</item>
/// <item><b>找不到人像主體</b>：模型**載入成功、也跑完了**，只是遮罩幾乎全空。
///       實測：非人像的場景（`fixtures/qr/qr-small-in-scene-160.png`，3000×2250）
///       得到 max=0.0165、mean=0.0001；人像（NASA 的 Leland Melvin 與兩隻狗，512×410）
///       得到 max=1.0000、mean=0.5241。判準與門檻見
///       <see cref="ConversionEngine.RemoveBg"/>（遮罩統計的擁有者在那裡，這一層只交遮罩）。</item>
/// </list>
internal static class RemoveBgModel
{
    /// <summary>
    /// 模型檔在磁碟上的預設位置：**執行檔旁邊的 `models\`**（取得腳本下載到同一個地方）。
    ///
    /// 為什麼是相對於執行檔而不是相對於工作目錄：worker 行程是由 UI 用
    /// <c>Process.Start(exe, "--worker", ...)</c> 起的，它的工作目錄**不是** exe 所在目錄。
    /// 用工作目錄解析會在「使用者從桌面捷徑啟動」與「從命令列啟動」兩種情況下指到不同的地方，
    /// 而症狀會是「有的時候找不到模型」——最難查的那一種。
    /// </summary>
    public const string DefaultFileName = "model_fp16.onnx";

    /// <summary>模型放的子資料夾名（與執行檔同層）。</summary>
    public const string DefaultFolderName = "models";

    /// <summary>
    /// 實測的模型長度（bytes）。**只用來當「便宜的第一道判斷」**，真正的權威是
    /// <see cref="Session"/> 建不建得起來（sha256 是取得腳本的職責，見類別說明）。
    /// </summary>
    public const long ExpectedBytes = 12_984_781L;

    private static readonly object Gate = new();
    private static InferenceSession? _session;
    private static string? _sessionPath;

    /// <summary>
    /// 「模型現在是什麼狀態」——**每一個狀態都對應一句說得出下一步的使用者訊息**
    /// （見 <see cref="ConversionEngine.RemoveBg"/> 的分派）。
    /// </summary>
    internal enum ModelLoad
    {
        /// <summary>載入成功（或還沒試過）。</summary>
        Ready = 0,

        /// <summary>檔案不在那個路徑上。</summary>
        Missing,

        /// <summary>檔案在，但 ORT 解不開（下載不完整／不是 ONNX／被截斷）。</summary>
        Corrupt
    }

    /// <summary>
    /// 模型路徑的**唯一解析處**。<paramref name="cliModel"/> 為 null／空白＝用預設位置
    /// （<see cref="DefaultFileName"/> 在 <see cref="AppContext.BaseDirectory"/> 的
    /// <see cref="DefaultFolderName"/> 底下）。
    ///
    /// 相對路徑一律轉成絕對路徑：診斷路徑會把這個字串**跨管道**送給 worker
    /// （<c>WorkerMessage.removeBgModel</c>），而相對路徑在另一端會對上**不同**的工作目錄
    /// ——那會變成「UI 找到模型、worker 找不到」這種兩邊都覺得自己對的分歧。
    /// </summary>
    public static string ResolvePath(string? cliModel)
    {
        if (!string.IsNullOrWhiteSpace(cliModel))
        {
            string given = cliModel.Trim();
            try { return Path.GetFullPath(given); }
            catch (Exception) { return given; }   // 連路徑都算不出來：原樣回，讓 File.Exists 判它不存在
        }
        return Path.Combine(AppContext.BaseDirectory, DefaultFolderName, DefaultFileName);
    }

    /// <summary>
    /// 這個路徑上的模型能不能用（**會真的建 session**，所以第一次呼叫是貴的那一次）。
    /// 回 <see cref="ModelLoad.Ready"/> 時 <see cref="Session"/> 一定拿得到東西。
    /// </summary>
    public static ModelLoad Check(string modelPath, Action<string> log)
    {
        _ = TryGetSession(modelPath, log, out ModelLoad state);
        return state;
    }

    /// <summary>
    /// 惰性建立、**行程內共用**的 session（見類別說明：建 session 比跑一次推論還貴）。
    /// 路徑換了（診斷會用不同的 <c>--remove-bg-model</c>）就重建，所以「剛剛驗的是別顆模型」
    /// 不會被快取蓋掉。
    /// </summary>
    public static InferenceSession? TryGetSession(string modelPath, Action<string> log, out ModelLoad state)
    {
        lock (Gate)
        {
            if (_session != null && string.Equals(_sessionPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                state = ModelLoad.Ready;
                return _session;
            }
            // 舊 session 是**別顆模型**：先放掉（它佔著約 312 MB 的工作集，實測）。
            if (_session != null)
            {
                log($"remove-bg: model path changed -> dropping the cached session " +
                    $"('{Path.GetFileName(_sessionPath ?? "?")}' -> '{Path.GetFileName(modelPath)}')");
                try { _session.Dispose(); } catch { }
                _session = null;
                _sessionPath = null;
            }

            if (!File.Exists(LongPath.Prepare(modelPath)))
            {
                state = ModelLoad.Missing;
                log($"remove-bg: model not found at '{modelPath}' (File.Exists=false) " +
                    "-> the tool will report ModelUnavailable and write nothing");
                return null;
            }

            var timer = Stopwatch.StartNew();
            try
            {
                _session = new InferenceSession(LongPath.Prepare(modelPath));
            }
            catch (Exception ex)
            {
                // ⚠ **所有**載入例外一律歸成 Corrupt，不歸成 Missing：這條路上檔案**存在**
                // （上面才判過 File.Exists），說「檔案不存在」是對使用者說一句錯的話。
                // IsModelFileCorrupt 只用來決定日誌那句話有多確定（認得出來＝實測過的形狀）。
                state = ModelLoad.Corrupt;
                log($"remove-bg: could not load '{modelPath}' " +
                    $"({ex.GetType().Name}: {FirstLine(ex.Message)}) -> this is the CORRUPT state" +
                    (IsModelFileCorrupt(ex) ? " (measured InvalidProtobuf shape)" : " (unrecognised loader failure)") +
                    ", NOT 'the file is not there'");
                return null;
            }
            _sessionPath = modelPath;
            timer.Stop();
            state = ModelLoad.Ready;
            log($"remove-bg: model '{Path.GetFileName(modelPath)}' loaded in " +
                $"{timer.Elapsed.TotalMilliseconds:F0} ms " +
                $"(inputs={_session.InputMetadata.Count}, outputs={_session.OutputMetadata.Count}; " +
                "cached for the rest of this process -- measured 139-190 ms to build vs 72-115 ms to run)");
            return _session;
        }
    }

    /// <summary>
    /// 這是「模型檔壞掉」而不是別的失敗嗎。實測的形狀是
    /// <c>OnnxRuntimeException: [ErrorCode:InvalidProtobuf] Load model from … failed: Protobuf parsing failed.</c>
    /// （下載到 1/3 的檔、以及一個 39 bytes 的 HTML 錯誤頁，兩者都是這個）。
    /// 判準刻意**同時**看錯誤碼與例外型別，而 <see cref="TryGetSession"/> 對所有載入例外
    /// 一律回 <see cref="ModelLoad.Corrupt"/>——所以這裡認不出來的例外也不會變成
    /// 「模型不存在」這句錯的話。
    /// </summary>
    private static bool IsModelFileCorrupt(Exception ex) =>
        ex is OnnxRuntimeException
        || ex.Message.Contains("InvalidProtobuf", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Protobuf", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
    {
        int cut = text.IndexOfAny(new[] { '\r', '\n' });
        return cut < 0 ? text : text.Substring(0, cut);
    }

    /// <summary>
    /// 前處理與推論的輸入名／輸出名（**實測**：輸入 <c>input</c>、輸出 <c>output</c>）。
    /// 寫成常數而不是散在程式裡：這兩個字串是模型介面的一部分，改模型就一定要改它們。
    /// </summary>
    private const string InputName = "input";
    private const string OutputName = "output";

    /// <summary>
    /// 官方 `preprocessor_config.json` 的兩項：短邊縮到 512、尺寸取整到 32 的倍數。
    ///
    /// ⚠ **`do_pad` 是 false**（官方設定就是 false）：也就是**不補成正方形**。
    /// 這一點是量出來的裁定，不是偏好的問題——把圖直接拉伸成 512×512 會**改變長寬比**，
    /// 而實測兩者的遮罩平均差 **0.26140**（聚合數字幾乎一樣，逐像素差很多）；人眼比較
    /// 拉伸版明顯較差（人被壓扁、輪廓一圈洋紅光暈），而且**變形無法事後補救**。
    /// </summary>
    private const int ShortestEdge = 512;

    /// <summary>尺寸取整的倍數（見 <see cref="ShortestEdge"/>）。</summary>
    private const int SizeDivisibility = 32;

    /// <summary>
    /// 送進模型的張量尺寸。**這一條是「遮罩放大時不可用原始尺寸」那件事的來源**：
    /// 取整到 32 會讓張量的長寬比與原圖**微微不同**（實測 960×768 → 640×512、
    /// 3000×2250 → 672×512、3840×2400 → 832×512），所以放大要用回傳的
    /// <c>TensorWidth</c>／<c>TensorHeight</c> 當來源算縮放，不能用理想比例回推。
    /// </summary>
    public static (int Width, int Height) TensorSize(int sourceWidth, int sourceHeight)
    {
        double scale = (double)ShortestEdge / Math.Min(sourceWidth, sourceHeight);
        int w = (int)Math.Round(sourceWidth * scale);
        int h = (int)Math.Round(sourceHeight * scale);
        w = Math.Max(SizeDivisibility, (int)Math.Round((double)w / SizeDivisibility) * SizeDivisibility);
        h = Math.Max(SizeDivisibility, (int)Math.Round((double)h / SizeDivisibility) * SizeDivisibility);
        return (w, h);
    }

    /// <summary>
    /// 正規化：<c>x / 127.5 - 1</c>（⇒ [-1, 1]），通道順序 **RGB**（模型的輸入是 RGB，
    /// 而 WIC 給的是 BGRA ⇒ 取樣時要換過來）。
    ///
    /// 縮放用 <see cref="TransformedBitmap"/>（WIC 的高品質重取樣），與遮罩放大回原尺寸
    /// 走同一條路：**同一個縮放器用在兩個方向**，才不會出現「縮進去是一種插值、放大回來是另一種」。
    /// </summary>
    public static float[] ToTensor(BitmapSource source, int tensorWidth, int tensorHeight)
    {
        var scaled = new TransformedBitmap(source, new ScaleTransform(
            (double)tensorWidth / source.PixelWidth, (double)tensorHeight / source.PixelHeight));
        scaled.Freeze();      // 凍結後才能交給 ORT 的執行緒（與本專案其他跨執行緒畫格同一條規矩）
        byte[] pixels = ToBgra32(scaled, out int width, out int height);
        if (width != tensorWidth || height != tensorHeight)
        {
            // 到這裡理論上到不了（縮放比例就是照張量尺寸算的），但「理論上」不是一種保證方式：
            // 尺寸不合就當場說出來，而不是把一張尺寸不對的張量餵進模型（那會回一個看似正常的結果）。
            throw new InvalidOperationException(
                $"the resampled frame is {width}x{height} but the tensor must be {tensorWidth}x{tensorHeight}");
        }

        int plane = width * height;
        var data = new float[3 * plane];
        for (int i = 0; i < plane; i++)
        {
            float b = pixels[i * 4];
            float g = pixels[i * 4 + 1];
            float r = pixels[i * 4 + 2];
            data[i] = r / 127.5f - 1f;
            data[plane + i] = g / 127.5f - 1f;
            data[2 * plane + i] = b / 127.5f - 1f;
        }
        return data;
    }

    /// <summary>一張圖的推論結果：遮罩（row-major、與張量同尺寸）與它自己的張量尺寸。</summary>
    /// <param name="Mask">
    /// 值域 **已在 [0,1]**——模型的最後一層已經包含 sigmoid（實測：輸出 min=0.0000、max=1.0000），
    /// **不要再過一次 sigmoid**（那會把 0.5 以上的值全部壓到 0.5 附近，遮罩當場爛掉）。
    /// </param>
    public readonly record struct MaskResult(float[] Mask, int TensorWidth, int TensorHeight);

    /// <summary>
    /// 跑一次推論。**呼叫端必須先確認 <see cref="Check"/> 是 Ready**（不然這裡會回一個
    /// 沒有意義的結果或丟例外——這一層不重複做狀態判定，那只會變成第二份規則）。
    /// </summary>
    public static MaskResult Infer(InferenceSession session, BitmapSource source, Action<string> log)
    {
        (int tw, int th) = TensorSize(source.PixelWidth, source.PixelHeight);
        float[] input = ToTensor(source, tw, th);

        var sw = Stopwatch.StartNew();
        using var results = session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(InputName, new DenseTensor<float>(input, new[] { 1, 3, th, tw }))
        });
        var output = results.First().AsTensor<float>();
        float[] mask = output.ToArray();
        sw.Stop();

        if (mask.Length != tw * th)
        {
            throw new InvalidOperationException(
                $"the model returned {mask.Length} values for a {tw}x{th} tensor " +
                $"(expected {tw * th}); the output shape is {string.Join(",", output.Dimensions.ToArray())}");
        }

        log($"remove-bg: tensor {tw}x{th} from {source.PixelWidth}x{source.PixelHeight} " +
            $"in {sw.Elapsed.TotalMilliseconds:F0} ms (fp16 model, shortest_edge={ShortestEdge}, " +
            $"size_divisibility={SizeDivisibility}, do_pad=false)");
        return new MaskResult(mask, tw, th);
    }

    /// <summary>
    /// 畫格 → Bgra32 的原始位元組（與 <see cref="ConversionEngine"/> 裡那一份同一個作法：
    /// stride 用 <c>width * 4</c>，目的陣列自己配）。**刻意留在這一層**而不是共用：
    /// 那一份是 private、而且它屬於解碼／讀 QR 的路徑，這裡要的是「WIC 畫格 → 緊湊的 BGRA」，
    /// 兩者一旦合併，改一邊就會動到另一邊（這一刀不動既有工具的任何一行行為）。
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
    /// 遮罩（[0,1] 的 float）→ **Bgra32 的灰階畫格**（B=G=R=A=灰值）。
    ///
    /// 為什麼灰階要塞進**四個通道**而不是只放 R：下一步是「放大回原尺寸」再「取 R 當 alpha」。
    /// 放大用的 <see cref="TransformedBitmap"/> 對四個通道一視同仁，所以把值放在哪裡都一樣；
    /// 但**取 R 當 alpha** 這件事要求放大之後 R 仍然是那個灰值——如果只寫 R 而讓 A 留著
    /// 別的値，任何會做 alpha 加權重取樣的實作就會算出不一樣的結果。四通道填滿是同值，
    /// 因此無論怎麼取樣，R 都等於那塊區域的遮罩值。
    /// </summary>
    public static BitmapSource MaskToGrayFrame(float[] mask, int tensorWidth, int tensorHeight)
    {
        int stride = tensorWidth * 4;
        var pixels = new byte[(long)stride * tensorHeight];
        for (int i = 0, p = 0; i < mask.Length; i++, p += 4)
        {
            float v = mask[i];
            byte g = (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);
            pixels[p] = g;
            pixels[p + 1] = g;
            pixels[p + 2] = g;
            pixels[p + 3] = g;
        }
        var frame = BitmapSource.Create(tensorWidth, tensorHeight, 96, 96, PixelFormats.Bgra32, null,
                                        pixels, stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// 遮罩的統計（**去背是否真的有做事**的唯一判準來源）。門檻與判定在
    /// <see cref="ConversionEngine.RemoveBg"/>；這裡只負責把數字算出來。
    /// </summary>
    /// <param name="Max">整張遮罩的最大值——「有沒有抓到主體」看這個（實測：人像 1.0000、非人像 0.0165）。</param>
    /// <param name="Mean">平均值——「主體有多大」看這個（實測：人像 0.5241、非人像 0.0001）。</param>
    public readonly record struct MaskStats(float Min, float Max, double Mean)
    {
        public static MaskStats Of(float[] mask)
        {
            float min = float.MaxValue, max = float.MinValue;
            double sum = 0;
            foreach (float v in mask)
            {
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            return new MaskStats(min, max, sum / mask.Length);
        }
    }

    /// <summary>
    /// 把快取的 session 放掉（**只有測試接縫會呼叫**：診斷要能證明「換一顆模型會重建」，
    /// 而正常產品路徑不需要——行程結束時整個記憶體就還給系統了）。
    /// </summary>
    internal static void DropSessionForTest(Action<string> log)
    {
        lock (Gate)
        {
            if (_session == null) return;
            try { _session.Dispose(); } catch { }
            _session = null;
            _sessionPath = null;
            log("remove-bg: cached session dropped (diagnostic seam only)");
        }
    }
}
