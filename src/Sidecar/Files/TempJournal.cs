using System.Text.Json;

namespace Sidecar;

/// <summary>
/// 進行中暫存檔的期刊——規格 §7.1「行程崩潰／斷電 → 下次啟動時掃描並清除所有 `.tmp-*` 殘檔」。
///
/// **刻意不盲掃磁碟**：只認自己登記過的暫存檔。盲掃有兩個問題——
/// ① 要決定掃哪些資料夾（掃全碟不可接受）；② 會誤刪**另一個行程正在寫**的暫存檔。
/// 登記 → 完成即註銷，因此崩潰後留下的清單就是「該清的東西」，一個不多一個不少。
/// </summary>
internal sealed class TempJournal
{
    private readonly string _journalPath;
    private readonly object _gate = new();
    private readonly List<string> _inFlight = new();

    public TempJournal(string journalPath)
    {
        _journalPath = journalPath;
        try
        {
            string? dir = Path.GetDirectoryName(journalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* 期刊寫不了不該讓程式起不來；只是失去殘檔回收能力 */ }
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.Name, "temps.json");

    public void Register(string tempPath)
    {
        lock (_gate) { _inFlight.Add(tempPath); Save(); }
    }

    public void Unregister(string tempPath)
    {
        lock (_gate)
        {
            _inFlight.Remove(tempPath);
            Save();
        }
    }

    /// <summary>
    /// 啟動時回收：刪掉上一輪沒收掉的暫存檔，回傳實際刪除的數量。
    /// 回傳值要能是 0（沒有殘檔是正常狀態），所以「有清到」必須另外看得出來。
    /// </summary>
    public int RecoverStale()
    {
        List<string> stale;
        lock (_gate)
        {
            stale = Load();
            _inFlight.Clear();
            Save();
        }

        int removed = 0;
        foreach (string temp in stale)
        {
            try
            {
                string prepared = LongPath.Prepare(temp);
                if (File.Exists(prepared))
                {
                    File.Delete(prepared);
                    removed++;
                }
            }
            catch
            {
                // 清不掉（權限／被鎖）就留在原地；不拋，因為它不該擋住程式啟動
            }
        }
        return removed;
    }

    private void Save()
    {
        try { File.WriteAllText(_journalPath, JsonSerializer.Serialize(_inFlight)); }
        catch { }
    }

    private List<string> Load()
    {
        try
        {
            return File.Exists(_journalPath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_journalPath)) ?? new List<string>()
                : new List<string>();
        }
        catch
        {
            return new List<string>();   // 期刊壞掉＝放棄回收，但不假裝清過
        }
    }
}
