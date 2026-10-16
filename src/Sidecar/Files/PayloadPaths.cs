namespace Sidecar;

/// <summary>
/// 把「使用者拖進來的東西」變成「這個產品要處理的檔案清單」——**唯一**的擁有者。
///
/// 拖曳的來源可以是**資料夾**（2026-10-15 切片）。展開必須發生在
/// <see cref="FormatCatalog.Classify"/> **之前**：那樣一來判定、工具層、`merge-pdf`、
/// 頁面大小三選一、排序視窗、引擎**全都不用改**——一個裝著 20 張圖的資料夾會自動讓
/// MERGE PDF 那顆扇形出現，因為 `AppliesTo` 已經拿得到檔案數。
///
/// ⚠ 這個檔案是「哪些檔要收」的**唯一**落腳處。別在別的地方再寫一份格式清單或副檔名清單
///   （判定一律問 <see cref="IFormatCatalog"/>）。
/// </summary>
internal static class PayloadPaths
{
    /// <summary>資料夾遞迴的深度上限。防的是「不小心拖了 C:\」，不是防壞人。</summary>
    public const int MaxDepth = 8;

    /// <summary>一次最多收幾個檔。超過**要說出來**（見 <see cref="Expand"/> 的 notice），不可靜默截斷。</summary>
    public const int MaxFiles = 1000;

    /// <summary>
    /// 展開。<paramref name="dropped"/> 裡的**檔案**一律原樣保留——那是使用者親手指到的，
    /// 而且既有行為靠它（單一個不支援的檔必須仍是 `Unsupported`，不是變成空的）。
    /// **資料夾裡找出來的**檔案才過濾：一個資料夾裡一定有 `.lnk`、`Thumbs.db`、說明文件，
    /// 讓它們進判定會把整批變成 Mixed（實測：`C:\Temp\a.jpg;C:\Temp\doc.pdf` 就是這樣變 Mixed 的）。
    /// </summary>
    /// <param name="notice">給人看的一行「實際發生了什麼」；沒有特別的事就是空字串。</param>
    /// <param name="truncated">
    /// 「有東西**沒有**被收進來」。呼叫端要拿它決定「這句話要不要給使用者看」：
    /// 平常那句「讀到 N 個檔（跳過 M 個）」是給日誌的，而截斷（1000 個上限、8 層深度）
    /// 是使用者**必須**知道的——不然他拿到的是 1000 頁的成品，而且看起來完全正常。
    /// 「什麼算截斷」只有這裡說了算。
    /// </param>
    public static string[] Expand(IReadOnlyList<string> dropped, IFormatCatalog catalog,
                                   Action<string>? log, out string notice, out bool truncated)
    {
        notice = "";
        truncated = false;
        if (dropped == null || dropped.Count == 0) return Array.Empty<string>();

        var kept = new List<string>();
        int skipped = 0;
        int folders = 0;
        int tooDeep = 0;
        bool capped = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in dropped)
        {
            if (string.IsNullOrEmpty(entry)) continue;

            bool isFolder;
            try { isFolder = Directory.Exists(LongPath.Prepare(entry)); }
            catch (Exception ex)
            {
                log?.Invoke($"payload paths: cannot look at '{entry}' ({ex.GetType().Name}) -> skipped");
                skipped++;
                continue;
            }

            if (!isFolder)
            {
                // 親手指到的檔案：原樣保留（不問格式）。
                if (seen.Add(entry)) kept.Add(entry);
                continue;
            }

            folders++;
            int before = kept.Count;
            Collect(entry, 0, catalog, log, kept, seen, ref skipped, ref capped, ref tooDeep);
            log?.Invoke($"payload paths: folder '{entry}' -> {kept.Count - before} file(s) taken");
            if (capped) break;   // 上限到了就別再去翻下一個資料夾
        }

        // 名稱自然排序：只在「有展開資料夾」時才動順序。
        // ⚠ 直接拖進來的檔案**必須保持他給的順序**——那個順序就是頁序，是使用者排出來的。
        if (folders > 0)
        {
            kept.Sort(NaturalCompare);
        }

        if (capped) notice = $"資料夾展開：已達上限，只取前 {MaxFiles} 個檔";
        else if (folders > 0) notice = $"資料夾展開：讀到 {kept.Count} 個檔（跳過 {skipped} 個）";
        truncated = capped || tooDeep > 0;
        // 🔴 2026-10-16（③）：深度上限原本是**靜默**的——`Collect` 到頂就 return，沒有任何一行
        //    說出來。也就是說「拖了一個結構很深的資料夾」會安靜地少收檔案，而使用者拿到的
        //    合併結果看起來完全正常（頁數對、檔名對），只是少了後面那幾層。
        //    這一條與檔案數上限（capped）是同一條規矩：**截斷可以，靜默不行**。
        if (tooDeep > 0)
            notice += (notice.Length > 0 ? "；" : "") +
                      $"另有 {tooDeep} 個資料夾在 {MaxDepth} 層以下，未收";

        log?.Invoke($"payload paths: in={dropped.Count} folders={folders} out={kept.Count} " +
                    $"skipped={skipped} capped={capped} tooDeep={tooDeep}");
        return kept.ToArray();
    }

    /// <summary>遞迴收集一個資料夾。深度到頂就停、數量到頂就停，兩者都不靜默。</summary>
    private static void Collect(string folder, int depth, IFormatCatalog catalog, Action<string>? log,
                                List<string> kept, HashSet<string> seen, ref int skipped, ref bool capped,
                                ref int tooDeep)
    {
        if (capped) return;
        // 到頂了：**數出來**（呼叫端會把它寫進 notice 與日誌）。這裡數的是「沒有進去的資料夾」，
        // 不是它底下有幾個檔——後者要把整棵子樹走一遍才知道，而這一條路的目的是「不要因為
        // 一個意外的巨樹卡住」，不是精算。
        if (depth > MaxDepth) { tooDeep++; return; }

        string[] files;
        string[] dirs;
        try
        {
            files = Directory.GetFiles(LongPath.Prepare(folder));
            dirs = Directory.GetDirectories(LongPath.Prepare(folder));
        }
        catch (Exception ex)
        {
            // 權限不足、路徑太長、資料夾剛被刪掉……都不是災難：記下來、跳過、繼續。
            log?.Invoke($"payload paths: cannot read '{folder}' ({ex.GetType().Name}) -> skipped");
            return;
        }

        Array.Sort(files, NaturalCompare);
        Array.Sort(dirs, NaturalCompare);

        foreach (string file in files)
        {
            if (kept.Count >= MaxFiles) { capped = true; return; }
            if (IsJunk(file)) { skipped++; continue; }
            if (!seen.Add(file)) continue;

            if (IsSomethingWeCanUse(file, catalog)) kept.Add(file);
            else { skipped++; log?.Invoke($"payload paths: skipped (not a format we can use) '{Path.GetFileName(file)}'"); }
        }

        foreach (string dir in dirs)
        {
            if (IsJunk(dir)) { skipped++; continue; }
            Collect(dir, depth + 1, catalog, log, kept, seen, ref skipped, ref capped, ref tooDeep);
            if (capped) return;
        }
    }

    /// <summary>
    /// 「這個檔我們認得嗎」——問判定（唯一擁有者），**不自己列清單**。
    /// 注意用的是 <c>Classify</c> 而不是 <c>FormatOfPath</c>：`.txt` 在格式表裡是有名字的，
    /// 但它沒有任何可轉換的目標，`Classify` 說 `Unsupported` 才是「不要收」的正確答案。
    /// </summary>
    private static bool IsSomethingWeCanUse(string file, IFormatCatalog catalog)
    {
        try { return catalog.Classify(new[] { file }).Status != PayloadStatus.Unsupported; }
        catch (Exception ex) { return ex is OperationCanceledException; }   // 讀不到就當成不能用
    }

    /// <summary>隱藏／系統／作業系統產生的雜物。這些進判定只會把整批弄成 Mixed。</summary>
    private static bool IsJunk(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Length == 0) return true;
        if (name == "Thumbs.db" || name == "desktop.ini" || name == ".DS_Store") return true;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;

        try
        {
            FileAttributes a = File.GetAttributes(LongPath.Prepare(path));
            if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
        }
        catch (Exception) { /* 問不到屬性：當成正常檔，後面判定會再擋一次 */ }

        return false;
    }

    /// <summary>
    /// 名稱的**自然排序**：`scan2` 要在 `scan10` 前面。
    /// ⚠ 這不是潔癖——字典序會把第 10 頁排到第 2 頁前面，而**合併的頁序就是這個順序**，
    ///   那種錯會直接印在成品上，且看起來「有成功」。
    /// </summary>
    public static int NaturalCompare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                // 先去前導零，再比長度，最後才比內容（避免溢位）。
                string da = a.Substring(si, i - si).TrimStart('0');
                string db = b.Substring(sj, j - sj).TrimStart('0');
                if (da.Length != db.Length) return da.Length - db.Length;
                int cmp = string.CompareOrdinal(da, db);
                if (cmp != 0) return cmp;
                continue;
            }

            int c = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
            if (c != 0) return c;
            i++; j++;
        }

        return (a.Length - i) - (b.Length - j);
    }
}
