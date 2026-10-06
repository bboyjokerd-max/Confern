namespace Sidecar;

/// <summary>
/// Worker 證據檔的**路徑規則**，以及「每個 worker 一份證據、對外仍是一個穩定檔名」
/// 這件事的實作——這個檔案是它兩個的唯一擁有者。
///
/// <para><b>為什麼需要它（2026-10-06 量到的缺陷）</b>：worker 是「每個工作 spawn 一個」
/// （規格 §9.1），而每一個 worker 都被 <see cref="ConversionClient"/> 餵同一個
/// <c>%LOCALAPPDATA%\Confern\worker.log</c>。但 <see cref="Evidence"/> 是以
/// <c>append:false</c>（＝<c>FileMode.Create</c> ＋ 預設 <c>FileShare.Read</c>）開啟的，
/// 那是**獨占寫入**：第二個同時存在的 worker 一開檔就吃 <c>IOException</c>，而且它是在
/// <c>Program.Main</c> 建 <see cref="Evidence"/> 時就炸掉——worker 從來沒連上管道，
/// UI 端只能等到連線逾時，然後把**整批**檔案標成失敗。兩個 worker 若真的並存，
/// 後開的那一個還會把前一個已經寫下的內容**截斷**掉。</para>
///
/// <para><b>做法：一 worker 一檔，再加一道單向鏡射。</b>
/// ① 每個 worker 把證據寫進**自己的** <c>worker-&lt;key&gt;.log</c>
/// （<see cref="ForJob"/>）——那是唯一被獨占開啟的檔案，而它永遠只屬於一個行程，
/// 所以「獨占」在這裡不再是問題；
/// ② <see cref="Mirror"/> 把那一份的內容增量倒進共用的 <c>worker.log</c>，於是既有讀取者
/// 那個**穩定檔名**照舊存在，而且裡面的內容永遠是「這個 worker 剛剛寫下的東西」。</para>
///
/// <para><b>為什麼不直接把 <see cref="Evidence"/> 改成 <c>append:true</c></b>：
/// 「每次執行從乾淨的日誌開始」是既有套件大量依賴的語意（`verify-m1d.ps1` 的 S12.19
/// 先刪掉 worker 日誌、再讀「**這次**的 worker 回報了什麼版本」，並用
/// <c>LastWriteTime &gt;= run 開始時間</c> 判斷新鮮度）。把 <see cref="Evidence"/> 改成
/// 附加模式會讓上一次執行的內容留在同一個檔案裡，那些斷言就會對著**舊內容**變綠——
/// 把缺陷換成假綠，比缺陷更糟。鏡射**保留**乾淨開始：接手的第一筆寫入會截斷共用檔。</para>
///
/// <para><b>為什麼不把共用檔改名</b>：既有讀取者寫死了 <c>worker.log</c>
/// （<c>tools/verify-m1d.ps1:254,815</c>、<c>tools/check-test-artifacts.ps1:31</c>、
/// <c>docs/TEST-PLAN-M1-first-run.md</c>）。改名會讓它們找不到檔案＝一堆套件靜默失去證據。</para>
///
/// <para><b>為什麼不用硬連結</b>：試過（`CreateHardLinkW`），但硬連結要求先把舊的共用名
/// 刪掉，於是兩個 worker 會在「刪除同一個名字」上互撞——量到的是
/// <c>IOException 0x80070020</c>（`SHARING_VIOLATION`：另一邊正握著那個檔案），
/// 兩邊都退到退路。鏡射沒有那個刪除動作：它只用 `FileMode.Append` 打開共用檔，
/// 而**附加是作業系統層級可共用的**（多寫者共用同一個檔案物件，各自往尾端寫）。
/// 代價是共用檔的內容由鏡射寫入、不是 worker 直接寫——但它是同一份位元組的增量，
/// 不是第二份真相。</para>
/// </summary>
internal static class WorkerLog
{
    /// <summary>共用的、給讀取者用的檔名。**不要改**——既有的套件與文件寫死了它。</summary>
    private const string SharedName = "worker.log";

    /// <summary>共用檔路徑（讀取者看的那一個）。</summary>
    public static string SharedPath() => Path.Combine(ProductInfo.DataDirectory, SharedName);

    /// <summary>
    /// 清掉太舊的專屬日誌。**為什麼需要它**：一個工作一個檔，而每一次拖放都會 spawn 一個
    /// worker——沒有清理的話 `%LOCALAPPDATA%\Confern` 會無上限地長檔案（實測一次完整回歸
    /// 就留下近百個）。
    ///
    /// <para><b>為什麼用「年齡」而不是「保留最新 N 個」</b>：以數量為準會刪掉**正在跑**的
    /// worker 的檔案——驗收套件會同時留下好幾個 worker（`--crop`、`--wheel-payload`、
    /// S12.21 的兩個），刪到別人手上的那一個就是又一次「互相毀滅」。年齡沒有這個問題：
    /// 活著的 worker 每一行都在更新自己的檔案，所以它**永遠不會**被判定成舊的。</para>
    ///
    /// <para>保留 7 天：足夠事後追查事故現場，又不會無限成長。任何一個檔案刪不掉
    /// （被別人握著、權限不足）都**不算錯**——這是清理，不是工作本身。</para>
    /// </summary>
    public static int PruneStale(TimeSpan olderThan)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(ProductInfo.DataDirectory)) return 0;
            DateTime cutoff = DateTime.Now - olderThan;
            foreach (string file in Directory.EnumerateFiles(ProductInfo.DataDirectory, "worker-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) >= cutoff) continue;
                    File.Delete(LongPath.Prepare(file));
                    removed++;
                }
                catch { /* 清理失敗不影響這一輪 */ }
            }
        }
        catch { /* 列舉失敗不影響這一輪 */ }
        return removed;
    }

    /// <summary>
    /// 這一個 job 專屬的 worker 日誌路徑。
    ///
    /// 鍵用 pipe 名稱：<see cref="WorkerProtocol.NewPipeName"/> 已經保證每個 job 唯一
    /// （`confern-` ＋ GUID），而且它與 worker 自己的身分是同一個東西——不需要另外發一個 id，
    /// 也就不會有「兩個身分不同步」的問題。
    /// </summary>
    public static string ForJob(string pipeName) => Path.Combine(
        ProductInfo.DataDirectory, $"worker-{Sanitize(pipeName)}.log");

    /// <summary>
    /// 檔名安全化的鍵：只留英數與 `-`。pipe 名稱今天已經是 `confern-&lt;guid&gt;`，
    /// 但它是**外部輸入**（`--worker &lt;pipeName&gt;` 誰都能打），而這個字串會被接進檔名——
    /// `..\..\foo` 這種東西不能變成路徑穿越。
    /// </summary>
    private static string Sanitize(string pipeName)
    {
        var sb = new System.Text.StringBuilder(pipeName.Length);
        foreach (char c in pipeName)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        string key = sb.ToString();
        if (key.Length > 64) key = key.Substring(0, 64);
        return key.Length == 0 ? "unnamed" : key;
    }

    /// <summary>
    /// 把 <paramref name="sourcePath"/>（worker 自己的那份）的內容增量鏡射到
    /// <see cref="SharedPath"/>。呼叫端負責 <see cref="Dispose"/>。
    ///
    /// <para><b>誰可以寫共用檔</b>：只有「宣告自己是目前持有者」的那一個。
    /// 共用檔的第一行是宣告（<see cref="OwnerHeaderPrefix"/> ＋ pipe 名稱）。一筆寫入之前，
    /// 如果那一行不是自己的，就**截斷並重新宣告**——這正是「乾淨開始」的語意：
    /// 換手的第一筆寫入把上一次的內容清掉。宣告之後的每一筆都是**附加**，
    /// 所以不會把任何已經寫下去的東西洗掉。</para>
    ///
    /// <para><b>失敗不吞</b>：鏡射寫不進去時，在**自己的**日誌裡記一次
    /// （只記一次，不洗版），指出共用檔沒更新。靜默地不寫等於讓讀取者讀到舊內容還以為是新鮮的。</para>
    /// </summary>
    public sealed class Mirror : IDisposable
    {
        internal const string OwnerHeaderPrefix = "worker.log owner: ";

        private readonly string _sourcePath;
        private readonly string _sharedPath;
        private readonly string _owner;
        private readonly Action<string> _note;
        private long _mirrored;
        private bool _claimed;
        private bool _broken;
        private bool _disposed;

        internal Mirror(string sourcePath, string sharedPath, string owner, Action<string> note)
        {
            _sourcePath = sourcePath;
            _sharedPath = sharedPath;
            _owner = owner;
            _note = note;
        }

        /// <summary>把來源檔新增的位元組接到共用檔尾端（必要時先宣告持有權）。</summary>
        internal void Flush()
        {
            if (_disposed || _broken) return;
            try
            {
                Directory.CreateDirectory(ProductInfo.DataDirectory);
                if (!_claimed || !IsOwner()) Claim();

                string pending = ReadNewContent();
                if (pending.Length > 0)
                {
                    using var writer = new StreamWriter(
                        new FileStream(LongPath.Prepare(_sharedPath), FileMode.Append, FileAccess.Write,
                                       FileShare.ReadWrite));
                    writer.Write(pending);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or NotSupportedException or ArgumentException)
            {
                _broken = true;   // 一次失敗就停：這是證據的鏡射，不是工作本身
                _note($"worker.log: the shared copy could not be updated " +
                      $"({ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}); this worker's evidence " +
                      $"stays in '{Path.GetFileName(_sourcePath)}'");
            }
        }

        /// <summary>宣告持有權：截斷共用檔並寫下自己的那一行。換手時才會發生。</summary>
        private void Claim()
        {
            using (var writer = new StreamWriter(
                       new FileStream(LongPath.Prepare(_sharedPath), FileMode.Create, FileAccess.Write,
                                      FileShare.ReadWrite)))
            {
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {OwnerHeaderPrefix}{_owner}");
            }
            _mirrored = 0;
            _claimed = true;
        }

        /// <summary>共用檔現在是不是自己的？讀第一行（共用模式，不會妨礙別的寫者）。</summary>
        private bool IsOwner()
        {
            try
            {
                if (!File.Exists(LongPath.Prepare(_sharedPath))) return false;
                using var reader = new StreamReader(new FileStream(
                    LongPath.Prepare(_sharedPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                string? first = reader.ReadLine();
                return first != null
                    && first.EndsWith(OwnerHeaderPrefix + _owner, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>來源檔裡還沒鏡射過的那一段。</summary>
        private string ReadNewContent()
        {
            if (!File.Exists(LongPath.Prepare(_sourcePath))) return string.Empty;
            using var reader = new StreamReader(new FileStream(
                LongPath.Prepare(_sourcePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            reader.BaseStream.Seek(_mirrored, SeekOrigin.Begin);
            string text = reader.ReadToEnd();
            _mirrored = reader.BaseStream.Position;
            return text;
        }

        public void Dispose() => _disposed = true;
    }
}
