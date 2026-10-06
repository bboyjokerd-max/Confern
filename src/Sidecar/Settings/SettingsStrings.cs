using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// 設定頁（規格 §8）的**全部**使用者可見文字。唯一的字串權威。
///
/// 為什麼集中在這裡（三個理由，缺一不可）：
///   ①XAML 裡不可以出現任何非 ASCII 字面值——檔案的編碼是踩過的坑，UI 文字一律由
///     code-behind 指派，XAML 只留結構；
///   ②規格 §8 的「語言」是**執行期可切換**的設定，字串必須能在視窗開著的時候重刷；
///   ③同一個視窗要有 zh-Hant 與 en 兩套，散在兩處遲早會漂移。
///
/// 這個類別**沒有**狀態、沒有 I/O、沒有 WPF 相依：只有表與純函式。
/// 注意：<see cref="SettingsStore.Describe"/> 的輸出與 <see cref="TriggerKeyInfo.Warning"/>
/// 是**規格文字**（後者由 <c>TryResolveTriggerKey</c> 產生），不經過這裡翻譯，原樣顯示。
/// </summary>
internal static class SettingsStrings
{
    /// <summary>支援的語言代碼（與 <see cref="Settings.Language"/> 的合法值一致）。</summary>
    public const string System = "system";
    public const string ZhHant = "zh-Hant";
    public const string En = "en";

    /// <summary>
    /// 「跟隨系統」的解析。回傳 <c>zh-Hant</c> 或 <c>en</c>，永不回傳 <c>system</c>。
    /// ⚠ 這裡**只**解析這個產品自己的兩套字串。本機的 <c>InvariantGlobalization=true</c>，
    /// <c>CultureInfo</c> 拿不到真實的使用者地區設定，所以走 Win32 的
    /// <c>GetUserDefaultUILanguage</c>（LANGID 的 primary language 0x04 = 中文）。
    /// 解析失敗一律當繁體中文——本產品的原始使用者語言。
    /// </summary>
    public static string Resolve(string? language)
    {
        if (string.Equals(language, ZhHant, StringComparison.OrdinalIgnoreCase)) return ZhHant;
        if (string.Equals(language, En, StringComparison.OrdinalIgnoreCase)) return En;
        return SystemLanguage();
    }

    /// <summary>
    /// 本機的使用者介面語言（LANGID）。**刻意自己宣告而不加進 <c>Native</c>**：
    /// <c>Native</c> 是 <c>Interop/Native.cs</c>（不可改），而這是本檔唯一用到的 Win32 呼叫。
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    private static string SystemLanguage()
    {
        try
        {
            ushort langId = GetUserDefaultUILanguage();
            return (langId & 0x3FF) == 0x04 ? ZhHant : En;
        }
        catch (Exception)
        {
            // 取不到系統語言不是「設定頁打不開」的理由，也不能靜默：預設 zh-Hant 要說出來。
            return ZhHant;
        }
    }

    /// <summary>
    /// 取字串。查不到時**退而求其次**（要求的語言 → zh-Hant → en），
    /// 都查不到就回 <c>!key!</c>——讓漏字在畫面上看得見，而不是顯示空白假裝沒事。
    /// </summary>
    public static string Get(string key, string language)
    {
        string lang = Resolve(language);
        if (Table.TryGetValue(lang, out Dictionary<string, string>? table) &&
            table.TryGetValue(key, out string? text))
            return text;

        if (!string.Equals(lang, ZhHant, StringComparison.OrdinalIgnoreCase) &&
            Table[ZhHant].TryGetValue(key, out string? fallback))
            return fallback;

        return $"!{key}!";
    }

    /// <summary>zh-Hant 是**基準表**：每個 key 都必須在這裡有值，en 可以缺（缺了會退回 zh-Hant）。</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Table = new(StringComparer.Ordinal)
    {
        [ZhHant] = new(StringComparer.Ordinal)
        {
            // ---- 視窗外框 ----
            ["window.title"] = "Confern 設定",
            ["window.subtitle"] = "規格 §8 的九個設定，加上規格 §9.5 的輸出資料夾與規格 §9.4 的壓縮方式。變更按下「儲存」後才寫入檔案。",

            // ---- 頁面 ----
            ["page.behaviour"] = "行為",
            ["page.quality"] = "影像品質與逾時",
            ["page.safety"] = "安全與診斷",
            ["page.behaviourDesc"] = "觸發鍵按住時，接收層才會接住拖放；開機啟動尚未生效。",
            ["page.qualityDesc"] = "轉檔品質、壓縮方式與接收層停留時間。",
            ["page.safetyDesc"] = "標記「尚未生效」者只儲存設定值。",

            // ---- 觸發鍵 ----
            ["field.triggerKey"] = "觸發鍵",
            ["btn.capture"] = "按下新鍵",
            ["btn.capturing"] = "請按鍵…",
            ["key.captureHint"] = "請按一個鍵",
            ["key.captureHintModifier"] = "修飾鍵不能單獨當觸發鍵",
            ["key.captureHintUnsupported"] = "這個鍵尚未支援",
            ["key.unknown"] = "尚未支援",
            ["key.customLabel"] = "自訂單鍵（{0}）",

            // ---- 開機啟動 ----
            ["field.startWithWindows"] = "開機啟動",
            ["field.language"] = "語言",

            // ---- 輸出資料夾（規格 §9.5）----
            // ⚠ 這些是**畫面上的標籤**，不是寫進設定檔的值：寫進檔案的永遠是
            // `/ 空字串（來源旁邊）/ desktop / 資料夾路徑`，由 SettingsStore.TryApply 決定。
            ["field.outputFolder"] = "輸出資料夾",
            ["out.source"] = "來源檔旁邊",
            ["out.desktop"] = "桌面",
            ["out.custom"] = "自訂資料夾",
            ["out.needPath"] = "自訂資料夾請填完整的路徑（例如 C:\\輸出）。",

            // ---- 品質 ----
            ["field.jpgQuality"] = "JPG 品質",
            ["field.webpQuality"] = "WebP 品質",

            // ---- 壓縮方式（M3 第三顆扇形，2026-10-06 使用者裁定：等級走設定頁）----
            // ⚠ 這些是**畫面上的標籤**，不是寫進設定檔的值：寫進檔案的永遠是
            // `small / standard / high / target-size`，由 SettingsStore.TryApply 決定。
            // 上限那一格與輸出資料夾同一條規矩：值存 KB，KB／MB 只是畫面的寫法。
            ["field.compressionMode"] = "壓縮方式",
            ["field.compressionTargetKb"] = "壓縮上限",
            ["compress.small"] = "小檔",
            ["compress.standard"] = "標準",
            ["compress.high"] = "高畫質",
            ["compress.targetSize"] = "壓縮到",
            ["compress.needSize"] = "「壓縮到」請填一個 1–1048576 之間的上限，並選 KB 或 MB。",
            ["unit.kb"] = "KB",
            ["unit.mb"] = "MB",

            // ---- 命名衝突 ----
            ["field.namingCollision"] = "命名衝突",
            ["naming.value"] = "自動編號（1）、（2）…",
            ["naming.reason"] = "MVP 固定：永不覆寫、永不詢問；覆寫與詢問列為未來選項。",
            // ---- 逾時 ----
            ["field.timeout"] = "接收層逾時",
            ["unit.seconds"] = " 秒",

            // ---- 緊急停用 ----
            ["field.emergencyHotkey"] = "緊急停用熱鍵",

            // ---- 診斷 ----
            ["field.diagnostics"] = "診斷記錄",

            // ---- 尚未生效 ----
            ["stub.badge"] = "尚未生效",
            ["stub.emergencyHotkey"] = "尚未生效：全域熱鍵還沒接上，這裡只存設定值，按鍵不會有反應。",

            // ---- 開關 ----
            ["toggle.on"] = "開",
            ["toggle.off"] = "關",

            // ---- 語言選項 ----
            ["lang.system"] = "跟隨系統",
            ["lang.zh-Hant"] = "繁體中文",
            ["lang.en"] = "English",

            // ---- 底部 ----
            ["btn.save"] = "儲存",
            ["btn.cancel"] = "取消",
            ["path.prefix"] = "設定檔：",
            ["path.missing"] = "（尚未建立，按「儲存」後寫入）",
            ["status.idle"] = "尚未變更。",
            ["status.dirty"] = "有未儲存的變更。",
            ["status.saved"] = "已儲存：{0}",
            ["status.saveFailed"] = "儲存失敗（設定未變更）：{0}",
            ["status.badField"] = "無法儲存：{0} 不接受目前的值（{1}）——請先修正再儲存。",
            ["status.openRejected"] = "設定頁已開啟，忽略這次開啟要求。",
            ["status.alreadyOpen"] = "設定頁已經開著。",
            ["status.openFailed"] = "設定頁開啟失敗：{0}",
            ["status.saveFailedOnOpen"] = "載入設定值失敗：{0}",

            // ---- 觸發鍵的顯示名 ----
            ["vk.Space"] = "空白鍵",
            ["vk.Tab"] = "Tab",
            ["vk.CapsLock"] = "CapsLock",
            ["vk.Escape"] = "Escape",
            ["vk.Shift"] = "Shift",
            ["vk.Enter"] = "Enter",
            ["vk.Backspace"] = "Backspace",
            ["vk.ArrowUp"] = "↑",
            ["vk.ArrowDown"] = "↓",
            ["vk.ArrowLeft"] = "←",
            ["vk.ArrowRight"] = "→"
        },
        [En] = new(StringComparer.Ordinal)
        {
            ["window.title"] = "Confern Settings",
            ["window.subtitle"] = "The nine settings of spec section 8, plus the output folder of section 9.5. Changes are written only when you press Save.",

            ["page.behaviour"] = "Behaviour",
            ["page.quality"] = "Image quality and timeouts",
            ["page.safety"] = "Safety and diagnostics",
            ["page.behaviourDesc"] = "The capture layer only accepts a drop while the trigger key is held.",
            ["page.qualityDesc"] = "Conversion quality and how long the capture layer stays up.",
            ["page.safetyDesc"] = "Anything marked NOT ACTIVE only stores the value for now.",

            ["field.triggerKey"] = "Trigger key",
            ["btn.capture"] = "Press a new key",
            ["btn.capturing"] = "Press a key...",
            ["key.captureHint"] = "press a key",
            ["key.captureHintModifier"] = "a modifier cannot be a trigger key on its own",
            ["key.captureHintUnsupported"] = "that key is not supported yet",
            ["key.unknown"] = "not supported",
            ["key.customLabel"] = "Custom key ({0})",

            ["field.startWithWindows"] = "Start with Windows",
            ["field.language"] = "Language",

            // Output folder (spec 9.5). Labels only: the file stores '' / 'desktop' / a path.
            ["field.outputFolder"] = "Output folder",
            ["out.source"] = "Next to the source",
            ["out.desktop"] = "Desktop",
            ["out.custom"] = "Custom folder",
            ["out.needPath"] = "Enter a full path for the custom folder (for example C:\\Output).",

            ["field.jpgQuality"] = "JPG quality",
            ["field.webpQuality"] = "WebP quality",

            // Compression (M3 third sector). Labels only: the file stores the canonical spellings.
            ["field.compressionMode"] = "Compression",
            ["field.compressionTargetKb"] = "Size limit",
            ["compress.small"] = "Small file",
            ["compress.standard"] = "Standard",
            ["compress.high"] = "High quality",
            ["compress.targetSize"] = "Compress to",
            ["compress.needSize"] = "For Compress to, enter a limit between 1 and 1048576 and pick KB or MB.",
            ["unit.kb"] = "KB",
            ["unit.mb"] = "MB",

            ["field.namingCollision"] = "Name collision",
            ["naming.value"] = "Auto-number (1), (2), ...",
            ["naming.reason"] = "Fixed in the MVP: never overwrite, never ask.",

            ["field.timeout"] = "Capture timeout",
            ["unit.seconds"] = " s",

            ["field.emergencyHotkey"] = "Emergency hotkey",

            ["field.diagnostics"] = "Diagnostic logging",

            ["stub.badge"] = "NOT ACTIVE",
            ["stub.emergencyHotkey"] = "Not active yet: stored only; the global hotkey is not wired up.",

            ["toggle.on"] = "On",
            ["toggle.off"] = "Off",

            ["lang.system"] = "Follow system",
            ["lang.zh-Hant"] = "Traditional Chinese",
            ["lang.en"] = "English",

            ["btn.save"] = "Save",
            ["btn.cancel"] = "Cancel",
            ["path.prefix"] = "Settings file: ",
            ["path.missing"] = "(not created yet; written when you press Save)",
            ["status.idle"] = "No changes yet.",
            ["status.dirty"] = "Unsaved changes.",
            ["status.saved"] = "Saved: {0}",
            ["status.saveFailed"] = "Save failed (settings unchanged): {0}",
            ["status.badField"] = "Cannot save: {0} does not accept the current value ({1}). Fix it and save again.",
            ["status.openRejected"] = "The settings window is already open; this open request was ignored.",
            ["status.alreadyOpen"] = "The settings window is already open.",
            ["status.openFailed"] = "Could not open the settings window: {0}",
            ["status.saveFailedOnOpen"] = "Could not load the settings: {0}",

            ["vk.Space"] = "Space",
            ["vk.Tab"] = "Tab",
            ["vk.CapsLock"] = "CapsLock",
            ["vk.Escape"] = "Escape",
            ["vk.Shift"] = "Shift",
            ["vk.Enter"] = "Enter",
            ["vk.Backspace"] = "Backspace",
            ["vk.ArrowUp"] = "Up",
            ["vk.ArrowDown"] = "Down",
            ["vk.ArrowLeft"] = "Left",
            ["vk.ArrowRight"] = "Right"
        }
    };
}
