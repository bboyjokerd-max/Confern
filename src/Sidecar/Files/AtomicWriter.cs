namespace Sidecar;

/// <summary>
/// 原子寫入（規格 §5）：先寫同資料夾的 `<name>.<ext>.tmp-<guid>`，成功後才 rename 成目標名。
///
/// **「永不覆寫」是靠 <c>File.Move(…, overwrite: false)</c> 的核心保證，不是「先檢查再寫」**——
/// 後者在競態下（另一個行程剛好也在寫同一個檔名）會毀掉使用者的檔案。
/// 失敗一律刪除暫存檔（§7.2 第三條：不得殘留）。
/// </summary>
internal static class AtomicWriter
{
    public static string TempPathFor(string targetPath)
    {
        string dir = Path.GetDirectoryName(targetPath) ?? ".";
        return Path.Combine(dir, $"{Path.GetFileName(targetPath)}.tmp-{Guid.NewGuid():N}");
    }

    /// <summary>把內容寫進暫存檔，flush 到磁碟，再 rename 到目標。任何失敗都不留暫存檔。</summary>
    /// <param name="overwrite">
    /// 轉檔輸出**永不覆寫**（規格 §5 的紅線，預設 false，靠 <c>File.Move(overwrite: false)</c> 保證）。
    /// 只有「程式自己的檔案」（設定檔）才傳 true——那裡覆寫是意圖，不是意外；而且仍然走同一條
    /// 原子寫入路徑，不另寫第二套（否則「不留 .tmp-*」這個保證會變成兩份要各自維護）。
    /// </param>
    public static void Write(string targetPath, Action<Stream> writeContent, TempJournal? journal = null,
                             bool overwrite = false)
    {
        string temp = TempPathFor(targetPath);
        try
        {
            journal?.Register(temp);
            using (var stream = new FileStream(LongPath.Prepare(temp), FileMode.CreateNew,
                                               FileAccess.Write, FileShare.None))
            {
                writeContent(stream);
                // 沖到磁碟再 rename：斷電最壞只會少一個輸出檔，不會留下半個「看起來正常」的目標檔
                stream.Flush(flushToDisk: true);
            }

            File.Move(LongPath.Prepare(temp), LongPath.Prepare(targetPath), overwrite: overwrite);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            journal?.Unregister(temp);
        }
    }

    public static bool Exists(string path)
    {
        try { return File.Exists(LongPath.Prepare(path)); } catch { return false; }
    }

    /// <summary>刪除失敗不覆蓋真正的錯誤（呼叫端正在處理的例外才是重點）。</summary>
    public static void TryDelete(string path)
    {
        try { File.Delete(LongPath.Prepare(path)); } catch { }
    }
}
