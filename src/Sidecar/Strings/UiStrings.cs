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
///   ③同一句話要有五套（zh-Hant／zh-Hans／ja／ko／en），散在五處遲早會漂移。
///
/// 2026-10-21：從「設定頁專用」升級成**全產品**唯一字串權威（原本叫 SettingsStrings）。
/// 扇區標籤也在這裡了（`tool.label.&lt;tool id&gt;`，見 <see cref="ToolDescriptor.SectorLabel"/>）：
/// 同一天實測發現「80×24 放中文會糊」只對一半——寬度不是限制，真正的限制是 11px 的
/// 筆畫密度，所以改成在地化 ＋ 字級 12.7px ＋ 挑筆畫少的措辭。
/// ⚠ <see cref="ToolDescriptor.Label"/>（大寫英文名）**刻意不進**這張表：它會進證據行，
/// 驗收腳本拿它當斷言，跟著介面語言走就會因為跑測試那台機器的系統語言而紅。
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
    public const string ZhHans = "zh-Hans";
    public const string Ja = "ja";
    public const string Ko = "ko";
    public const string En = "en";
    /// <summary>
    /// 目前的語言（已解析成 <see cref="ZhHant"/>／<see cref="ZhHans"/>／<see cref="Ja"/>／<see cref="Ko"/>／<see cref="En"/> 之一）。
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
    /// 「跟隨系統」的解析。回傳五個語言代碼之一，永不回傳 <c>system</c>。
    /// ⚠ 這裡**只**解析這個產品自己的五套字串。本機的 <c>InvariantGlobalization=true</c>，
    /// <c>CultureInfo</c> 拿不到真實的使用者地區設定，所以走 Win32 的
    /// <c>GetUserDefaultUILanguage</c>：LANGID 低 10 位是主語言（0x04 中文、0x11 日文、
    /// 0x12 韓文），高 6 位是子語言（中文的 0x01 台灣、0x03 香港 → 繁；其餘 → 簡）。
    /// 解析失敗一律當繁體中文——本產品的原始使用者語言。
    /// </summary>
    public static string Resolve(string? language)
    {
        if (string.Equals(language, ZhHant, StringComparison.OrdinalIgnoreCase)) return ZhHant;
        if (string.Equals(language, ZhHans, StringComparison.OrdinalIgnoreCase)) return ZhHans;
        if (string.Equals(language, Ja, StringComparison.OrdinalIgnoreCase)) return Ja;
        if (string.Equals(language, Ko, StringComparison.OrdinalIgnoreCase)) return Ko;
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
            // LANGID 的佈局：低 10 位是主語言，高 6 位是子語言。
            int primary = langId & 0x3FF;
            int sub = (langId >> 10) & 0x3F;
            switch (primary)
            {
                // 中文：子語言 0x01 台灣、0x03 香港、0x05 澳門 = 繁；0x02 中國、0x04 新加坡 = 簡。
                // ⚠ 2026-10-21 之前這裡一律回 zh-Hant（因為當時只有繁中）。現在有簡中表了，
                //   簡體系統就該拿簡體字，否則大陸使用者的第一印象是「裝到繁體版」。
                case 0x04:
                    return sub is 0x01 or 0x03 ? ZhHant : ZhHans;
                case 0x11: return Ja;    // 日文
                case 0x12: return Ko;    // 韓文
                default: return En;
            }
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

        /// <summary>
    /// zh-Hant 是**基準表**：每個 key 都必須在這裡有值，其餘四張可以缺（缺了會退回 zh-Hant）。
    /// ⚠ 2026-10-21 起五張表都是 196 鍵、逐鍵對齊——退回只是保險，不是常態。
    /// 加鍵時**先加 zh-Hant 再加其餘四張**，順序也照基準表，否則人工比對會失效。
    /// </summary>
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
            ["qr.hint"] = "這是圖片裡解出來的內容（沒有產生任何檔案）。可以選取，Ctrl+C 或下面的按鈕複製。",
            ["qr.copy"] = "複製",
            ["qr.title"] = "讀 QR",
            ["qr.notUrl"] = "{0}　（不是網址，所以只顯示在這裡）",
            ["note.capped"] = "資料夾展開：已達上限，只取前 {0} 個檔",
            ["note.folderFiles"] = "資料夾展開：讀到 {0} 個檔（跳過 {1} 個）",
            ["note.tooDeep"] = "另有 {0} 個資料夾在 {1} 層以下，未收",
            ["fmt.mixedKinds"] = "混合選取，請選同類別：",
            ["fmt.listSeparator"] = "、",
            ["svc.interrupted"] = "轉檔服務中斷",
            ["svc.versionMismatchRestarted"] = "轉檔服務版本不符，已重新啟動",
            ["svc.versionMismatch"] = "轉檔服務版本不符",
            ["svc.error"] = "轉檔服務錯誤",
            ["svc.timeout"] = "轉檔逾時",
            ["svc.cannotStart"] = "轉檔服務無法啟動",
            ["progress.files"] = "{0} 個檔案",
            ["fmt.mixedShort"] = "混合選取，請選同類別",
            ["fmt.noTarget"] = "沒有可轉換的目標格式",
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
            ["lang.zh-Hans"] = "简体中文",
            ["lang.ja"] = "日本語",
            ["lang.ko"] = "한국어",
            ["palette.midnight"] = "暮藍",
            ["palette.graphite"] = "石墨",
            ["palette.citrus"] = "柑橘",
            ["palette.celadon"] = "青瓷",
            ["palette.plum"] = "絳紫",
            ["palette.porcelain"] = "冷瓷",

            // ---- 輪盤扇區標籤（工具）----
            // 扇區只有 80x24px、字級 12.7px：筆畫密的字會糊（2026-10-21 實測，
            // 連通墨塊數壓縮 0.47／旋轉 0.5）。所以這裡挑筆畫少的措辭，
            // 日文用 切抜 而不用 切り抜き。字表外的診斷字串走 ToolDescriptor.Label。
            ["tool.label.rotate90"] = "旋轉",
            ["tool.label.strip-metadata"] = "清除",
            ["tool.label.compress"] = "壓縮",
            ["tool.label.crop"] = "裁切",
            ["tool.label.read-qr"] = "讀 QR",
            ["tool.label.merge-pdf"] = "合併 PDF",
            ["tool.label.remove-bg"] = "去背",

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
        [ZhHans] = new(StringComparer.Ordinal)
        {
            ["window.title"] = "Confern 设置",
            ["window.subtitle"] = "规格 §8 的九项设置，加上规格 §9.5 的输出文件夹与规格 §9.4 的压缩方式。变更在按下「保存」后才会写入文件。",
            ["page.behaviour"] = "行为",
            ["page.quality"] = "图像质量与超时",
            ["page.safety"] = "安全与诊断",
            ["page.behaviourDesc"] = "按住触发键时，接收层才会接住拖放；开机启动尚未生效。",
            ["page.qualityDesc"] = "转换质量、压缩方式与接收层停留时间。",
            ["page.safetyDesc"] = "标记为「尚未生效」的项只保存设置值。",
            ["field.triggerKey"] = "触发键",
            ["btn.capture"] = "按下新键",
            ["btn.capturing"] = "请按键…",
            ["key.captureHint"] = "请按一个键",
            ["key.captureHintModifier"] = "修饰键不能单独作为触发键",
            ["key.captureHintUnsupported"] = "该键尚不支持",
            ["key.unknown"] = "尚不支持",
            ["key.customLabel"] = "自定义单键（{0}）",
            ["field.startWithWindows"] = "开机启动",
            ["field.language"] = "语言",
            ["field.palette"] = "配色",
            ["crop.directionPrefix"] = "方向：",
            ["ratio.free"] = "自由",
            ["ratio.square"] = "1:1",
            ["ratio.wide"] = "16:9",
            ["ratio.tall"] = "9:16",
            ["ratio.classic"] = "4:3",
            ["ratio.classicTall"] = "3:4",
            ["ratio.original"] = "原始宽高比",
            ["ratio.originalFlipped"] = "原始宽高比 90°",
            ["merge.ok"] = "确定 (Enter)",
            ["merge.cancel"] = "取消 (Esc)",
            ["merge.title"] = "合并 PDF：调整顺序",
            ["merge.hint"] = "上面的顺序就是合并的顺序（第 1 个文件的页面会排在最前面）。拖动右侧的把手可以上下移动，垃圾桶按钮可以移除。确认之后才会合并，取消不会产生任何文件。",
            ["merge.up"] = "上",
            ["merge.down"] = "下",
            ["merge.pageSize"] = "页面大小",
            ["merge.sizeOriginal"] = "各自原始尺寸",
            ["merge.sizeFirst"] = "统一成第一张",
            ["merge.sizeA4"] = "A4 居中留边",
            ["merge.range"] = "显示第 {0}-{1} 行，共 {2} 行",
            ["merge.pagesTotal"] = "共 {0} 页",
            ["merge.pagesTotalOne"] = "共 {0} 页",
            ["merge.pagesUnknown"] = "总页数未知（{0} 个文件读不到页数）",
            ["merge.pagesOf"] = "{0} 页",
            ["merge.pagesOfOne"] = "{0} 页",
            ["merge.pagesUnreadable"] = "页数无法读取",
            ["merge.gripTip"] = "按住此处上下拖动以调整顺序",
            ["merge.remove"] = "移除",
            ["merge.removeTip"] = "将这份文件从合并列表中移除",
            ["merge.rowDragTip"] = "拖动此行可调整顺序",
            ["merge.errMove"] = "移动时发生意外错误（{0}）——顺序未改变",
            ["merge.errLastRemove"] = "至少要保留一份文件才能合并——最后这一份不能移除（要放弃请按 Esc 取消）",
            ["merge.errRemove"] = "移除时发生意外错误（{0}）——列表未改变",
            ["merge.errEmpty"] = "没有可合并的文件——请至少保留一份，或按 Esc 取消",
            ["merge.errConfirm"] = "确认时发生意外错误（{0}）——没有提交任何内容",
            ["qr.hint"] = "这是从图片中识别出来的内容（未产生任何文件）。可以选中，用 Ctrl+C 或下面的按钮复制。",
            ["qr.copy"] = "复制",
            ["qr.title"] = "读 QR",
            ["qr.notUrl"] = "{0}　（不是网址，因此只在此处显示）",
            ["note.capped"] = "文件夹展开：已达上限，只取前 {0} 个文件",
            ["note.folderFiles"] = "文件夹展开：读到 {0} 个文件（跳过 {1} 个）",
            ["note.tooDeep"] = "另有 {0} 个文件夹位于 {1} 层以下，未收录",
            ["fmt.mixedKinds"] = "混合选择，请选择同一类型：",
            ["fmt.listSeparator"] = "、",
            ["svc.interrupted"] = "转换服务中断",
            ["svc.versionMismatchRestarted"] = "转换服务版本不匹配，已重新启动",
            ["svc.versionMismatch"] = "转换服务版本不匹配",
            ["svc.error"] = "转换服务错误",
            ["svc.timeout"] = "转换超时",
            ["svc.cannotStart"] = "转换服务无法启动",
            ["progress.files"] = "{0} 个文件",
            ["fmt.mixedShort"] = "混合选择，请选择同一类型",
            ["fmt.noTarget"] = "没有可转换的目标格式",
            ["crop.directionNone"] = "方向",
            ["crop.wide"] = "横向",
            ["crop.tall"] = "纵向",
            ["crop.ok"] = "确定 (Enter)",
            ["crop.cancel"] = "取消 (Esc)",
            ["crop.title"] = "裁剪",
            ["crop.hint"] = "在图像上拖出一个矩形（可移动、可拉伸四边与四角）· Enter 确认 · Esc 取消",
            ["crop.ratio"] = "宽高比",
            ["crop.batchNote"] = "（同一宽高比会应用到本批的每一张图，每一张按自身尺寸换算）",
            ["crop.infoBatch"] = "　（本批共 {0} 张，每一张按自身尺寸换算）",
            ["crop.selection"] = "已选 {0}×{1} px　归一化 {2},{3},{4},{5}",
            ["crop.tipDirection"] = "当前的构图是{0}。点击可切换到另一个方向（{1} ↔ {2}）。",
            ["crop.tipNoDirection"] = "{0}没有可切换的方向——只有 16:9／4:3／原始宽高比 这一类有两个方向。",
            ["crop.noteRestored"] = "宽高比＝{0}：矩形已还原为 {1}×{2} px（恢复为切换前的大小）",
            ["crop.noteFlipped"] = "宽高比＝{0}：矩形已由 {1}×{2} 翻转为 {3}×{4} px（保留长边与中心）",
            ["crop.noteUnchanged"] = "宽高比＝{0}：矩形未改变",
            ["crop.noteAdjusted"] = "宽高比＝{0}：矩形已由 {1}×{2} 调整为 {3}×{4} px",
            ["crop.errDirection"] = "切换方向时发生意外错误（{0}）——已忽略",
            ["crop.errRatio"] = "切换宽高比时发生意外错误（{0}）——已忽略",
            ["crop.errDragStart"] = "开始拖动时发生意外错误（{0}）——已忽略，窗口仍可使用",
            ["crop.errDragMove"] = "拖动时发生意外错误（{0}）——已中止本次拖动，窗口仍可使用",
            ["crop.errEmpty"] = "选区就是整张图，没有可裁剪的内容——请拖出一个更小的矩形（或按 Esc 取消）",
            ["crop.errConfirm"] = "确认时发生意外错误（{0}）——没有提交任何内容",
            ["crop.selftestEmpty"] = "自测：矩形 {0},{1},{2},{3} 在此尺寸下为空",
            ["field.outputFolder"] = "输出文件夹",
            ["out.source"] = "源文件旁边",
            ["out.desktop"] = "桌面",
            ["out.custom"] = "自定义文件夹",
            ["out.needPath"] = "自定义文件夹请填写完整路径（例如 C:\\输出）。",
            ["field.jpgQuality"] = "JPG 质量",
            ["field.webpQuality"] = "WebP 质量",
            ["field.compressionMode"] = "压缩方式",
            ["field.compressionTargetKb"] = "压缩上限",
            ["compress.small"] = "小文件",
            ["compress.standard"] = "标准",
            ["compress.high"] = "高质量",
            ["compress.targetSize"] = "压缩到",
            ["compress.needSize"] = "「压缩到」请填写 1–1048576 之间的上限，并选择 KB 或 MB。",
            ["unit.kb"] = "KB",
            ["unit.mb"] = "MB",
            ["field.namingCollision"] = "命名冲突",
            ["naming.value"] = "自动编号（1）、（2）…",
            ["naming.reason"] = "MVP 固定：永不覆盖、永不询问；覆盖与询问列为后续选项。",
            ["field.timeout"] = "接收层超时",
            ["unit.seconds"] = " 秒",
            ["field.emergencyHotkey"] = "紧急停用热键",
            ["field.diagnostics"] = "诊断日志",
            ["stub.badge"] = "尚未生效",
            ["stub.emergencyHotkey"] = "尚未生效：全局热键还没接上，这里只保存设置值，按键不会有反应。",
            ["toggle.on"] = "开",
            ["toggle.off"] = "关",
            ["lang.system"] = "跟随系统",
            ["lang.zh-Hant"] = "繁體中文",
            ["lang.en"] = "English",
            ["lang.zh-Hans"] = "简体中文",
            ["lang.ja"] = "日本語",
            ["lang.ko"] = "한국어",
            ["palette.midnight"] = "暮蓝",
            ["palette.graphite"] = "石墨",
            ["palette.citrus"] = "柑橘",
            ["palette.celadon"] = "青瓷",
            ["palette.plum"] = "绛紫",
            ["palette.porcelain"] = "冷瓷",

            // ---- 輪盤扇區標籤（工具）----
            // 扇區只有 80x24px、字級 12.7px：筆畫密的字會糊（2026-10-21 實測，
            // 連通墨塊數壓縮 0.47／旋轉 0.5）。所以這裡挑筆畫少的措辭，
            // 日文用 切抜 而不用 切り抜き。字表外的診斷字串走 ToolDescriptor.Label。
            ["tool.label.rotate90"] = "旋转",
            ["tool.label.strip-metadata"] = "清理",
            ["tool.label.compress"] = "压缩",
            ["tool.label.crop"] = "裁剪",
            ["tool.label.read-qr"] = "读 QR",
            ["tool.label.merge-pdf"] = "合并 PDF",
            ["tool.label.remove-bg"] = "抠图",
            ["btn.save"] = "保存",
            ["btn.cancel"] = "取消",
            ["path.prefix"] = "配置文件：",
            ["path.missing"] = "（尚未创建，按「保存」后写入）",
            ["status.idle"] = "尚未修改。",
            ["status.dirty"] = "有未保存的更改。",
            ["status.saved"] = "已保存：{0}",
            ["status.saveFailed"] = "保存失败（设置未更改）：{0}",
            ["status.badField"] = "无法保存：{0} 不接受当前值（{1}）——请先修正再保存。",
            ["status.openRejected"] = "设置页已打开，本次打开请求已忽略。",
            ["status.alreadyOpen"] = "设置页已经打开。",
            ["status.openFailed"] = "设置页打开失败：{0}",
            ["status.saveFailedOnOpen"] = "加载设置值失败：{0}",
            ["vk.Space"] = "空格键",
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
            ["tool.rotate90"] = "旋转 90°",
            ["tool.stripMetadata"] = "清理",
            ["tool.compress"] = "压缩",
            ["tool.crop"] = "裁剪",
            ["tool.readQr"] = "读 QR",
            ["tool.mergePdf"] = "合并",
            ["tool.removeBg"] = "抠图",
            ["wheel.holdToConvert"] = "按住触发键",
            ["tray.openSettings"] = "打开设置",
            ["tray.pause"] = "暂停转换",
            ["tray.resume"] = "继续转换",
            ["tray.exit"] = "退出",
            ["progress.moreHidden"] = "…还有 {0} 项",
            ["format.moreKinds"] = "等 {0} 种",
            ["format.example"] = "（例如 {0}）",
            ["err.unsupported"] = "不支持的格式",
            ["err.decodeFailed"] = "无法解码",
            ["err.targetUnavailable"] = "此格式目前无法输出",
            ["err.sourceMissing"] = "源文件已不存在",
            ["err.unreadable"] = "无法读取源文件",
            ["err.writeDenied"] = "无法写入此文件夹",
            ["err.noSpace"] = "空间不足",
            ["err.locked"] = "文件被锁定",
            ["err.timeout"] = "云文件下载超时",
            ["err.targetTooSmall"] = "已达最低质量仍超出指定大小",
            ["err.cropRectEmpty"] = "选区为空",
            ["err.cropNothingToDo"] = "选区就是整张图",
            ["err.writeConflict"] = "输出文件名被其他进程占用",
            ["err.qrNotFound"] = "图片中找不到 QR 码",
            ["err.mergeFailed"] = "无法合并此 PDF",
            ["err.mergeNeedsPassword"] = "此 PDF 需要密码才能打开，请先解除密码再合并",
            ["err.noSubjectFound"] = "这张图找不到人物主体，无法抠图",
            ["err.modelUnavailable"] = "抠图模型尚未获取，请运行 tools\\get-rembg-model.ps1",
            ["err.modelCorrupt"] = "抠图模型文件损坏或下载不完整，请重新获取",
            ["err.generic"] = "转换失败",
            ["err.withPosition"] = "{0}（第 {1} 个文件） {2}",
            ["err.summaryMore"] = "{0}（共 {1} 个）"
        },

        [Ja] = new(StringComparer.Ordinal)
        {
            // ---- 視窗外框 ----
            ["window.title"] = "Confern 設定",
            ["window.subtitle"] = "仕様 §8 の 9 つの設定に、仕様 §9.5 の出力先フォルダーと仕様 §9.4 の圧縮方式を加えたものです。「保存」を押したときにだけファイルへ書き込みます。",

            // ---- 頁面 ----
            ["page.behaviour"] = "動作",
            ["page.quality"] = "画質と待ち時間",
            ["page.safety"] = "安全と診断",
            ["page.behaviourDesc"] = "トリガーキーを押している間だけ、受け付けレイヤーがドロップを受け取ります。自動起動はまだ有効ではありません。",
            ["page.qualityDesc"] = "変換の品質、圧縮方式、受け付けレイヤーが表示されている時間です。",
            ["page.safetyDesc"] = "「未反映」と書かれた項目は、設定値を保存するだけです。",

            // ---- 觸發鍵 ----
            ["field.triggerKey"] = "トリガーキー",
            ["btn.capture"] = "キーを入力",
            ["btn.capturing"] = "入力待ち…",
            ["key.captureHint"] = "キーを押してください",
            ["key.captureHintModifier"] = "修飾キーだけではトリガーキーにできません",
            ["key.captureHintUnsupported"] = "このキーはまだ対応していません",
            ["key.unknown"] = "未対応",
            ["key.customLabel"] = "カスタムキー（{0}）",

            // ---- 開機啟動 ----
            ["field.startWithWindows"] = "自動起動",
            ["field.language"] = "言語",
            ["field.palette"] = "配色",
            ["crop.directionPrefix"] = "方向：",
            ["ratio.free"] = "自由",
            ["ratio.square"] = "1:1",
            ["ratio.wide"] = "16:9",
            ["ratio.tall"] = "9:16",
            ["ratio.classic"] = "4:3",
            ["ratio.classicTall"] = "3:4",
            ["ratio.original"] = "元の比率",
            ["ratio.originalFlipped"] = "元の比率 90°",
            ["merge.ok"] = "OK (Enter)",
            ["merge.cancel"] = "キャンセル (Esc)",
            ["merge.title"] = "PDF を結合：順番を並べ替え",
            ["merge.hint"] = "上の順番がそのまま結合の順番になります（1 番目のファイルのページが先頭です）。右側のハンドルをドラッグすると上下に移動でき、ごみ箱のボタンで外せます。OK を押したときにだけ結合し、キャンセルではファイルを一切作りません。",
            ["merge.up"] = "上へ",
            ["merge.down"] = "下へ",
            ["merge.pageSize"] = "ページサイズ",
            ["merge.sizeOriginal"] = "各ファイルの元のサイズ",
            ["merge.sizeFirst"] = "1 枚目に合わせる",
            ["merge.sizeA4"] = "A4 中央配置",
            ["merge.range"] = "{2} 行のうち {0}〜{1} 行を表示",
            ["merge.pagesTotal"] = "全 {0} ページ",
            ["merge.pagesTotalOne"] = "全 {0} ページ",
            ["merge.pagesUnknown"] = "合計ページ数は不明（{0} 件のファイルでページ数を読めません）",
            ["merge.pagesOf"] = "{0} ページ",
            ["merge.pagesOfOne"] = "{0} ページ",
            ["merge.pagesUnreadable"] = "ページ数不明",
            ["merge.gripTip"] = "ここを上下にドラッグして順番を変更します",
            ["merge.remove"] = "外す",
            ["merge.removeTip"] = "このファイルを結合リストから外します",
            ["merge.rowDragTip"] = "この行をドラッグすると順番を変更できます",
            ["merge.errMove"] = "移動中に予期しないエラーが発生しました（{0}）。順番は変わっていません。",
            ["merge.errLastRemove"] = "結合にはファイルが 1 つ以上必要です。最後の 1 つは外せません（中止する場合は Esc でキャンセルしてください）。",
            ["merge.errRemove"] = "削除中に予期しないエラーが発生しました（{0}）。リストは変わっていません。",
            ["merge.errEmpty"] = "結合できるファイルがありません。1 つ以上残すか、Esc でキャンセルしてください。",
            ["merge.errConfirm"] = "確定時に予期しないエラーが発生しました（{0}）。何も送信されていません。",
            ["qr.hint"] = "画像から読み取った内容です（ファイルは作成していません）。選択して Ctrl+C か下のボタンでコピーできます。",
            ["qr.copy"] = "コピー",
            ["qr.title"] = "QR を読み取る",
            ["qr.notUrl"] = "{0}　（URL ではないため、ここに表示するだけです）",
            ["note.capped"] = "フォルダー展開：上限に達したため、先頭の {0} ファイルだけを取得しました",
            ["note.folderFiles"] = "フォルダー展開：{0} ファイルを読み取り（{1} 件をスキップ）",
            ["note.tooDeep"] = "{1} 階層より深いところにフォルダーが {0} 個あり、収集していません",
            ["fmt.mixedKinds"] = "種類が混在しています。同じ種類を選んでください：",
            ["fmt.listSeparator"] = "、",
            ["svc.interrupted"] = "変換サービスが中断されました",
            ["svc.versionMismatchRestarted"] = "変換サービスのバージョンが一致しないため再起動しました",
            ["svc.versionMismatch"] = "変換サービスのバージョンが一致しません",
            ["svc.error"] = "変換サービスのエラー",
            ["svc.timeout"] = "変換がタイムアウトしました",
            ["svc.cannotStart"] = "変換サービスを起動できません",
            ["progress.files"] = "{0} ファイル",
            ["fmt.mixedShort"] = "種類が混在しています。同じ種類を選んでください",
            ["fmt.noTarget"] = "変換できる形式がありません",
            ["crop.directionNone"] = "方向",
            ["crop.wide"] = "横向き",
            ["crop.tall"] = "縦向き",
            ["crop.ok"] = "OK (Enter)",
            ["crop.cancel"] = "キャンセル (Esc)",
            ["crop.title"] = "切り抜き",
            ["crop.hint"] = "画像の上で矩形をドラッグします（移動・辺と四隅のドラッグができます）· Enter で確定 · Esc でキャンセル",
            ["crop.ratio"] = "比率",
            ["crop.batchNote"] = "（同じ比率がこのバッチのすべての画像に適用され、それぞれ自分のサイズで換算されます）",
            ["crop.infoBatch"] = "　（このバッチは {0} 枚、それぞれ自分のサイズで換算）",
            ["crop.selection"] = "選択 {0}×{1} px　正規化 {2},{3},{4},{5}",
            ["crop.tipDirection"] = "現在の向きは{0}です。押すと別の向きに切り替わります（{1} ↔ {2}）。",
            ["crop.tipNoDirection"] = "{0}には切り替えられる向きがありません。16:9／4:3／元の比率のように、向きが 2 つあるものだけが切り替えできます。",
            ["crop.noteRestored"] = "比率＝{0}：矩形を {1}×{2} px に戻しました（切り替え前のサイズに戻したもの）",
            ["crop.noteFlipped"] = "比率＝{0}：矩形を {1}×{2} から {3}×{4} px に反転しました（長辺と中心は維持）",
            ["crop.noteUnchanged"] = "比率＝{0}：矩形は変わっていません",
            ["crop.noteAdjusted"] = "比率＝{0}：矩形を {1}×{2} から {3}×{4} px に調整しました",
            ["crop.errDirection"] = "向きの切り替え中に予期しないエラーが発生しました（{0}）。無視しました。",
            ["crop.errRatio"] = "比率の切り替え中に予期しないエラーが発生しました（{0}）。無視しました。",
            ["crop.errDragStart"] = "ドラッグ開始時に予期しないエラーが発生しました（{0}）。無視しました。ウィンドウはそのまま使えます。",
            ["crop.errDragMove"] = "ドラッグ中に予期しないエラーが発生しました（{0}）。このドラッグは中止しました。ウィンドウはそのまま使えます。",
            ["crop.errEmpty"] = "選択範囲が画像全体と同じで、切り抜くものがありません。もっと小さい矩形をドラッグしてください（Esc でキャンセル）。",
            ["crop.errConfirm"] = "確定時に予期しないエラーが発生しました（{0}）。何も送信されていません。",
            ["crop.selftestEmpty"] = "セルフテスト：矩形 {0},{1},{2},{3} はこのサイズでは空です",

            // ---- 輸出資料夾（規格 §9.5）----
            // ⚠ 這些是**畫面上的標籤**，不是寫進設定檔的值：寫進檔案的永遠是
            // `/ 空字串（來源旁邊）/ desktop / 資料夾路徑`，由 SettingsStore.TryApply 決定。
            ["field.outputFolder"] = "出力先",
            ["out.source"] = "元ファイルの隣",
            ["out.desktop"] = "デスクトップ",
            ["out.custom"] = "カスタム",
            ["out.needPath"] = "カスタムフォルダーにはフルパスを入力してください（例：C:\\出力）。",

            // ---- 品質 ----
            ["field.jpgQuality"] = "JPG 画質",
            ["field.webpQuality"] = "WebP 画質",

            // ---- 壓縮方式（M3 第三顆扇形，2026-10-06 使用者裁定：等級走設定頁）----
            // ⚠ 這些是**畫面上的標籤**，不是寫進設定檔的值：寫進檔案的永遠是
            // `small / standard / high / target-size`，由 SettingsStore.TryApply 決定。
            // 上限那一格與輸出資料夾同一條規矩：值存 KB，KB／MB 只是畫面的寫法。
            ["field.compressionMode"] = "圧縮方式",
            ["field.compressionTargetKb"] = "圧縮上限",
            ["compress.small"] = "小サイズ",
            ["compress.standard"] = "標準",
            ["compress.high"] = "高画質",
            ["compress.targetSize"] = "サイズ指定",
            ["compress.needSize"] = "「サイズ指定」には 1〜1048576 の範囲で上限を入力し、KB か MB を選んでください。",
            ["unit.kb"] = "KB",
            ["unit.mb"] = "MB",

            // ---- 命名衝突 ----
            ["field.namingCollision"] = "名前の衝突",
            ["naming.value"] = "自動連番（1）、（2）…",
            ["naming.reason"] = "MVP では固定です。上書きも確認もしません。上書きと確認は将来のオプションです。",
            // ---- 逾時 ----
            ["field.timeout"] = "待ち時間",
            ["unit.seconds"] = " 秒",

            // ---- 緊急停用 ----
            ["field.emergencyHotkey"] = "緊急停止キー",

            // ---- 診斷 ----
            ["field.diagnostics"] = "診断ログ",

            // ---- 尚未生效 ----
            ["stub.badge"] = "未反映",
            ["stub.emergencyHotkey"] = "未反映：グローバルホットキーがまだ接続されておらず、ここでは設定値だけを保存します。キーを押しても反応しません。",

            // ---- 開關 ----
            ["toggle.on"] = "オン",
            ["toggle.off"] = "オフ",

            // ---- 語言選項 ----
            ["lang.system"] = "システムに従う",
            ["lang.zh-Hant"] = "繁體中文",
            ["lang.en"] = "English",
            ["lang.zh-Hans"] = "简体中文",
            ["lang.ja"] = "日本語",
            ["lang.ko"] = "한국어",
            ["palette.midnight"] = "ミッドナイト",
            ["palette.graphite"] = "グラファイト",
            ["palette.citrus"] = "シトラス",
            ["palette.celadon"] = "セラドン",
            ["palette.plum"] = "プラム",
            ["palette.porcelain"] = "ポーセリン",

            // ---- 輪盤扇區標籤（工具）----
            // 扇區只有 80x24px、字級 12.7px：筆畫密的字會糊（2026-10-21 實測，
            // 連通墨塊數壓縮 0.47／旋轉 0.5）。所以這裡挑筆畫少的措辭，
            // 日文用 切抜 而不用 切り抜き。字表外的診斷字串走 ToolDescriptor.Label。
            ["tool.label.rotate90"] = "回転",
            ["tool.label.strip-metadata"] = "メタ削除",
            ["tool.label.compress"] = "圧縮",
            ["tool.label.crop"] = "切抜",
            ["tool.label.read-qr"] = "QR 読取",
            ["tool.label.merge-pdf"] = "PDF 結合",
            ["tool.label.remove-bg"] = "背景除去",

            // ---- 底部 ----
            ["btn.save"] = "保存",
            ["btn.cancel"] = "キャンセル",
            ["path.prefix"] = "設定ファイル：",
            ["path.missing"] = "（まだ作成されていません。「保存」を押すと書き込まれます）",
            ["status.idle"] = "まだ変更はありません。",
            ["status.dirty"] = "保存されていない変更があります。",
            ["status.saved"] = "保存しました：{0}",
            ["status.saveFailed"] = "保存に失敗しました（設定は変更されていません）：{0}",
            ["status.badField"] = "保存できません：{0} は現在の値（{1}）を受け付けません。修正してから保存してください。",
            ["status.openRejected"] = "設定画面はすでに開いているため、この要求は無視しました。",
            ["status.alreadyOpen"] = "設定画面はすでに開いています。",
            ["status.openFailed"] = "設定画面を開けませんでした：{0}",
            ["status.saveFailedOnOpen"] = "設定値の読み込みに失敗しました：{0}",

            // ---- 觸發鍵的顯示名 ----
            ["vk.Space"] = "Space",
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
            ["tool.rotate90"] = "90° 回転",
            ["tool.stripMetadata"] = "メタ削除",
            ["tool.compress"] = "圧縮",
            ["tool.crop"] = "切り抜き",
            ["tool.readQr"] = "QR 読取",
            ["tool.mergePdf"] = "結合",
            ["tool.removeBg"] = "背景除去",

            ["wheel.holdToConvert"] = "トリガーキーを押し続ける",

            ["tray.openSettings"] = "設定を開く",
            ["tray.pause"] = "変換を一時停止",
            ["tray.resume"] = "変換を再開",
            ["tray.exit"] = "終了",

            ["progress.moreHidden"] = "…ほか {0} 件",

            ["format.moreKinds"] = "ほか {0} 種",
            ["format.example"] = "（例：{0}）",

            ["err.unsupported"] = "未対応の形式",
            ["err.decodeFailed"] = "デコードできません",
            ["err.targetUnavailable"] = "この形式は現在出力できません",
            ["err.sourceMissing"] = "元のファイルがもうありません",
            ["err.unreadable"] = "元のファイルを読み取れません",
            ["err.writeDenied"] = "このフォルダーに書き込めません",
            ["err.noSpace"] = "空き容量が足りません",
            ["err.locked"] = "ファイルがロックされています",
            ["err.timeout"] = "クラウドファイルのダウンロードがタイムアウトしました",
            ["err.targetTooSmall"] = "最低画質でも指定サイズを超えています",
            ["err.cropRectEmpty"] = "選択範囲が空です",
            ["err.cropNothingToDo"] = "選択範囲が画像全体と同じです",
            ["err.writeConflict"] = "出力名が他のプロセスに使われています",
            ["err.qrNotFound"] = "画像に QR コードが見つかりません",
            ["err.mergeFailed"] = "この PDF を結合できません",
            ["err.mergeNeedsPassword"] = "この PDF はパスワードが必要です。パスワードを解除してから結合してください。",
            ["err.noSubjectFound"] = "この画像には被写体が見つからないため、背景を除去できません",
            ["err.modelUnavailable"] = "背景除去モデルがまだありません。tools\\get-rembg-model.ps1 を実行してください",
            ["err.modelCorrupt"] = "背景除去モデルのファイルが壊れているか、ダウンロードが不完全です。もう一度取得してください",
            ["err.generic"] = "変換に失敗しました",
            ["err.withPosition"] = "{0}（{1} 番目のファイル） {2}",
            ["err.summaryMore"] = "{0}（全 {1} 件）"
        },

        [Ko] = new(StringComparer.Ordinal)
        {
            ["window.title"] = "Confern 설정",
            ["window.subtitle"] = "사양 §8의 설정 아홉 가지에 사양 §9.5의 출력 폴더와 사양 §9.4의 압축 방식을 더한 화면입니다. 변경 사항은 ‘저장’을 눌러야 파일에 기록됩니다.",

            ["page.behaviour"] = "동작",
            ["page.quality"] = "품질·타임아웃",
            ["page.safety"] = "안전·진단",
            ["page.behaviourDesc"] = "트리거 키를 누르고 있는 동안에만 캡처 레이어가 드롭을 받습니다 · 자동 시작은 아직 적용되지 않습니다.",
            ["page.qualityDesc"] = "변환 품질과 압축 방식, 캡처 레이어가 머무는 시간입니다.",
            ["page.safetyDesc"] = "‘적용 전’으로 표시된 항목은 설정값만 저장합니다.",

            ["field.triggerKey"] = "트리거 키",
            ["btn.capture"] = "새 키 누르기",
            ["btn.capturing"] = "키를 누르세요…",
            ["key.captureHint"] = "키를 누르세요",
            ["key.captureHintModifier"] = "수정자 키는 단독으로 트리거 키가 될 수 없습니다",
            ["key.captureHintUnsupported"] = "아직 지원하지 않는 키입니다",
            ["key.unknown"] = "미지원",
            ["key.customLabel"] = "사용자 지정 키({0})",

            ["field.startWithWindows"] = "자동 시작",
            ["field.language"] = "언어",
            ["field.palette"] = "색상 테마",
            ["crop.directionPrefix"] = "방향: ",
            ["ratio.free"] = "자유",
            ["ratio.square"] = "1:1",
            ["ratio.wide"] = "16:9",
            ["ratio.tall"] = "9:16",
            ["ratio.classic"] = "4:3",
            ["ratio.classicTall"] = "3:4",
            ["ratio.original"] = "원본 비율",
            ["ratio.originalFlipped"] = "원본 비율 90°",
            ["merge.ok"] = "확인 (Enter)",
            ["merge.cancel"] = "취소 (Esc)",
            ["merge.title"] = "PDF 병합: 순서 정하기",
            ["merge.hint"] = "위의 순서가 곧 병합 순서입니다(첫 번째 파일의 첫 페이지가 가장 앞). 오른쪽 손잡이를 끌어 위아래로 옮기고, 휴지통 버튼으로 뺄 수 있습니다. 확인해야 병합이 시작되며, 취소하면 어떤 파일도 만들어지지 않습니다.",
            ["merge.up"] = "위",
            ["merge.down"] = "아래",
            ["merge.pageSize"] = "페이지 크기",
            ["merge.sizeOriginal"] = "각 파일 원래 크기",
            ["merge.sizeFirst"] = "첫 파일에 맞춤",
            ["merge.sizeA4"] = "A4 가운데 여백",
            ["merge.range"] = "{0}-{1}행 표시 · 전체 {2}행",
            ["merge.pagesTotal"] = "총 {0}페이지",
            ["merge.pagesTotalOne"] = "총 {0}페이지",
            ["merge.pagesUnknown"] = "총 페이지 수 알 수 없음({0}개 파일의 페이지 수를 읽지 못함)",
            ["merge.pagesOf"] = "{0}페이지",
            ["merge.pagesOfOne"] = "{0}페이지",
            ["merge.pagesUnreadable"] = "페이지 수를 읽지 못함",
            ["merge.gripTip"] = "여기를 위아래로 끌어 순서를 바꿉니다",
            ["merge.remove"] = "제거",
            ["merge.removeTip"] = "이 파일을 병합 목록에서 뺍니다",
            ["merge.rowDragTip"] = "이 행을 끌어 순서를 바꿉니다",
            ["merge.errMove"] = "이동하는 중 예기치 않은 오류({0}) · 순서는 그대로입니다",
            ["merge.errLastRemove"] = "병합하려면 파일이 최소한 하나 있어야 합니다 · 마지막 파일은 뺄 수 없습니다(그만두려면 Esc)",
            ["merge.errRemove"] = "제거하는 중 예기치 않은 오류({0}) · 목록은 그대로입니다",
            ["merge.errEmpty"] = "병합할 파일이 없습니다 · 최소한 하나를 남기거나 Esc를 누르세요",
            ["merge.errConfirm"] = "확인하는 중 예기치 않은 오류({0}) · 아무것도 전송되지 않았습니다",
            ["qr.hint"] = "이미지에서 읽어 낸 내용입니다(어떤 파일도 만들어지지 않았습니다). 선택해서 Ctrl+C로 복사하거나 아래 버튼을 쓰세요.",
            ["qr.copy"] = "복사",
            ["qr.title"] = "QR 스캔",
            ["qr.notUrl"] = "{0}　(URL이 아니라 여기에만 표시합니다)",
            ["note.capped"] = "폴더 펼침: 한도에 도달해 앞의 {0}개 파일만 가져왔습니다",
            ["note.folderFiles"] = "폴더 펼침: {0}개 파일을 읽었습니다({1}개 건너뜀)",
            ["note.tooDeep"] = "{0}개 폴더는 {1}단계보다 깊어 담지 않았습니다",
            ["fmt.mixedKinds"] = "종류가 섞여 있습니다. 같은 종류만 고르세요: ",
            ["fmt.listSeparator"] = ", ",
            ["svc.interrupted"] = "변환 서비스가 중단되었습니다",
            ["svc.versionMismatchRestarted"] = "변환 서비스 버전이 맞지 않아 다시 시작했습니다",
            ["svc.versionMismatch"] = "변환 서비스 버전이 맞지 않습니다",
            ["svc.error"] = "변환 서비스 오류",
            ["svc.timeout"] = "변환 시간 초과",
            ["svc.cannotStart"] = "변환 서비스를 시작할 수 없습니다",
            ["progress.files"] = "{0}개 파일",
            ["fmt.mixedShort"] = "종류가 섞여 있습니다 · 같은 종류만 고르세요",
            ["fmt.noTarget"] = "변환할 수 있는 대상 형식이 없습니다",
            ["crop.directionNone"] = "방향",
            ["crop.wide"] = "가로",
            ["crop.tall"] = "세로",
            ["crop.ok"] = "확인 (Enter)",
            ["crop.cancel"] = "취소 (Esc)",
            ["crop.title"] = "자르기",
            ["crop.hint"] = "이미지 위에서 사각형을 끌어 그리세요(옮기기, 네 변과 네 모서리 조절 가능) · Enter 확인 · Esc 취소",
            ["crop.ratio"] = "비율",
            ["crop.batchNote"] = "(같은 비율이 이 묶음의 모든 이미지에 적용되며, 각 이미지는 자기 크기로 계산됩니다)",
            ["crop.infoBatch"] = "　(이 묶음은 {0}장, 각 이미지는 자기 크기로 계산)",
            ["crop.selection"] = "선택 {0}×{1} px　정규화 {2},{3},{4},{5}",
            ["crop.tipDirection"] = "지금 구도는 {0}입니다. 누르면 다른 방향으로 바뀝니다({1} ↔ {2}).",
            ["crop.tipNoDirection"] = "{0}에는 바꿀 방향이 없습니다 · 16:9 / 4:3 / 원본 비율처럼 방향이 둘인 비율만 해당됩니다.",
            ["crop.noteRestored"] = "비율 = {0}: 사각형을 {1}×{2} px로 되돌렸습니다(뒤집기 전 크기)",
            ["crop.noteFlipped"] = "비율 = {0}: 사각형을 {1}×{2}에서 {3}×{4} px로 뒤집었습니다(긴 변과 중심은 유지)",
            ["crop.noteUnchanged"] = "비율 = {0}: 사각형은 그대로입니다",
            ["crop.noteAdjusted"] = "비율 = {0}: 사각형을 {1}×{2}에서 {3}×{4} px로 조정했습니다",
            ["crop.errDirection"] = "방향을 바꾸는 중 예기치 않은 오류({0}) · 무시했습니다",
            ["crop.errRatio"] = "비율을 바꾸는 중 예기치 않은 오류({0}) · 무시했습니다",
            ["crop.errDragStart"] = "끌기를 시작하는 중 예기치 않은 오류({0}) · 무시했고 창은 그대로 쓸 수 있습니다",
            ["crop.errDragMove"] = "끄는 중 예기치 않은 오류({0}) · 이번 끌기를 멈췄고 창은 그대로 쓸 수 있습니다",
            ["crop.errEmpty"] = "선택 범위가 이미지 전체라 자를 것이 없습니다 · 더 작은 사각형을 그리세요(Esc로 취소)",
            ["crop.errConfirm"] = "확인하는 중 예기치 않은 오류({0}) · 아무것도 전송되지 않았습니다",
            ["crop.selftestEmpty"] = "자체 점검: 사각형 {0},{1},{2},{3}은 이 크기에서 비어 있습니다",

            ["field.outputFolder"] = "출력 폴더",
            ["out.source"] = "원본 파일 옆",
            ["out.desktop"] = "바탕 화면",
            ["out.custom"] = "사용자 지정 폴더",
            ["out.needPath"] = "사용자 지정 폴더는 전체 경로를 입력하세요(예: C:\\Output).",

            ["field.jpgQuality"] = "JPG 품질",
            ["field.webpQuality"] = "WebP 품질",

            ["field.compressionMode"] = "압축 방식",
            ["field.compressionTargetKb"] = "압축 상한",
            ["compress.small"] = "작은 파일",
            ["compress.standard"] = "표준",
            ["compress.high"] = "고화질",
            ["compress.targetSize"] = "압축 크기",
            ["compress.needSize"] = "‘압축 크기’에는 1–1048576 사이의 상한을 입력하고 KB 또는 MB를 고르세요.",
            ["unit.kb"] = "KB",
            ["unit.mb"] = "MB",

            ["field.namingCollision"] = "이름 충돌",
            ["naming.value"] = "자동 번호 (1), (2)…",
            ["naming.reason"] = "MVP에서는 고정: 절대 덮어쓰지 않고 묻지도 않습니다.",
            ["field.timeout"] = "캡처 타임아웃",
            ["unit.seconds"] = "초",

            ["field.emergencyHotkey"] = "긴급 중지 단축키",

            ["field.diagnostics"] = "진단 기록",

            ["stub.badge"] = "적용 전",
            ["stub.emergencyHotkey"] = "아직 적용 전입니다: 전역 단축키가 연결되지 않아 여기서는 설정값만 저장되고 키를 눌러도 반응하지 않습니다.",

            ["toggle.on"] = "켜짐",
            ["toggle.off"] = "꺼짐",

            ["lang.system"] = "시스템 따름",
            ["lang.zh-Hant"] = "繁體中文",
            ["lang.en"] = "English",
            ["lang.zh-Hans"] = "简体中文",
            ["lang.ja"] = "日本語",
            ["lang.ko"] = "한국어",
            ["palette.midnight"] = "미드나이트",
            ["palette.graphite"] = "그래파이트",
            ["palette.citrus"] = "시트러스",
            ["palette.celadon"] = "청자색",
            ["palette.plum"] = "자두색",
            ["palette.porcelain"] = "포슬린",

            // ---- 輪盤扇區標籤（工具）----
            // 扇區只有 80x24px、字級 12.7px：筆畫密的字會糊（2026-10-21 實測，
            // 連通墨塊數壓縮 0.47／旋轉 0.5）。所以這裡挑筆畫少的措辭，
            // 日文用 切抜 而不用 切り抜き。字表外的診斷字串走 ToolDescriptor.Label。
            ["tool.label.rotate90"] = "회전",
            ["tool.label.strip-metadata"] = "메타 제거",
            ["tool.label.compress"] = "압축",
            ["tool.label.crop"] = "자르기",
            ["tool.label.read-qr"] = "QR 스캔",
            ["tool.label.merge-pdf"] = "PDF 병합",
            ["tool.label.remove-bg"] = "배경 제거",

            ["btn.save"] = "저장",
            ["btn.cancel"] = "취소",
            ["path.prefix"] = "설정 파일: ",
            ["path.missing"] = "(아직 없음 · ‘저장’을 누르면 기록됩니다)",
            ["status.idle"] = "아직 변경 없음.",
            ["status.dirty"] = "저장하지 않은 변경이 있습니다.",
            ["status.saved"] = "저장했습니다: {0}",
            ["status.saveFailed"] = "저장 실패(설정은 그대로): {0}",
            ["status.badField"] = "저장할 수 없습니다: {0}에서 현재 값({1})을 받지 않습니다 · 먼저 고친 뒤 저장하세요.",
            ["status.openRejected"] = "설정 창이 이미 열려 있어 이번 열기 요청을 무시했습니다.",
            ["status.alreadyOpen"] = "설정 창이 이미 열려 있습니다.",
            ["status.openFailed"] = "설정 창을 열지 못했습니다: {0}",
            ["status.saveFailedOnOpen"] = "설정 값을 불러오지 못했습니다: {0}",

            ["vk.Space"] = "Space",
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
            ["tool.rotate90"] = "90° 회전",
            ["tool.stripMetadata"] = "메타 제거",
            ["tool.compress"] = "압축",
            ["tool.crop"] = "자르기",
            ["tool.readQr"] = "QR 스캔",
            ["tool.mergePdf"] = "병합",
            ["tool.removeBg"] = "배경 제거",

            ["wheel.holdToConvert"] = "트리거 키를 누르세요",

            ["tray.openSettings"] = "설정 열기",
            ["tray.pause"] = "변환 일시 중지",
            ["tray.resume"] = "변환 재개",
            ["tray.exit"] = "종료",

            ["progress.moreHidden"] = "…{0}개 더",

            ["format.moreKinds"] = "외 {0}종",
            ["format.example"] = "(예: {0})",

            ["err.unsupported"] = "지원하지 않는 형식",
            ["err.decodeFailed"] = "디코딩할 수 없습니다",
            ["err.targetUnavailable"] = "이 형식은 지금 출력할 수 없습니다",
            ["err.sourceMissing"] = "원본 파일이 더 이상 없습니다",
            ["err.unreadable"] = "원본 파일을 읽을 수 없습니다",
            ["err.writeDenied"] = "이 폴더에 쓸 수 없습니다",
            ["err.noSpace"] = "공간이 부족합니다",
            ["err.locked"] = "파일이 잠겨 있습니다",
            ["err.timeout"] = "클라우드 파일 내려받기 시간 초과",
            ["err.targetTooSmall"] = "최저 품질로도 지정한 크기를 넘습니다",
            ["err.cropRectEmpty"] = "선택 범위가 비어 있습니다",
            ["err.cropNothingToDo"] = "선택 범위가 이미지 전체입니다",
            ["err.writeConflict"] = "출력 파일 이름을 다른 프로세스가 쓰고 있습니다",
            ["err.qrNotFound"] = "이미지에서 QR 코드를 찾지 못했습니다",
            ["err.mergeFailed"] = "이 PDF를 병합할 수 없습니다",
            ["err.mergeNeedsPassword"] = "이 PDF는 암호가 있어야 열립니다 · 암호를 먼저 풀고 병합하세요",
            ["err.noSubjectFound"] = "이 이미지에서 인물 주체를 찾지 못해 배경을 제거할 수 없습니다",
            ["err.modelUnavailable"] = "배경 제거 모델이 아직 없습니다 · tools\\get-rembg-model.ps1을 실행하세요",
            ["err.modelCorrupt"] = "배경 제거 모델 파일이 손상되었거나 내려받기가 불완전합니다 · 다시 받으세요",
            ["err.generic"] = "변환 실패",
            ["err.withPosition"] = "{0}({1}번째 파일) {2}",
            ["err.summaryMore"] = "{0}(총 {1}개)"
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
            ["qr.hint"] = "This is what the image decoded to (no file was produced). Select it and press Ctrl+C, or use the button below.",
            ["qr.copy"] = "Copy",
            ["qr.title"] = "Scan QR",
            ["qr.notUrl"] = "{0}　(not a URL, so it is only shown here)",
            ["note.capped"] = "Folder expanded: limit reached, only the first {0} files were taken",
            ["note.folderFiles"] = "Folder expanded: {0} files read ({1} skipped)",
            ["note.tooDeep"] = "{0} more folders sit deeper than {1} levels and were not collected",
            ["fmt.mixedKinds"] = "Mixed selection — pick one kind: ",
            ["fmt.listSeparator"] = ", ",
            ["svc.interrupted"] = "Conversion service interrupted",
            ["svc.versionMismatchRestarted"] = "Conversion service version mismatch — restarted",
            ["svc.versionMismatch"] = "Conversion service version mismatch",
            ["svc.error"] = "Conversion service error",
            ["svc.timeout"] = "Conversion timed out",
            ["svc.cannotStart"] = "Conversion service could not start",
            ["progress.files"] = "{0} files",
            ["fmt.mixedShort"] = "Mixed selection — pick one kind",
            ["fmt.noTarget"] = "No convertible target format",
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
            ["lang.zh-Hant"] = "繁體中文",
            ["lang.en"] = "English",
            ["lang.zh-Hans"] = "简体中文",
            ["lang.ja"] = "日本語",
            ["lang.ko"] = "한국어",
            ["palette.midnight"] = "Midnight",
            ["palette.graphite"] = "Graphite",
            ["palette.citrus"] = "Citrus",
            ["palette.celadon"] = "Celadon",
            ["palette.plum"] = "Plum",
            ["palette.porcelain"] = "Porcelain",

            // ---- 輪盤扇區標籤（工具）----
            // 扇區只有 80x24px、字級 12.7px：筆畫密的字會糊（2026-10-21 實測，
            // 連通墨塊數壓縮 0.47／旋轉 0.5）。所以這裡挑筆畫少的措辭，
            // 日文用 切抜 而不用 切り抜き。字表外的診斷字串走 ToolDescriptor.Label。
            ["tool.label.rotate90"] = "ROTATE",
            ["tool.label.strip-metadata"] = "CLEAN",
            ["tool.label.compress"] = "COMPRESS",
            ["tool.label.crop"] = "CROP",
            ["tool.label.read-qr"] = "READ QR",
            ["tool.label.merge-pdf"] = "MERGE PDF",
            ["tool.label.remove-bg"] = "CUTOUT",

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
