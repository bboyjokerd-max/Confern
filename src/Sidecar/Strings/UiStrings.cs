using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// **全產品**的使用者可見文字：設定頁（規格 §8）＋ 轉檔時真正會看到的畫面（輪盤、托盤、失敗原因）。
/// 唯一的字串權威。
///
/// 為什麼集中在這裡（三個理由，缺一不可）：
///   ①XAML 裡不可以出現任何非 ASCII 字面值——檔案的編碼是踩過的坑，UI 文字一律由
///     code-behind 指派，XAML 只留結構；
///   ②規格 §8 的「語言」是**執行期可切換**的設定，字串必須能在視窗開著的時候重刷；
///   ③同一句話要有 zh-Hant 與 en 兩套，散在兩處遲早會漂移。
///
/// 2026-10-21：從「設定頁專用」升級成**全產品**唯一字串權威（原本叫 SettingsStrings）。
/// 扇區標籤（<see cref="ToolDescriptor.Label"/>）仍然不進這張表——那是大寫英文短詞，
/// 與格式名同一個視覺階層，80×24 px 的扇區上放中文會糊。
///
/// 這個類別**沒有**狀態、沒有 I/O、沒有 WPF 相依：只有表與純函式。
/// 注意：<see cref="SettingsStore.Describe"/> 的輸出與 <see cref="TriggerKeyInfo.Warning"/>
/// 是**規格文字**（後者由 <c>TryResolveTriggerKey</c> 產生），不經過這裡翻譯，原樣顯示。
/// </summary>
internal static class UiStrings
{
    /// <summary>支援的語言代碼（與 <see cref="Settings.Language"/> 的合法值一致）。</summary>
    public const string System = "system";
    public const string ZhHant = "zh-Hant";
    public const string En = "en";
    /// <summary>
    /// 目前的語言（已解析成 <see cref="ZhHant"/> 或 <see cref="En"/>）。
    /// 由 <see cref="SettingsStore"/> 在載入與套用設定的**同一處**設定——與配色同一個模式：
    /// 只有一個寫入點，呼叫端不會各自解讀 <c>system</c>。
    /// </summary>
    public static string Current { get; private set; } = Resolve(System);

    /// <summary>
    /// 設定目前語言。傳入不認得的值**不會靜默退回**：<see cref="Resolve"/> 會解析成系統語言，
    /// 而設定頁那一端（<c>TryApply</c> 的 <c>case "language"</c>）才是驗證與拒絕的地方——
    /// 這裡只負責「記住已解析的結果」。
    /// </summary>
    public static void SetLanguage(string? language) => Current = Resolve(language);

    /// <summary>取目前語言的字串（產品畫面走這一個多載，不必把語言一路傳下去）。</summary>
    public static string Get(string key) => Get(key, Current);

    /// <summary>取字串並填參數。<see cref="Get(string)"/> 的格式化版本。</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(Get(key), args);

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
["field.palette"] = "配色",
            ["crop.directionPrefix"] = "方向：",
            ["ratio.free"] = "自由",
            ["ratio.square"] = "1:1",
            ["ratio.wide"] = "16:9",
            ["ratio.tall"] = "9:16",
            ["ratio.classic"] = "4:3",
            ["ratio.classicTall"] = "3:4",
            ["ratio.original"] = "原比例",
            ["ratio.originalFlipped"] = "原比例 90°",
            ["merge.ok"] = "確認 (Enter)",
            ["merge.cancel"] = "取消 (Esc)",
            ["merge.title"] = "合併 PDF：排順序",
            ["merge.hint"] = "上面的順序就是要合併的順序（第 1 個檔的頁面會在最前面）。拖曳右邊的把手可以上下移動，垃圾桶可以移除。確認之後才合併，取消不會產生任何檔案。",
            ["merge.up"] = "上",
            ["merge.down"] = "下",
            ["merge.pageSize"] = "頁面大小",
            ["merge.sizeOriginal"] = "各自原始尺寸",
            ["merge.sizeFirst"] = "統一成第一張",
            ["merge.sizeA4"] = "A4 置中留邊",
            ["merge.range"] = "顯示第 {0}-{1} 列，共 {2} 列",
            ["merge.pagesTotal"] = "合計 {0} 頁",
            ["merge.pagesTotalOne"] = "合計 {0} 頁",
            ["merge.pagesUnknown"] = "合計頁數未知（{0} 個檔讀不到頁數）",
            ["merge.pagesOf"] = "{0} 頁",
            ["merge.pagesOfOne"] = "{0} 頁",
            ["merge.pagesUnreadable"] = "頁數讀不到",
            ["merge.gripTip"] = "按住這裡上下拖曳來調整順序",
            ["merge.remove"] = "移除",
            ["merge.removeTip"] = "把這一份從合併清單裡拿掉",
            ["merge.rowDragTip"] = "拖曳這一列可以調整順序",
            ["merge.errMove"] = "移動時發生未預期的錯誤（{0}）——順序沒有改變",
            ["merge.errLastRemove"] = "至少要留一份檔案才能合併——最後這一份不能移除（要放棄就按 Esc 取消）",
            ["merge.errRemove"] = "移除時發生未預期的錯誤（{0}）——清單沒有改變",
            ["merge.errEmpty"] = "沒有任何檔案可以合併——請至少留一份，或按 Esc 取消",
            ["merge.errConfirm"] = "確認時發生未預期的錯誤（{0}）——沒有任何東西被送出",
            ["crop.directionNone"] = "方向",
            ["crop.wide"] = "橫式",
            ["crop.tall"] = "直式",
            ["crop.ok"] = "確認 (Enter)",
            ["crop.cancel"] = "取消 (Esc)",
            ["crop.title"] = "裁切",
            ["crop.hint"] = "在圖上拖一個矩形（可移動、可拉四邊與四角）· Enter 確認 · Esc 取消",
            ["crop.ratio"] = "比例",
            ["crop.batchNote"] = "（同一比例會套用到這一批的每一張圖，每一張依自己的尺寸換算）",
            ["crop.infoBatch"] = "　（這一批共 {0} 張，每一張依自己的尺寸換算）",
            ["crop.selection"] = "選取 {0}×{1} px　正規化 {2},{3},{4},{5}",
            ["crop.tipDirection"] = "目前的構圖是{0}。按下去換成另一個方向（{1} ↔ {2}）。",
            ["crop.tipNoDirection"] = "{0}沒有方向可以換——只有 16:9／4:3／原比例 這一類有兩個方向。",
            ["crop.noteRestored"] = "比例＝{0}：矩形還原為 {1}×{2} px（翻回切換前的大小）",
            ["crop.noteFlipped"] = "比例＝{0}：矩形由 {1}×{2} 翻轉為 {3}×{4} px（保留長邊與中心）",
            ["crop.noteUnchanged"] = "比例＝{0}：矩形不變",
            ["crop.noteAdjusted"] = "比例＝{0}：矩形由 {1}×{2} 調整為 {3}×{4} px",
            ["crop.errDirection"] = "切換方向時發生未預期的錯誤（{0}）——已忽略",
            ["crop.errRatio"] = "切換比例時發生未預期的錯誤（{0}）——已忽略",
            ["crop.errDragStart"] = "拖曳開始時發生未預期的錯誤（{0}）——已忽略，視窗仍可使用",
            ["crop.errDragMove"] = "拖曳時發生未預期的錯誤（{0}）——已停止這次拖曳，視窗仍可使用",
            ["crop.errEmpty"] = "選取範圍等於整張圖，沒有東西可以裁——請拖出一個比較小的矩形（或按 Esc 取消）",
            ["crop.errConfirm"] = "確認時發生未預期的錯誤（{0}）——沒有任何東西被送出",
            ["crop.selftestEmpty"] = "自測：矩形 {0},{1},{2},{3} 在這個尺寸上是空的",

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
            ["vk.ArrowRight"] = "→",
            // ── 2026-10-21：轉檔時真正會看到的畫面（工具輪盤／托盤／失敗原因）──
            ["tool.rotate90"] = "轉 90°",
            ["tool.stripMetadata"] = "清除",
            ["tool.compress"] = "壓縮",
            ["tool.crop"] = "裁切",
            ["tool.readQr"] = "讀 QR",
            ["tool.mergePdf"] = "合併",
            ["tool.removeBg"] = "去背",

            ["wheel.holdToConvert"] = "按住觸發鍵",

            ["tray.openSettings"] = "開啟設定",
            ["tray.pause"] = "暫停轉換",
            ["tray.resume"] = "繼續轉換",
            ["tray.exit"] = "結束",

            ["progress.moreHidden"] = "…還有 {0} 筆",

            ["format.moreKinds"] = "等 {0} 種",
            ["format.example"] = "（例如 {0}）",

            ["err.unsupported"] = "不支援的格式",
            ["err.decodeFailed"] = "無法解碼",
            ["err.targetUnavailable"] = "此格式目前無法輸出",
            ["err.sourceMissing"] = "來源檔案已不存在",
            ["err.unreadable"] = "無法讀取來源檔案",
            ["err.writeDenied"] = "無法寫入此資料夾",
            ["err.noSpace"] = "空間不足",
            ["err.locked"] = "檔案被鎖定",
            ["err.timeout"] = "雲端檔案下載逾時",
            ["err.targetTooSmall"] = "已達最低品質仍超過指定大小",
            ["err.cropRectEmpty"] = "選取範圍是空的",
            ["err.cropNothingToDo"] = "選取範圍等於整張圖",
            ["err.writeConflict"] = "輸出檔名被其他行程占用",
            ["err.qrNotFound"] = "圖片中找不到 QR 碼",
            ["err.mergeFailed"] = "無法合併這個 PDF",
            ["err.mergeNeedsPassword"] = "這個 PDF 需要密碼才能開啟，請先解除密碼再合併",
            ["err.noSubjectFound"] = "這張圖找不到人像主體，無法去背",
            ["err.modelUnavailable"] = "去背模型尚未取得，請執行 tools\\get-rembg-model.ps1",
            ["err.modelCorrupt"] = "去背模型檔損壞或下載不完整，請重新取得",
            ["err.generic"] = "轉檔失敗",
            ["err.withPosition"] = "{0}（第 {1} 個檔） {2}",
            ["err.summaryMore"] = "{0}（共 {1} 個）"
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
["field.palette"] = "Palette",
            ["crop.directionPrefix"] = "Direction: ",
            ["ratio.free"] = "Free",
            ["ratio.square"] = "1:1",
            ["ratio.wide"] = "16:9",
            ["ratio.tall"] = "9:16",
            ["ratio.classic"] = "4:3",
            ["ratio.classicTall"] = "3:4",
            ["ratio.original"] = "Original",
            ["ratio.originalFlipped"] = "Original 90°",
            ["merge.ok"] = "OK (Enter)",
            ["merge.cancel"] = "Cancel (Esc)",
            ["merge.title"] = "Merge PDFs: set the order",
            ["merge.hint"] = "The order above is the merge order (page 1 of the first file comes first). Drag the handle on the right to move a row up or down, or use the bin button to remove it. Nothing is merged until you confirm, and cancelling produces no file.",
            ["merge.up"] = "Up",
            ["merge.down"] = "Down",
            ["merge.pageSize"] = "Page size",
            ["merge.sizeOriginal"] = "Each file's own size",
            ["merge.sizeFirst"] = "Match the first file",
            ["merge.sizeA4"] = "A4, centred",
            ["merge.range"] = "Showing rows {0}-{1} of {2}",
            ["merge.pagesTotal"] = "{0} pages in total",
            ["merge.pagesTotalOne"] = "{0} page in total",
            ["merge.pagesUnknown"] = "Total unknown ({0} files have no readable page count)",
            ["merge.pagesOf"] = "{0} pages",
            ["merge.pagesOfOne"] = "{0} page",
            ["merge.pagesUnreadable"] = "page count unreadable",
            ["merge.gripTip"] = "Drag up or down here to reorder",
            ["merge.remove"] = "Remove",
            ["merge.removeTip"] = "Drop this file from the merge list",
            ["merge.rowDragTip"] = "Drag this row to reorder",
            ["merge.errMove"] = "Unexpected error while moving ({0}) — the order is unchanged",
            ["merge.errLastRemove"] = "A merge needs at least one file — the last one cannot be removed (press Esc to give up)",
            ["merge.errRemove"] = "Unexpected error while removing ({0}) — the list is unchanged",
            ["merge.errEmpty"] = "There is nothing to merge — keep at least one file, or press Esc",
            ["merge.errConfirm"] = "Unexpected error while confirming ({0}) — nothing was sent",
            ["crop.directionNone"] = "Direction",
            ["crop.wide"] = "Landscape",
            ["crop.tall"] = "Portrait",
            ["crop.ok"] = "OK (Enter)",
            ["crop.cancel"] = "Cancel (Esc)",
            ["crop.title"] = "Crop",
            ["crop.hint"] = "Drag a rectangle on the image (move it, or pull its edges and corners) · Enter to confirm · Esc to cancel",
            ["crop.ratio"] = "Ratio",
            ["crop.batchNote"] = "(the same ratio applies to every image in this batch, each computed from its own size)",
            ["crop.infoBatch"] = "　({0} images in this batch, each from its own size)",
            ["crop.selection"] = "Selected {0}×{1} px　normalised {2},{3},{4},{5}",
            ["crop.tipDirection"] = "The framing is {0} right now. Press to switch to the other direction ({1} ↔ {2}).",
            ["crop.tipNoDirection"] = "{0} has no other direction — only ratios like 16:9 / 4:3 / original have two.",
            ["crop.noteRestored"] = "Ratio = {0}: rectangle restored to {1}×{2} px (back to the size before the flip)",
            ["crop.noteFlipped"] = "Ratio = {0}: rectangle flipped from {1}×{2} to {3}×{4} px (long side and centre kept)",
            ["crop.noteUnchanged"] = "Ratio = {0}: rectangle unchanged",
            ["crop.noteAdjusted"] = "Ratio = {0}: rectangle adjusted from {1}×{2} to {3}×{4} px",
            ["crop.errDirection"] = "Unexpected error while switching direction ({0}) — ignored",
            ["crop.errRatio"] = "Unexpected error while switching ratio ({0}) — ignored",
            ["crop.errDragStart"] = "Unexpected error while starting the drag ({0}) — ignored, the window still works",
            ["crop.errDragMove"] = "Unexpected error while dragging ({0}) — this drag stopped, the window still works",
            ["crop.errEmpty"] = "The selection is the whole image, so there is nothing to crop — drag a smaller rectangle (or press Esc)",
            ["crop.errConfirm"] = "Unexpected error while confirming ({0}) — nothing was sent",
            ["crop.selftestEmpty"] = "selftest: rectangle {0},{1},{2},{3} is empty at this size",

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
            ["vk.ArrowRight"] = "Right",
            // 2026-10-21: the surface you actually see while converting (wheel / tray / failure reasons)
            ["tool.rotate90"] = "Rotate",
            ["tool.stripMetadata"] = "Clean",
            ["tool.compress"] = "Shrink",
            ["tool.crop"] = "Crop",
            ["tool.readQr"] = "Scan",
            ["tool.mergePdf"] = "Merge",
            ["tool.removeBg"] = "Cutout",

            ["wheel.holdToConvert"] = "Hold the trigger key",

            ["tray.openSettings"] = "Open settings",
            ["tray.pause"] = "Pause conversion",
            ["tray.resume"] = "Resume conversion",
            ["tray.exit"] = "Quit",

            ["progress.moreHidden"] = "…{0} more",

            ["format.moreKinds"] = "and {0} more",
            ["format.example"] = " (e.g. {0})",

            ["err.unsupported"] = "Unsupported format",
            ["err.decodeFailed"] = "Could not decode",
            ["err.targetUnavailable"] = "This format cannot be written right now",
            ["err.sourceMissing"] = "The source file no longer exists",
            ["err.unreadable"] = "Could not read the source file",
            ["err.writeDenied"] = "Cannot write to this folder",
            ["err.noSpace"] = "Not enough disk space",
            ["err.locked"] = "The file is locked",
            ["err.timeout"] = "Timed out downloading the cloud file",
            ["err.targetTooSmall"] = "Even the lowest quality is over the target size",
            ["err.cropRectEmpty"] = "The selection is empty",
            ["err.cropNothingToDo"] = "The selection is the whole image",
            ["err.writeConflict"] = "Another process is holding the output name",
            ["err.qrNotFound"] = "No QR code found in the image",
            ["err.mergeFailed"] = "Could not merge this PDF",
            ["err.mergeNeedsPassword"] = "This PDF needs a password; remove it before merging",
            ["err.noSubjectFound"] = "No subject found, so there is nothing to cut out",
            ["err.modelUnavailable"] = "The background-removal model is missing; run tools\\get-rembg-model.ps1",
            ["err.modelCorrupt"] = "The background-removal model is damaged or incomplete; download it again",
            ["err.generic"] = "Conversion failed",
            ["err.withPosition"] = "{0} (file {1}) {2}",
            ["err.summaryMore"] = "{0} ({1} files)"
        }
    };
}
