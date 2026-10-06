using System.Text.Json;

namespace Sidecar;

/// <summary>
/// 設定（規格 §8）。預設值就是規格值——使用者不開設定檔也有正確行為。
/// 檔案位置：`%LOCALAPPDATA%\Confern\settings.json`。
/// </summary>
internal sealed record Settings
{
    public int JpgQuality { get; init; } = 85;              // 規格 §8：60–100
    public int WebpQuality { get; init; } = 85;
    public string TriggerKey { get; init; } = "Space";      // 空白鍵
    public bool StartWithWindows { get; init; } = true;
    public string Language { get; init; } = "system";
    public string NamingCollision { get; init; } = "auto-suffix";   // 固定 `(1)`，不詢問
    public int CaptureTimeoutSeconds { get; init; } = 30;   // 5–120
    public string EmergencyHotkey { get; init; } = "Ctrl+Alt+Shift+Q";
    public bool Diagnostics { get; init; } = false;

    /// <summary>
    /// 輸出資料夾（規格 §9.5，2026-10-05 使用者裁定）。三種寫法：
    /// 空字串或 <c>source</c>＝**來源檔旁邊**（預設）；<c>desktop</c>＝桌面；其他＝資料夾路徑。
    ///
    /// 為什麼用字串而不是列舉＋路徑兩個欄位：檔案是給人看的（文件也教使用者直接改它），
    /// 一個欄位比兩個欄位好懂。打錯的寫法（例如 `Destkop`）會被當成路徑 → 不存在 →
    /// <see cref="NamingPolicy"/> 回退到來源旁邊，**而且引擎會把「回退了」寫進日誌**——
    /// 打錯不會靜默地什麼都沒發生。
    /// </summary>
    public string OutputFolder { get; init; } = "";

    /// <summary>
    /// 壓縮等級（2026-10-06 M3 的第三顆扇形，工具 id <c>compress</c>）。四個正規值見
    /// <see cref="CompressionPolicy.Modes"/>。
    ///
    /// ⚠ **向後相容的關鍵**：預設值是 <see cref="CompressionPolicy.ModeStandard"/>＝
    /// 「照既有的 <see cref="JpgQuality"/>／<see cref="WebpQuality"/> 走」。一份沒有這個欄位的
    /// 舊設定檔載入後就是這個值，而所有**既有**的路徑（格式轉換、旋轉、去中繼資料）根本不讀它，
    /// 所以舊檔案的行為逐位元不變。等級由設定頁決定（使用者裁定），**不做成輪盤上的多顆扇形**。
    /// </summary>
    public string CompressionMode { get; init; } = CompressionPolicy.DefaultMode;

    /// <summary>
    /// 「壓縮到」的上限，單位 **KB**。<c>0</c>＝沒有上限（＝這個欄位還不存在的年代的意思）。
    ///
    /// 為什麼只存一個數字而不連單位一起存：單位是**畫面的寫法**（KB／MB），不是設定值。
    /// 存兩個欄位就會出現「1024 KB」與「1 MB」兩個不同的檔案內容代表同一件事，
    /// 而它們在 diff 裡看起來像兩個不同的設定。
    /// </summary>
    public int CompressionTargetKb { get; init; } = CompressionPolicy.DefaultTargetKb;

    /// <summary>把超出範圍的值夾回規格允許的區間，並回報改了什麼（不靜默）。</summary>
    public Settings Normalized(out List<string> notes)
    {
        notes = new List<string>();
        int jpg = Math.Clamp(JpgQuality, 60, 100);
        int webp = Math.Clamp(WebpQuality, 60, 100);
        int capture = Math.Clamp(CaptureTimeoutSeconds, 5, 120);
        if (jpg != JpgQuality) notes.Add($"JpgQuality {JpgQuality} -> {jpg} (spec 8: 60-100)");
        if (webp != WebpQuality) notes.Add($"WebpQuality {WebpQuality} -> {webp} (spec 8: 60-100)");
        if (capture != CaptureTimeoutSeconds) notes.Add($"CaptureTimeoutSeconds {CaptureTimeoutSeconds} -> {capture} (spec 8: 5-120)");

        // 壓縮政策（M3）。**這裡一個例外都不准丟**：設定檔是給人直接改的，打錯字必須是
        // 「夾回／退回預設並大聲記 log」，不是「程式起不來」——本專案才剛為此付過代價
        // （設定頁的例外殺掉 UI 執行緒，2026-10-06）。
        string mode = CompressionPolicy.CanonicalModeOrEmpty(CompressionMode);
        if (mode.Length == 0)
        {
            notes.Add($"CompressionMode '{CompressionMode}' -> '{CompressionPolicy.ModeStandard}' " +
                      $"(not one of {string.Join("/", CompressionPolicy.Modes)})");
            mode = CompressionPolicy.ModeStandard;
        }

        int targetKb = Math.Clamp(CompressionTargetKb, 0, CompressionPolicy.MaxTargetKb);
        if (targetKb != CompressionTargetKb)
            notes.Add($"CompressionTargetKb {CompressionTargetKb} -> {targetKb} " +
                      $"(0 = no limit, max {CompressionPolicy.MaxTargetKb})");

        // 「壓縮到」卻沒有上限＝這個模式做不到任何事。**退回標準並說出來**，不要留下一個
        // 「存得下但沒生效」的組合：使用者選了「壓縮到 0 KB」時，他要的是「照原本的品質走」，
        // 而不是「每一張都失敗」。
        if (mode == CompressionPolicy.ModeTargetSize && targetKb < CompressionPolicy.MinTargetKb)
        {
            notes.Add($"CompressionMode '{mode}' without a target (CompressionTargetKb={CompressionTargetKb}) " +
                      $"-> '{CompressionPolicy.ModeStandard}' (a limit of 0 KB is not a limit)");
            mode = CompressionPolicy.ModeStandard;
            targetKb = 0;
        }

        return this with
        {
            JpgQuality = jpg,
            WebpQuality = webp,
            CaptureTimeoutSeconds = capture,
            CompressionMode = mode,
            CompressionTargetKb = targetKb
        };
    }

    /// <summary>目標格式要用的編碼品質（只有 JPG／WebP 有意義）。</summary>
    public int QualityFor(string targetFormat) =>
        CompressionPolicy.QualityForFormat(JpgQuality, WebpQuality, targetFormat);

    /// <summary>這份設定所代表的壓縮政策（「壓縮」工具的唯一輸入）。</summary>
    public CompressionPolicy ToCompressionPolicy() => CompressionPolicy.FromSettings(this);

    /// <summary>
    /// 解析 <see cref="OutputFolder"/> 成為實際路徑。**回傳 null ＝來源檔旁邊**——
    /// 這個 null 正是 <see cref="ConversionEngine"/> 的 <c>targetDirectory</c> 參數要的預設值，
    /// 所以「設定」到「引擎」的對應只發生在這一處（不散落在 UI 裡）。
    /// </summary>
    public string? ResolveOutputDirectory()
    {
        string value = OutputFolder.Trim();
        if (value.Length == 0) return null;
        if (value.Equals("source", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("sibling", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.Equals("desktop", StringComparison.OrdinalIgnoreCase))
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return value;
    }
}

/// <summary>
/// 觸發鍵的解析結果。<c>Name</c> 是可寫回檔案的正式寫法，<c>Warning</c> 是**規格要求的提示**
/// （§8：選 Shift 要警告「Shift 在檔案總管拖曳時代表移動」）——提示是模型的事，不是 UI 自己編的。
/// </summary>
internal sealed record TriggerKeyInfo(uint Vk, string Name, string? Warning);

internal static class SettingsStore
{
    /// <summary>
    /// 檔案是給人看的（文件也教使用者直接改它），所以縮排輸出。欄位名不區分大小寫：
    /// 「大小寫打得不一樣」不該變成靜默退回預設。
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 這個檔案是給人看、也教使用者直接改的，所以不要轉義 `+`、中文之類的字元
        // （預設編碼器會把 `Ctrl+Alt+Shift+Q` 寫成 `Ctrl\u002BAlt\u002BShift\u002BQ`）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 規格 §8 的九個鍵 ＋ <see cref="Settings.OutputFolder"/>（規格 §9.5，2026-10-05 追加的第十個）
    /// ＋ 壓縮政策的兩個鍵（M3，2026-10-06 追加的第十一與第十二個）。
    /// **唯一擁有者**：未知鍵警告、`--settings-set`、設定頁都走這裡。
    ///
    /// ⚠ <c>OutputFolder</c> 曾經漏列：設定檔裡出現它會被當成打錯字而警告一次
    /// （C1a 加了欄位卻沒加鍵，2026-10-05 的 C1 補上）。這種警告一旦是假警報，
    /// 真正打錯字的那一則就沒人會看。所以加欄位時**這裡一定要一起加**。
    /// </summary>
    public static readonly string[] KnownKeys =
    {
        nameof(Settings.JpgQuality), nameof(Settings.WebpQuality), nameof(Settings.TriggerKey),
        nameof(Settings.StartWithWindows), nameof(Settings.Language), nameof(Settings.NamingCollision),
        nameof(Settings.CaptureTimeoutSeconds), nameof(Settings.EmergencyHotkey), nameof(Settings.Diagnostics),
        nameof(Settings.OutputFolder),
        nameof(Settings.CompressionMode), nameof(Settings.CompressionTargetKb)
    };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.Name, "settings.json");

    /// <summary>
    /// 一行說完目前生效的設定。日誌、probe、設定頁摘要共用，避免三份不同說法。
    ///
    /// ⚠ <c>outputFolder=</c> 只**附加在行尾**（2026-10-05 的 C1）：既有驗收腳本比對的是這行的
    /// 前綴與各 <c>key=value</c> 欄位，插在中間會弄紅它們；行尾追加則兩種都保留。
    /// 2026-10-06 的 <c>compress=</c> 沿用同一條規矩（第二個行尾欄位，仍然只追加）。
    /// </summary>
    public static string Describe(Settings s) =>
        $"jpgQuality={s.JpgQuality} webpQuality={s.WebpQuality} capture={s.CaptureTimeoutSeconds} " +
        $"trigger={s.TriggerKey} collision={s.NamingCollision} hotkey={s.EmergencyHotkey} " +
        $"diagnostics={s.Diagnostics} startWithWindows={s.StartWithWindows} language={s.Language} " +
        $"outputFolder={DescribeOutputFolder(s)} " +
        $"compress={CompressionPolicy.CanonicalMode(s.CompressionMode)}" +
        $"{(s.CompressionTargetKb > 0 ? $"/{s.CompressionTargetKb}KB" : "")}";

    /// <summary>
    /// <see cref="Settings.OutputFolder"/> 的顯示寫法（日誌與探針共用）。空字串＝**來源檔旁邊**，
    /// 這個用語與 <see cref="Program"/> 既有的 `outputDir=(source folder)` 一致。
    /// </summary>
    public static string DescribeOutputFolder(Settings s)
    {
        string value = (s.OutputFolder ?? "").Trim();
        return value.Length == 0 ? "(source folder)" : value;
    }

    public static Settings Load(string? path, Action<string> log)
    {
        path ??= DefaultPath();
        if (!File.Exists(path))
        {
            log($"settings: no file at {path}; using spec defaults (jpgQuality=85, trigger=Space, capture=30s)");
            return new Settings();
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            // 讀不到**不可以**讓程式起不來，也不可以靜默忽略
            log($"settings: {path} is unreadable ({ex.GetType().Name}: {ex.Message}) -> using spec defaults");
            return new Settings();
        }

        WarnUnknownKeys(text, path, log);

        Settings loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Settings>(text, Json) ?? new Settings();
        }
        catch (Exception ex)
        {
            log($"settings: {path} is unreadable ({ex.GetType().Name}: {ex.Message}) -> using spec defaults");
            return new Settings();
        }

        Settings normalized = loaded.Normalized(out List<string> notes);
        foreach (string note in notes) log($"settings: clamped {note}");
        log($"settings: loaded from {path} {Describe(normalized)}");
        return normalized;
    }

    /// <summary>
    /// 未知的鍵要**說出來**：打錯一個字母就靜默退回預設，是最難查的那種「設定沒生效」。
    /// </summary>
    private static void WarnUnknownKeys(string text, string path, Action<string> log)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                log($"settings: {path} is valid JSON but not an object -> keys not checked");
                return;
            }
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                bool known = KnownKeys.Any(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase));
                if (!known) log($"settings: WARNING unknown key '{prop.Name}' in {path} is ignored (typo?)");
            }
        }
        catch (Exception ex)
        {
            log($"settings: {path} is not valid JSON ({ex.GetType().Name}) -> keys not checked");
        }
    }

    /// <summary>
    /// 存檔（設定頁與 `--settings-set` 共用）。三個保證：
    /// ①**先夾制再寫**，檔案裡永遠不會出現規格外值；
    /// ②走 <see cref="AtomicWriter"/> 這個唯一的原子寫入擁有者，且**允許覆寫**——設定檔是我們自己的，
    /// 覆寫是意圖；使用者的輸出檔仍受「永不覆寫」紅線保護；
    /// ③失敗**不留 `.tmp-*`**、**不靜默**（記錄後往外丟，結束碼由呼叫端決定）。
    /// </summary>
    public static void Save(Settings settings, string? path, Action<string> log)
    {
        path ??= DefaultPath();
        Settings normalized = settings.Normalized(out List<string> notes);
        foreach (string note in notes) log($"settings: clamped {note} before saving");

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, Json);
        try
        {
            // 登記進臨時檔日誌：萬一在存檔途中斷電，啟動時的回收仍然找得到那個殘檔。
            AtomicWriter.Write(path, s => s.Write(bytes, 0, bytes.Length),
                               new TempJournal(TempJournal.DefaultPath()), overwrite: true);
        }
        catch (Exception ex)
        {
            log($"settings: SAVE FAILED to {path} ({ex.GetType().Name}: {ex.Message})");
            throw;
        }
        log($"settings: saved to {path} {Describe(normalized)}");
    }

    /// <summary>
    /// `key=value` → 新設定。**唯一擁有者**（`--settings-set` 與設定頁共用）。回傳 false 代表這個鍵或值
    /// 不接受，呼叫端必須**把它說出來**；靜默忽略會讓使用者以為設定生效了。
    /// </summary>
    public static bool TryApply(Settings current, string key, string value, Action<string> log, out Settings next)
    {
        next = current;
        string v = value.Trim();
        switch (key.Trim().ToLowerInvariant())
        {
            case "jpgquality":
                if (!int.TryParse(v, out int jq)) { log($"settings: reject jpgQuality='{value}' (not an integer)"); return false; }
                next = current with { JpgQuality = jq };
                return true;
            case "webpquality":
                if (!int.TryParse(v, out int wq)) { log($"settings: reject webpQuality='{value}' (not an integer)"); return false; }
                next = current with { WebpQuality = wq };
                return true;
            case "capturetimeoutseconds" or "capture":
                if (!int.TryParse(v, out int cs)) { log($"settings: reject captureTimeoutSeconds='{value}' (not an integer)"); return false; }
                next = current with { CaptureTimeoutSeconds = cs };
                return true;
            case "triggerkey":
                if (!TryResolveTriggerKey(v, out TriggerKeyInfo info, out string why))
                {
                    log($"settings: reject triggerKey='{value}' ({why})");
                    return false;
                }
                if (info.Warning != null) log($"settings: WARNING {info.Warning}");
                next = current with { TriggerKey = info.Name };
                return true;
            case "startwithwindows":
                if (!bool.TryParse(v, out bool sw)) { log($"settings: reject startWithWindows='{value}' (not true/false)"); return false; }
                next = current with { StartWithWindows = sw };
                return true;
            case "diagnostics":
                if (!bool.TryParse(v, out bool dg)) { log($"settings: reject diagnostics='{value}' (not true/false)"); return false; }
                next = current with { Diagnostics = dg };
                return true;
            case "outputfolder":
                // 規格 §9.5（2026-10-05 使用者裁定）的三種寫法：空字串或 `source`／`sibling`＝
                // **來源檔旁邊**（預設）、`desktop`＝桌面、其他＝資料夾路徑。
                // ⚠ 設定頁的下拉顯示的是「來源檔旁邊／桌面／自訂資料夾」這些**標籤**，
                // 但寫回檔案的必須是這三種寫法之一——`ResolveOutputDirectory()` 只認它們。
                if (v.Length == 0 || v.Equals("source", StringComparison.OrdinalIgnoreCase) ||
                    v.Equals("sibling", StringComparison.OrdinalIgnoreCase))
                {
                    next = current with { OutputFolder = "" };     // 正規寫法是空字串，不是 "source"
                    return true;
                }
                if (v.Equals("desktop", StringComparison.OrdinalIgnoreCase))
                {
                    next = current with { OutputFolder = "desktop" };
                    return true;
                }
                if (!Path.IsPathRooted(v))
                {
                    // 相對路徑的意思取決於「誰在跑、從哪裡跑」，而檔案**落在哪裡**不可以取決於那種事
                    log($"settings: reject outputFolder='{value}' " +
                        "(not an absolute folder path; use '', 'desktop' or a full path)");
                    return false;
                }
                try
                {
                    next = current with
                    {
                        OutputFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(v))
                    };
                    return true;
                }
                catch (Exception ex)
                {
                    log($"settings: reject outputFolder='{value}' (not a usable path: {ex.GetType().Name})");
                    return false;
                }
            case "compressionmode":
                // 「有哪些等級」只有 CompressionPolicy 說了算；這裡只負責把認得的收斂成正規寫法、
                // 把不認得的**拒絕並說出來**（靜默退回標準＝使用者以為改成功了，而它沒有）。
                string mode = CompressionPolicy.CanonicalModeOrEmpty(v);
                if (mode.Length == 0)
                {
                    log($"settings: reject compressionMode='{value}' " +
                        $"(expected one of {string.Join(", ", CompressionPolicy.Modes)})");
                    return false;
                }
                next = current with { CompressionMode = mode };
                return true;
            case "compressiontargetkb" or "compressiontarget":
                // ⚠ 這個 case **一個例外都不准丟**（本專案才因為設定頁的例外殺掉 UI 執行緒付過代價）。
                // 非數字／負數／0／荒謬的大值都是「拒絕 ＋ 大聲說」——設定頁會把它顯示成
                // 「還不能存」，而不是讓儲存鈕按下去之後炸開。
                if (!int.TryParse(v, out int targetKb))
                {
                    log($"settings: reject compressionTargetKb='{value}' (not an integer number of KB)");
                    return false;
                }
                if (targetKb < CompressionPolicy.MinTargetKb)
                {
                    log($"settings: reject compressionTargetKb={targetKb} " +
                        $"(a limit must be at least {CompressionPolicy.MinTargetKb} KB; 0 means \"no limit\", " +
                        "which the compress-to mode cannot act on)");
                    return false;
                }
                if (targetKb > CompressionPolicy.MaxTargetKb)
                {
                    log($"settings: reject compressionTargetKb={targetKb} " +
                        $"(max {CompressionPolicy.MaxTargetKb} KB = 1 GiB; anything above that is larger " +
                        "than any image this product can encode, i.e. not a limit at all)");
                    return false;
                }
                next = current with { CompressionTargetKb = targetKb };
                return true;
            case "language":
                string lang = v.Equals("system", StringComparison.OrdinalIgnoreCase) ? "system"
                            : v.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) || v.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ? "zh-Hant"
                            : v.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : "";
                if (lang == "") { log($"settings: reject language='{value}' (spec 8: system, zh-Hant, en)"); return false; }
                next = current with { Language = lang };
                return true;
            case "emergencyhotkey":
                if (v == "") { log("settings: reject emergencyHotkey='' (spec 8: changeable, or off)"); return false; }
                next = current with { EmergencyHotkey = v };
                return true;
            case "namingcollision":
                // 規格 §8：MVP 固定自動編號 `(1)`，不可改（覆寫／詢問列為未來選項）。
                log($"settings: reject namingCollision='{value}' (spec 8: fixed to auto-suffix in the MVP)");
                return false;
            default:
                log($"settings: reject unknown key '{key}'");
                return false;
        }
    }

    /// <summary>
    /// 觸發鍵名 → VK。**不接受的就說原因**——`Options.ParseVirtualKey` 對未知值會靜默退回空白鍵，
    /// 那在命令列診斷上可以接受，在設定檔上不行（使用者會以為改成功了）。
    /// 規格 §8 允許「空白鍵 / Ctrl+Shift / Shift / 自訂單鍵」；**組合鍵（含 `+`）尚未實作**，
    /// 這裡明講拒絕，不假裝接受。
    /// </summary>
    public static bool TryResolveTriggerKey(string text, out TriggerKeyInfo info, out string reason)
    {
        info = new TriggerKeyInfo(0, "", null);
        reason = "";
        string t = (text ?? "").Trim();
        if (t == "") { reason = "empty"; return false; }

        if (t.Contains('+'))
        {
            reason = "modifier combinations are not implemented yet (spec 8 lists Ctrl+Shift; tracked as an open gap)";
            return false;
        }

        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex) &&
            hex is > 0 and <= 0xFE)
        {
            info = new TriggerKeyInfo(hex, $"0x{hex:X2}", null);
            return true;
        }

        string upper = t.ToUpperInvariant();
        if (upper.StartsWith("VK_")) upper = upper[3..];

        uint vk = upper switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESCAPE" or "ESC" => 0x1B,
            "CAPITAL" or "CAPSLOCK" => 0x14,
            "SHIFT" => 0x10,
            "CTRL" or "CONTROL" => 0x11,
            "ALT" or "MENU" => 0x12,
            "BACKSPACE" => 0x08,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => 0
        };

        if (vk == 0 && upper.Length == 1 && char.IsAsciiLetterOrDigit(upper[0]))
            vk = upper[0];

        if (vk == 0 && upper.Length is 2 or 3 && upper[0] == 'F' &&
            int.TryParse(upper.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            vk = (uint)(0x70 + fn - 1);

        if (vk == 0) { reason = "not a key name this build knows"; return false; }

        // 規格 §8 明文的警告：單獨 Shift 在檔案總管拖曳時代表「移動」，可能誤操作。
        string? warning = vk == 0x10
            ? "Shift is also Explorer's drag-modifier: holding it while dragging means MOVE, which can surprise the user"
            : null;

        info = new TriggerKeyInfo(vk, NameOf(vk), warning);
        return true;
    }

    /// <summary>解析設定檔裡的觸發鍵；解析不了就**明講**並用預設，不讓程式起不來。</summary>
    public static uint ResolveTriggerVk(Settings settings, Action<string> log)
    {
        if (TryResolveTriggerKey(settings.TriggerKey, out TriggerKeyInfo info, out string why)) return info.Vk;
        log($"settings: WARNING triggerKey='{settings.TriggerKey}' unusable ({why}) -> using Space");
        return 0x20;
    }

    /// <summary>VK → 寫回檔案的正式名稱（與鍵帽顯示名一致；認不得的就寫代碼，不假裝認得）。</summary>
    public static string NameOf(uint vk) => vk switch
    {
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Escape",
        0x14 => "CapsLock",
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x08 => "Backspace",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"0x{vk:X2}"
    };
}
