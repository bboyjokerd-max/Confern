using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sidecar;

/// <summary>
/// 一份已驗簽的授權（`confern license` 顯示的就是它）。
///
/// <para><b>它是什麼，不是什麼</b>：它證明「這個金鑰字串是我們簽的，內容沒有被改過」。
/// 它**不證明**是誰在用、用在哪一台機器、用了幾次——本機驗簽做不到那些事，
/// 而做得到它們的代價（心跳、機器指紋、線上停權）正是這個產品不想付的代價。</para>
///
/// <para><b><see cref="UpdatesUntil"/> 不是到期日</b>：授權本身永久有效，這個日期只決定
/// 「能免費拿到哪一天之前發表的版本」。授權過期不會讓程式停下來——把使用者的工具關掉
/// 是最糟的收費方式，而且會把一次性買斷講成訂閱。</para>
/// </summary>
/// <param name="Plan">方案識別字（小寫，例如 <c>pro</c>）。</param>
/// <param name="Issued">簽發日（<c>yyyy-MM-dd</c>）。</param>
/// <param name="UpdatesUntil">可免費更新到這一天為止（<c>yyyy-MM-dd</c>）。</param>
/// <param name="Email">購買時的信箱；空字串＝沒記錄（刻意不強制）。</param>
internal sealed record LicenseInfo(string Plan, DateOnly Issued, DateOnly UpdatesUntil, string Email)
{
    /// <summary>一行摘要（命令列與日誌共用）。信箱只印前後各 2 個字元。</summary>
    public string Describe() =>
        $"plan={Plan} issued={Issued:yyyy-MM-dd} updatesUntil={UpdatesUntil:yyyy-MM-dd}" +
        (Email.Length > 0 ? $" email={MaskEmail(Email)}" : "");

    private static string MaskEmail(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0) return "***";
        string local = email.Substring(0, at);
        string domain = email.Substring(at);
        if (local.Length <= 2) return "**" + domain;
        return local.Substring(0, 2) + new string('*', Math.Min(6, local.Length - 2)) + domain;
    }
}

/// <summary>
/// 授權金鑰的格式與驗章（2026-11-03）。
///
/// <para><b>格式</b>：<c>CN1.&lt;base64url(payload)&gt;.&lt;base64url(signature)&gt;</c>。
/// payload 是一段**原封不動**的 UTF-8 JSON，簽章是對那串位元組做 ECDSA P-256／SHA-256。
/// 三個部分都用 base64url（沒有 <c>+</c>、<c>/</c>、<c>=</c>），所以整個金鑰可以安全地
/// 貼進 email、貼進說明、貼進命令列而不會被任何一層轉義弄壞。</para>
///
/// <para><b>為什麼是「離線驗簽」</b>：驗章只需要公鑰，而公鑰可以公開。所以程式**永遠不必連網**，
/// 使用者的授權也不會隨著我們的伺服器倒下而失效。代價是「退款之後沒辦法遠端停用」——
/// 對一個十幾美元的桌面工具，那個代價比「買家發現工具要打電話回家」便宜太多。</para>
///
/// <para><b>私鑰從來不在這裡</b>：簽發用 <c>tools\make-license.ps1</c>，私鑰放在
/// <c>%LOCALAPPDATA%\Confern-keys\license-key.json</c>（不進版控、不進安裝檔）。
/// 這裡只有公鑰，而公鑰印在任何地方都不會損失什麼。</para>
/// </summary>
internal static class License
{
    /// <summary>格式前綴。改版時換一個新的前綴，舊金鑰就會被明確地判成「不認得的格式」。</summary>
    public const string Prefix = "CN1";

    /// <summary>
    /// 驗章用的公鑰：P-256 的 <c>X||Y</c>，各 32 bytes，base64。**這不是秘密。**
    ///
    /// 換一組金鑰＝所有已售出的授權立刻失效，所以它是一個**要備份**的常數，
    /// 不是一個可以隨手改的設定值（私鑰備份見進度日誌）。
    /// </summary>
    private const string PublicKeyBase64 =
        "DEkU8KCLtTrvVTOirQEhxqTLtWoF8QgkALKAxUk218S3m1Z1aC9NRxCSua4cvGcbsBxUAHGPgU+q1iY5sKW//w==";

    private static readonly string[] KnownPlans = { "pro", "solo" };

    /// <summary>
    /// 驗一份金鑰。<paramref name="info"/> 成功時是解出來的內容；失敗時 <paramref name="why"/>
    /// 說得出**是哪一層**失敗（格式／簽章／內容），因為「金鑰無效」對使用者等於沒有資訊。
    /// </summary>
    public static bool TryVerify(string? key, out LicenseInfo? info, out string why)
    {
        info = null;
        string text = (key ?? "").Trim();
        if (text.Length == 0)
        {
            why = "no license key was given";
            return false;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 3)
        {
            why = $"not a license key (expected 3 dot-separated parts, got {parts.Length})";
            return false;
        }
        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            why = $"not a {ProductInfo.Name} license key (prefix '{parts[0]}' is not '{Prefix}')";
            return false;
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = FromBase64Url(parts[1]);
            signature = FromBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            why = "the key is not valid base64url (it was probably cut off when it was copied)";
            return false;
        }
        if (signature.Length != 64)
        {
            why = $"the signature is {signature.Length} bytes, not 64 (P-256)";
            return false;
        }

        if (!VerifySignature(payload, signature))
        {
            // ⚠ 這一條是整個機制的核心：內容與簽章對不上就什麼都不算。
            // 常見原因：使用者改了一個字元、貼到一半、或有人自己編了一份 payload。
            why = "the signature does not match the contents (the key was edited or mistyped)";
            return false;
        }

        LicensePayload? parsed;
        try { parsed = JsonSerializer.Deserialize<LicensePayload>(Encoding.UTF8.GetString(payload)); }
        catch (JsonException ex)
        {
            why = $"the signed contents are not readable JSON ({ex.GetType().Name})";
            return false;
        }
        if (parsed == null)
        {
            why = "the signed contents hold no object";
            return false;
        }
        if (parsed.V != 1)
        {
            why = $"the signed contents say version {parsed.V}, and this build understands 1";
            return false;
        }
        if (string.IsNullOrWhiteSpace(parsed.Plan))
        {
            why = "the signed contents name no plan";
            return false;
        }
        string plan = parsed.Plan.Trim().ToLowerInvariant();
        if (!KnownPlans.Contains(plan))
        {
            why = $"plan '{parsed.Plan}' is not one of {string.Join("/", KnownPlans)}";
            return false;
        }
        if (!TryParseDate(parsed.Issued, out DateOnly issued))
        {
            why = $"issued date '{parsed.Issued}' is not yyyy-MM-dd";
            return false;
        }
        if (!TryParseDate(parsed.UpdatesUntil, out DateOnly updatesUntil))
        {
            why = $"updatesUntil date '{parsed.UpdatesUntil}' is not yyyy-MM-dd";
            return false;
        }

        info = new LicenseInfo(plan, issued, updatesUntil, (parsed.Email ?? "").Trim());
        why = "";
        return true;
    }

    private static bool TryParseDate(string? text, out DateOnly value) =>
        DateOnly.TryParseExact((text ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                               DateTimeStyles.None, out value);

    private static bool VerifySignature(byte[] payload, byte[] signature)
    {
        try
        {
            byte[] raw = Convert.FromBase64String(PublicKeyBase64);
            if (raw.Length != 64) return false;
            using ECDsa ec = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = raw.AsSpan(0, 32).ToArray(),
                    Y = raw.AsSpan(32, 32).ToArray()
                }
            });
            // 預設格式就是 IEEE P1363（r||s 各 32 bytes），與 tools\make-license.ps1 的
            // ECDsaCng.SignHash 相同。這一點是**量過的**：見進度日誌的 make-license 那一段。
            return ec.VerifyData(payload, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>base64url → bytes（補回 <c>=</c> 之後交給 Convert）。</summary>
    private static byte[] FromBase64Url(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("base64url length is impossible");
        }
        return Convert.FromBase64String(s);
    }

    /// <summary>`license.json` 裡的欄位（名字刻意短：這一段會佔用金鑰字串的長度）。</summary>
    private sealed class LicensePayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("v")] public int V { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("plan")] public string? Plan { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("issued")] public string? Issued { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("updatesUntil")] public string? UpdatesUntil { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("email")] public string? Email { get; set; }
    }
}
