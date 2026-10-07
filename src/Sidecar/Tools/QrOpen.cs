using System.Diagnostics;

namespace Sidecar;

/// <summary>
/// 「解出來的內容是網址 → 開瀏覽器；不是 → 顯示文字」的**判定**（工具 `read-qr`）。
///
/// ## 為什麼是一個純函式
///   這條判定決定了一件有副作用的事（開一個外部程式）。有副作用的判定若和呼叫端綁在一起，
///   驗收就只能靠**真的開一次瀏覽器**來證明它——那是在使用者的機器上留下足跡。
///   所以判定本身必須是一個「給字串、回分類」的純函式，讓驗收能列舉輸入而不觸發任何副作用。
///
/// ## ⚠ 安全收窄（使用者 2026-10-07 裁定，這裡照做）
///   **只有 `http` 與 `https` 這兩個 scheme 才准開瀏覽器。**
///   QR 可以裝任何字串，而 <c>file://</c>、<c>javascript:</c>、<c>ms-msdt:</c>、<c>search-ms:</c>
///   這類 scheme 交給 <c>ShellExecute</c> 等於**讓一張圖片叫得動本機程式**（本機檔案、
///   協定處理常式、甚至已知的 msdt 遠端執行鏈）。那不是這個工具該有的能力：使用者的要求是
///   「解出來的內容是網址就用瀏覽器打開」，而「網址」在這裡的意思就是 http／https。
///   其餘一律走「顯示文字」——顯示文字不會有任何副作用，而且使用者仍然看得到完整內容。
///
///   這條收窄是**白名單**（先認 scheme 再放行），不是黑名單（列出不准的 scheme）：
///   黑名單永遠追不上新的處理常式，而白名單的失敗方向是「少開一個瀏覽器」，不是「多開一個程式」。
/// </summary>
internal static class QrTextClassifier
{
    /// <summary>這一組就是白名單；除此之外的一律是「文字」。</summary>
    private static readonly string[] BrowserSchemes = { "http", "https" };

    /// <summary>內容的分類。只有 <see cref="Url"/> 會被交給瀏覽器。</summary>
    internal enum QrKind
    {
        /// <summary>空字串或只有空白：沒有東西可以開、也沒有東西可以複製。</summary>
        Empty,

        /// <summary>http／https 的絕對 URI，且 host 非空 → 交給預設瀏覽器。</summary>
        Url,

        /// <summary>認不得的字串（包含所有其他 scheme）→ 顯示在畫面上、可複製。</summary>
        Text
    }

    /// <summary>
    /// 分類。<paramref name="raw"/> 是解碼器吐出來的**原封不動**的字串：
    /// 🔴 這一條**不修改傳入的字串**（不 trim、不截斷、不改大小寫）——產品顯示與複製的都必須是
    /// 解碼器給的那一份，否則「逐字元相同」的斷言會變成在斷言一個加工過的版本。
    /// 前後空白只在**判定**時被忽略（見 <see cref="BrowserTarget"/> 的說明）。
    /// </summary>
    internal static QrKind Classify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return QrKind.Empty;
        return BrowserTarget(raw) != null ? QrKind.Url : QrKind.Text;
    }

    /// <summary>分類的標籤（診斷輸出用；**是驗收斷言的字面值**）。</summary>
    internal static string KindLabel(QrKind kind) => kind switch
    {
        QrKind.Url => "url",
        QrKind.Text => "text",
        _ => "empty"
    };

    /// <summary>
    /// 該交給瀏覽器的**那一個字串**（＝分類為 <see cref="QrKind.Url"/> 時要開啟的目標），
    /// 不是網址就回 <c>null</c>。分類與目標由這裡**同一個實作**回答，所以
    /// 「說它是網址、開的卻是別的字串」這種分歧不可能發生。
    ///
    /// 為什麼判定用 <c>Trim()</c> 過的字而顯示／複製用原始字串：QR 常常把內容編成一行
    /// 而兩端夾了換行（掃描器也可能留下它），此時使用者看到的確實是一個網址；
    /// 而 <c>new Uri(" http://x ")</c> 在 .NET 的解析下是失敗的——**只有判定**忽略前後空白。
    /// 顯示那一份永遠是原始字串（見 <see cref="QrTextClassifier.Classify"/> 的紅線）。
    ///
    /// 兩個條件缺一不可：
    ///   ① scheme 在 <see cref="BrowserSchemes"/> 白名單裡（<c>file</c>／<c>javascript</c>／
    ///      <c>ms-msdt</c>／<c>search-ms</c> 因此當場落選）；
    ///   ② <c>Uri.SchemeDelimiter</c>（`//`）真的在 scheme 後面，且 host 非空。
    ///      ②是刻意比 <see cref="Uri.TryCreate(string?, UriKind)"/> 更嚴：`http:foo.com` 在
    ///      RFC 3986 之下是「合法的 URI」（scheme-specific part 是 `foo.com`），
    ///      交給 ShellExecute 會變成一條**說不出要去哪裡**的請求。寧可當成文字顯示。
    /// </summary>
    internal static string? BrowserTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string candidate = raw.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)) return null;

        bool schemeAllowed = false;
        foreach (string scheme in BrowserSchemes)
        {
            if (string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            {
                schemeAllowed = true;
                break;
            }
        }
        if (!schemeAllowed) return null;

        // ② `//` 必須緊接在 scheme 之後（大小寫不敏感：scheme 在 URI 裡本來就不分大小寫）。
        if (!candidate.StartsWith(uri.Scheme + Uri.SchemeDelimiter, StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.IsNullOrEmpty(uri.Host)) return null;
        return candidate;
    }
}

/// <summary>
/// 開預設瀏覽器——**唯一**會叫動外部程式的閘門，而且預設是關著測的。
///
/// ## 為什麼要有這個縫（本專案既有慣例）
///   套件在使用者的機器上跑。這一條路徑的副作用是**在使用者的桌面上彈出一個瀏覽器**，
///   那和「搶使用者的滑鼠」是同一類問題（本專案 2026-10-06 就是為此立下「預設跑套件絕不注入
///   合成輸入」的規矩）。所以正式路徑與自測路徑**共用同一個閘門**，而閘門在自測模式下
///   只把「我本來要開這個」記下來，不呼叫 <see cref="Process.Start(ProcessStartInfo)"/>。
///
/// ## 兩道鎖，而不是一道
///   ①<see cref="UseSelfTestSeam"/>：自測模式把實作整支換掉（連 <c>Process.Start</c> 都不會走到）；
///   ②<see cref="RealLaunchAttempts"/>：**真的那支實作**自己的計數器。它是用來證明①有效的
///   ——沒有它，「縫有沒有生效」只能靠讀程式碼相信（見 <see cref="QrOpenSelfTest"/> 的斷言）。
/// </summary>
internal static class QrBrowserOpener
{
    /// <summary>正式路徑：真的叫 <c>ShellExecute</c>。null 之前就是這一個。</summary>
    private static readonly Func<string, bool> Real = url =>
    {
        Interlocked.Increment(ref _realLaunchAttempts);
        // UseShellExecute=true ＝ 走 shell 的「開啟這個東西」那條路，也就是預設瀏覽器。
        // 這是**唯一**碰 Process.Start 的地方（見 QrTextClassifier 的安全收窄）。
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    };

    private static Func<string, bool> _opener = Real;
    private static int _realLaunchAttempts;
    private static int _seamOpened;
    private static string _seamTarget = "";

    /// <summary>自測模式記下來的目標（＝「本來要開這個」的證據）。</summary>
    internal static string LastTarget => Volatile.Read(ref _seamTarget);

    /// <summary>自測模式下被要求開啟的次數。</summary>
    internal static int SeamCallCount => Volatile.Read(ref _seamOpened);

    /// <summary>**真的那支實作**被執行的次數。自測模式下必須永遠是 0。</summary>
    internal static int RealLaunchAttempts => Volatile.Read(ref _realLaunchAttempts);

    /// <summary>
    /// 換掉實作（自測用）。<c>enabled=false</c> 還原成真的那一個。
    /// 只由診斷入口呼叫一次；產品路徑**不讀任何旗標**，它永遠走真的那一個。
    /// </summary>
    internal static void UseSelfTestSeam(bool enabled) =>
        Volatile.Write(ref _opener, enabled ? Seam : Real);

    private static bool Seam(string url)
    {
        Interlocked.Increment(ref _seamOpened);
        Volatile.Write(ref _seamTarget, url);
        return true;
    }

    /// <summary>要求開啟一個 URL。回 false＝開不起來（原因在 <paramref name="error"/>）。</summary>
    internal static bool TryOpen(string url, out string? error)
    {
        error = null;
        try
        {
            return _opener(url);
        }
        catch (Exception ex)
        {
            // 沒有預設瀏覽器、shell 拒絕這條協定…都不是崩潰的理由：回報，UI 繼續運作。
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 目前執行中的瀏覽器行程數（chrome／msedge／firefox／iexplore）。
    /// 它是「有沒有真的彈出瀏覽器」的**外部**量測：自測在要求開瀏覽器前後各量一次，
    /// 兩次相同才算真的沒有副作用。刻意只讀不碰（<c>GetProcessesByName</c>）。
    /// </summary>
    internal static int BrowserProcessCount()
    {
        int count = 0;
        foreach (string name in new[] { "chrome", "msedge", "firefox", "iexplore", "brave", "opera" })
        {
            try
            {
                count += Process.GetProcessesByName(name).Length;
            }
            catch (Exception)
            {
                // 讀不到某一個名字不是錯誤（權限、行程剛好結束）；它不會讓整個量測失敗。
            }
        }
        return count;
    }
}

/// <summary>
/// 「解出來的內容該去哪裡」的**單一出口**（工具 `read-qr`）。
///
/// 判定 → 副作用 這一條鏈只有這一個實作：分類是 <see cref="QrTextClassifier"/>、
/// 開瀏覽器是 <see cref="QrBrowserOpener"/>、顯示文字是 <see cref="QrTextUi"/>，
/// 而**它們三個的順序**寫在這裡。正式路徑（<c>App.CommitWheel</c> 的 read-qr 那一條）
/// 與自測路徑都呼叫它，所以自測量到的就是產品真的會做的事。
/// </summary>
internal static class QrTextRouter
{
    /// <summary>
    /// 把一段解出來的文字送出去。<paramref name="sourceName"/> 只用在文字視窗的標題列
    /// （「這張圖解出來的」），不參與判定、也不參與開啟。
    ///
    /// 回傳**當時做了什麼**（給診斷斷言）：`url`／`text`／`empty`，以及開不開得起來。
    /// </summary>
    internal static (QrTextClassifier.QrKind Kind, bool Opened, string Target, string Note)
        Route(string text, string sourceName, Action<string>? log)
    {
        QrTextClassifier.QrKind kind = QrTextClassifier.Classify(text);
        string? target = QrTextClassifier.BrowserTarget(text);

        if (kind == QrTextClassifier.QrKind.Url && target != null)
        {
            bool opened = QrBrowserOpener.TryOpen(target, out string? error);
            string note = opened
                ? $"opened in the default browser ({target.Length} chars)"
                : $"the browser could not be opened ({error})";
            log?.Invoke(opened
                ? $"read-qr: the text is an http/https url -> opening it in the default browser"
                : $"read-qr: the text is an http/https url but the browser refused it: {error}");
            return (kind, opened, target, note);
        }

        if (kind == QrTextClassifier.QrKind.Empty)
        {
            // 空字串連視窗都不開：一個沒有內容的視窗只是要使用者自己按關掉。
            log?.Invoke("read-qr: the decoded text is empty; there is nothing to show and nothing to open");
            return (kind, false, "", "nothing to show (the decoded text is empty)");
        }

        // 非網址（含所有不在白名單裡的 scheme）：**只顯示，不做任何其他事**。
        log?.Invoke($"read-qr: the text is not an http/https url -> showing it (no file, no side effect)");
        QrTextUi.Open(text, sourceName, log);
        return (kind, true, "", "shown in the text window (copy with the button or Ctrl+C)");
    }
}
