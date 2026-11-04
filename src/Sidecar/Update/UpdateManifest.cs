using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidecar;

/// <summary>
/// 更新清單（manifest）——**這個檔案是伺服器端唯一的產物**，產品只讀不寫。
///
/// <para><b>為什麼是「一份 JSON 放在一個固定網址」而不是查詢 API</b>：更新檢查要在**沒有帳號、
/// 沒有 Token、沒有後端**的前提下成立。一份靜態檔案可以用任何東西託管（GitHub Pages、S3、
/// 一個 nginx 目錄），而且它自己就是答案——維運的人不必先讓伺服器活著才能回答「最新版是什麼」。</para>
///
/// <para>欄位多寫少寫都不該讓檢查整個壞掉：**必要欄位只有 <c>version</c> 與 <c>url</c>**，
/// 其餘選填；多出來的鍵刻意忽略（清單會隨時間長出欄位，而舊的產品要繼續讀得懂）。</para>
/// </summary>
internal sealed record UpdateManifest(
    string Version,
    string Url,
    string Sha256,
    string Notes,
    bool Mandatory,
    string Published)
{
    /// <summary>JSON 的形狀。刻意不拿這個 record 的欄位直接反序列化：缺欄位要能「指出是哪一個」。</summary>
    private sealed class Wire
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("mandatory")] public bool? Mandatory { get; set; }
        [JsonPropertyName("published")] public string? Published { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// 解析。<b>失敗一定要說出為什麼</b>：一份壞掉的上游清單，與一個壞掉的產品，從日誌上看起來
    /// 必須不一樣（這是這個專案反覆踩到的形狀）。
    /// </summary>
    public static bool TryParse(string json, out UpdateManifest? manifest, out string why)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            why = "the manifest is empty";
            return false;
        }

        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(json, Json);
        }
        catch (JsonException ex)
        {
            why = $"the manifest is not valid JSON ({ex.Message})";
            return false;
        }

        if (wire == null)
        {
            why = "the manifest is JSON null";
            return false;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(wire.Version)) missing.Add("version");
        if (string.IsNullOrWhiteSpace(wire.Url)) missing.Add("url");
        if (missing.Count > 0)
        {
            why = $"the manifest is missing {string.Join(" and ", missing)}";
            return false;
        }

        string version = wire.Version!.Trim();
        if (!UpdateVersion.IsWellFormed(version))
        {
            why = $"the manifest version '{version}' is not a version number";
            return false;
        }

        string url = wire.Url!.Trim();
        if (!IsHttp(url))
        {
            why = $"the manifest url '{url}' is not an http(s) url";
            return false;
        }

        // 選填欄位一律給空字串而不是 null：呼叫端不必到處寫 ?.，而「沒有這個欄位」與
        // 「這個欄位是空的」在下游是同一件事（都不顯示）。
        manifest = new UpdateManifest(
            Version: version,
            Url: url,
            Sha256: (wire.Sha256 ?? "").Trim(),
            Notes: (wire.Notes ?? "").Trim(),
            Mandatory: wire.Mandatory ?? false,
            Published: (wire.Published ?? "").Trim());
        why = "";
        return true;
    }

    private static bool IsHttp(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>一行證據用。</summary>
    public string Describe() =>
        $"version={Version} url='{Url}' sha256={(Sha256.Length > 0 ? Sha256 : "none")} " +
        $"mandatory={Mandatory} published={(Published.Length > 0 ? Published : "?")}";
}

/// <summary>
/// 版本比較。<b>刻意自己寫，不引入 SemVer 套件</b>：整個功能只用得到一個比較函式，而套件的
/// 邊界行為（<c>1.0.0+build</c>、<c>1.0.0-rc.1</c>）會變成一個新的相依與一個新的行為來源。
///
/// <para>規則（本專案用得到的全部）：</para>
/// <list type="bullet">
///   <item>開頭的 <c>v</c> 可有可無（<c>v0.1.0</c> 等於 <c>0.1.0</c>）。</item>
///   <item>三段數字 <c>major.minor.patch</c>；缺的段視為 0（<c>0.2</c> 等於 <c>0.2.0</c>）。</item>
///   <item><c>-</c> 之後是 prerelease：有 prerelease 的比沒有的小（<c>0.2.0-rc1</c> 小於 <c>0.2.0</c>），
///         兩個都有時逐段比（兩邊都是數字就按數字比，否則按序數）。</item>
///   <item><c>+</c> 之後的 build metadata 完全忽略（<c>0.1.0+abc</c> 等於 <c>0.1.0</c>）。</item>
/// </list>
/// </summary>
internal static class UpdateVersion
{
    /// <summary>是不是一個看得懂的版本號（三段以內的數字，可帶 prerelease）。</summary>
    public static bool IsWellFormed(string text) => Parse(text, out _, out _);

    /// <summary>
    /// a &gt; b 回 1，相等回 0，a &lt; b 回 -1。**兩邊都看不懂時回 0**（無法判斷 → 不該提示更新）；
    /// 只有一邊看不懂時，看得懂的那一邊算大——打錯的版本號因此不會讓人永遠收不到更新，
    /// 也不會讓人無條件被提示。
    /// </summary>
    public static int Compare(string a, string b)
    {
        bool okA = Parse(a, out int[] na, out string pa);
        bool okB = Parse(b, out int[] nb, out string pb);
        if (!okA && !okB) return 0;
        if (!okA) return -1;
        if (!okB) return 1;

        for (int i = 0; i < 3; i++)
        {
            int cmp = na[i].CompareTo(nb[i]);
            if (cmp != 0) return Math.Sign(cmp);
        }

        bool hasA = pa.Length > 0;
        bool hasB = pb.Length > 0;
        if (!hasA && hasB) return 1;    // 0.1.0 > 0.1.0-rc1
        if (hasA && !hasB) return -1;
        if (!hasA && !hasB) return 0;
        return Math.Sign(ComparePrerelease(pa, pb));
    }

    /// <summary>兩段 prerelease 的比較（<c>rc2</c> 對 <c>rc10</c> 要按數字比，不是按字串）。</summary>
    private static int ComparePrerelease(string a, string b)
    {
        string[] pa = a.Split('.');
        string[] pb = b.Split('.');
        int n = Math.Min(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            bool aIsNum = long.TryParse(pa[i], out long va);
            bool bIsNum = long.TryParse(pb[i], out long vb);
            // 純數字段**永遠小於**文字段（SemVer 的規定）⇒ 型別不同時不能直接比字串。
            if (aIsNum != bIsNum) return aIsNum ? -1 : 1;
            int cmp = aIsNum ? va.CompareTo(vb) : string.CompareOrdinal(pa[i], pb[i]);
            if (cmp != 0) return cmp;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    /// <summary>拆成三段數字＋prerelease（空字串＝沒有 prerelease）。</summary>
    private static bool Parse(string text, out int[] numbers, out string prerelease)
    {
        numbers = new int[3];
        prerelease = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);

        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);          // build metadata 不算版本

        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s.Substring(dash + 1);
            s = s.Substring(0, dash);
        }

        string[] parts = s.Split('.');
        if (parts.Length == 0 || parts.Length > 3) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int value) || value < 0) return false;
            numbers[i] = value;
        }
        return true;
    }
}
