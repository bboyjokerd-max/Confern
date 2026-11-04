using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidecar;

/// <summary>一次更新檢查的結果。四個狀態涵蓋了「產品需要知道」的全部。</summary>
internal enum UpdateState
{
    /// <summary>上游的版本比這一份新。</summary>
    Newer,

    /// <summary>一樣新。</summary>
    Current,

    /// <summary>上游比這一份舊（例如使用者裝的是內部測試版）——**不提示更新**。</summary>
    Older,

    /// <summary>問不到、或問到的東西看不懂。<b>這不是錯誤</b>：使用者什麼都不必做。</summary>
    Failed
}

/// <summary>
/// 一次檢查的完整結果。<see cref="Reason"/> 只在 <see cref="UpdateState.Failed"/> 時有內容，
/// 而且它會被寫進日誌——「連不上」與「清單壞掉」必須分得出來。
/// </summary>
internal sealed record UpdateResult(
    UpdateState State,
    string Current,
    string Latest,
    string Url,
    string Sha256,
    string Notes,
    bool Mandatory,
    string Reason)
{
    public bool IsNewer => State == UpdateState.Newer;

    /// <summary>一行證據（命令列與日誌共用同一句，所以兩邊的數字不會漂移）。</summary>
    public string Describe() => State switch
    {
        UpdateState.Newer =>
            $"state=newer current={Current} latest={Latest} url='{Url}' " +
            $"sha256={(Sha256.Length > 0 ? Sha256 : "none")} mandatory={Mandatory}",
        UpdateState.Current => $"state=current current={Current} latest={Latest}",
        UpdateState.Older => $"state=older current={Current} latest={Latest} (the published build is older; no prompt)",
        _ => $"state=failed current={Current} reason='{Reason}'"
    };
}

/// <summary>
/// 更新檢查。<b>它是這個產品唯一一個主動連外的程式碼</b>，所以界線寫在這裡：
///
/// <list type="number">
///   <item><b>只有 GET 一份 JSON</b>。不上傳任何東西（連機器碼、版號之外的識別都沒有；
///         User-Agent 只有產品名與版本）。</item>
///   <item><b>不做任何 TLS 例外</b>：憑證有問題就失敗，不「方便一下」。</item>
///   <item><b>失敗是常態，不是錯誤</b>：斷網、離線、公司防火牆都會讓它失敗，而使用者
///         不該看到任何對話框（見呼叫端的 <see cref="UpdateState.Failed"/> 處理）。</item>
///   <item><b>有上限</b>：連線與讀取共用一個逾時（預設 5 秒），而且檢查永遠在背景執行緒上跑。</item>
/// </list>
///
/// <para><b>它不會下載安裝檔，也不會執行任何東西。</b>「有新版本」的最終動作是把下載頁
/// 打開給使用者（見托盤那一端）——自動安裝是另一個風險等級，等有了簽章再說。</para>
/// </summary>
internal static class UpdateChecker
{
    /// <summary>預設逾時。5 秒：比任何一個使用者的耐心短，也比一條慢速連線的握手長。</summary>
    public const int DefaultTimeoutMs = 5000;

    /// <summary>
    /// **刻意留空**：目前沒有任何網址在託管更新清單（2026-11-03）。空字串 ＝ 不檢查，
    /// 而這比一個填錯的網址好——填錯的網址會讓每一台機器每次啟動都連一個不存在的地方。
    /// 有了清單之後，把網址寫進設定檔的 <c>UpdateUrl</c>（或部署時改這裡）。
    /// </summary>
    public const string DefaultManifestUrl = "";

    private static bool IsHttp(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 問一次「有沒有新版本」。<b>這個函式不丟例外</b>：所有失敗都變成
    /// <see cref="UpdateState.Failed"/> 加上一句人看得懂的理由。
    /// </summary>
    public static UpdateResult Check(string manifestUrl, string currentVersion, int timeoutMs)
    {
        string url = (manifestUrl ?? "").Trim();
        if (url.Length == 0)
        {
            return Failed(currentVersion, "no update url is configured");
        }
        if (!IsHttp(url))
        {
            return Failed(currentVersion, $"'{url}' is not an http(s) url");
        }
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

        try
        {
            using var handler = new SocketsHttpHandler
            {
                // 更新檢查不值得跟著使用者的系統 Proxy 設定出錯；代理仍然支援，但不要自動探索。
                UseProxy = true,
                AllowAutoRedirect = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs)
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Confern/{currentVersion}");

            using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return Failed(currentVersion,
                    $"the server answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
            }

            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!UpdateManifest.TryParse(body, out UpdateManifest? manifest, out string why))
            {
                return Failed(currentVersion, why);
            }

            int cmp = UpdateVersion.Compare(manifest!.Version, currentVersion);
            UpdateState state = cmp > 0 ? UpdateState.Newer
                              : cmp == 0 ? UpdateState.Current
                              : UpdateState.Older;
            return new UpdateResult(
                State: state,
                Current: currentVersion,
                Latest: manifest.Version,
                Url: manifest.Url,
                Sha256: manifest.Sha256,
                Notes: manifest.Notes,
                Mandatory: manifest.Mandatory,
                Reason: "");
        }
        catch (TaskCanceledException)
        {
            // HttpClient 的逾時就是這個例外（不是 TimeoutException）。
            return Failed(currentVersion, $"timed out after {timeoutMs} ms");
        }
        catch (OperationCanceledException)
        {
            return Failed(currentVersion, $"timed out after {timeoutMs} ms");
        }
        catch (HttpRequestException ex)
        {
            return Failed(currentVersion, $"could not reach the server ({ex.Message})");
        }
        catch (Exception ex)
        {
            // 最後一道網：更新檢查**永遠不准**讓產品出事。
            return Failed(currentVersion, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static UpdateResult Failed(string currentVersion, string reason) =>
        new(UpdateState.Failed, currentVersion, "", "", "", "", false, reason);
}

/// <summary>
/// 上一次檢查的結果（`%LOCALAPPDATA%\Confern\update-check.json`）。
///
/// <para><b>為什麼要快取</b>：常駐程式一天被打開好幾次（開機、登出登入、使用者手動），
/// 每次啟動都連一次外站是沒禮貌的，而且斷網時會拖慢每一次啟動。規則很簡單：
/// <b>24 小時內不再問</b>，而「使用者自己按了檢查」永遠繞過快取。</para>
///
/// <para><see cref="NotifiedVersion"/> 是「已經為哪個版本彈過氣泡」：沒有它，同一個新版本會在
/// 每一次啟動時再彈一次，那會變成騷擾。</para>
/// </summary>
internal sealed record UpdateCacheEntry(
    string Latest,
    string Url,
    string Sha256,
    string Notes,
    bool Mandatory,
    DateTimeOffset CheckedUtc,
    string NotifiedVersion)
{
    public static readonly UpdateCacheEntry Empty =
        new("", "", "", "", false, DateTimeOffset.MinValue, "");
}

internal static class UpdateCache
{
    /// <summary>快取的有效期（小時）。</summary>
    public const int MaxAgeHours = 24;

    private sealed class Wire
    {
        [JsonPropertyName("latest")] public string? Latest { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("mandatory")] public bool? Mandatory { get; set; }
        [JsonPropertyName("checkedUtc")] public DateTimeOffset? CheckedUtc { get; set; }
        [JsonPropertyName("notifiedVersion")] public string? NotifiedVersion { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// 檔案位置。<c>CONFERN_UPDATE_CHECK</c> 可以整個換掉（驗收套件就是靠它，
    /// 這樣跑一次檢查不會動到使用者真正的那一份）。
    /// </summary>
    public static string DefaultPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("CONFERN_UPDATE_CHECK");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;
        return Path.Combine(ProductInfo.DataDirectory, "update-check.json");
    }

    /// <summary>讀上一次的結果。讀不到（不存在／壞掉／權限）一律回空的那一份，不丟例外。</summary>
    public static UpdateCacheEntry Read()
    {
        try
        {
            string path = DefaultPath();
            if (!File.Exists(path)) return UpdateCacheEntry.Empty;
            string text = File.ReadAllText(path);
            Wire? wire = JsonSerializer.Deserialize<Wire>(text, Json);
            if (wire == null) return UpdateCacheEntry.Empty;
            return new UpdateCacheEntry(
                Latest: wire.Latest ?? "",
                Url: wire.Url ?? "",
                Sha256: wire.Sha256 ?? "",
                Notes: wire.Notes ?? "",
                Mandatory: wire.Mandatory ?? false,
                CheckedUtc: wire.CheckedUtc ?? DateTimeOffset.MinValue,
                NotifiedVersion: wire.NotifiedVersion ?? "");
        }
        catch (Exception)
        {
            return UpdateCacheEntry.Empty;   // 快取壞掉＝沒有快取，不是故障
        }
    }

    /// <summary>寫回。失敗只回 false（呼叫端寫一行日誌就好），不丟例外。</summary>
    public static bool Write(UpdateCacheEntry entry)
    {
        try
        {
            string path = DefaultPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var wire = new Wire
            {
                Latest = entry.Latest,
                Url = entry.Url,
                Sha256 = entry.Sha256,
                Notes = entry.Notes,
                Mandatory = entry.Mandatory,
                CheckedUtc = entry.CheckedUtc,
                NotifiedVersion = entry.NotifiedVersion
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wire, Json);
            AtomicWriter.Write(path, stream => stream.Write(bytes, 0, bytes.Length), overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>這一份快取還算新嗎（<see cref="MaxAgeHours"/> 小時之內）。</summary>
    public static bool IsFresh(UpdateCacheEntry entry, out int ageMinutes)
    {
        ageMinutes = 0;
        if (entry.CheckedUtc == DateTimeOffset.MinValue) return false;
        TimeSpan age = DateTimeOffset.UtcNow - entry.CheckedUtc;
        ageMinutes = (int)Math.Max(0, age.TotalMinutes);
        return age <= TimeSpan.FromHours(MaxAgeHours);
    }

    /// <summary>一行人話，給日誌用。</summary>
    public static string Describe(UpdateCacheEntry entry)
    {
        if (entry.CheckedUtc == DateTimeOffset.MinValue) return "none";
        IsFresh(entry, out int age);
        return $"latest={(entry.Latest.Length > 0 ? entry.Latest : "?")} " +
               $"checked={entry.CheckedUtc:yyyy-MM-dd HH:mm}Z age={age}m " +
               $"notified={(entry.NotifiedVersion.Length > 0 ? entry.NotifiedVersion : "-")}";
    }
}
