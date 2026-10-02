using Microsoft.Win32;

namespace Sidecar;

/// <summary>
/// 「開機時自動啟動」的**唯一擁有者**（規格 §8 的 `StartWithWindows`，預設 on）。
///
/// 實作＝<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 底下一個值，
/// 名稱是 <see cref="ProductInfo.Name"/>，內容是**加引號的絕對路徑**（路徑含空白時不加引號
/// 會變成「找不到檔案」，而且 Windows 不會抱怨，只會靜默地什麼都不做）。
///
/// 兩條刻意的設計：
/// <list type="number">
/// <item>
/// <b>子機碼路徑由建構子注入</b>（<see cref="Autostart(string)"/>）：驗收必須能在一個
/// 用完即刪的臨時機碼上做完整個 enable/disable 循環，而不是在使用者真正的 Run 機碼上做。
/// 真機碼只有 <see cref="DefaultSubkeyPath"/> 這一個常數。
/// </item>
/// <item>
/// <b>只有常駐程式會套用它</b>（設定頁存檔時、以及啟動時）。<c>--settings-set</c> 是純粹的
/// 設定檔編輯器，**不得**碰 Run 機碼——否則任何一次自動化驗收都會悄悄改掉使用者的開機啟動設定，
/// 而那種副作用在事後幾乎不可能被發現。
/// </item>
/// </list>
///
/// 所有實際的機碼變更都經過 <see cref="Evidence"/> 記錄；「刪除一個本來就不存在的值」是 no-op，
/// 不是錯誤（停用一個從未啟用的設定是完全正常的操作）。
/// </summary>
internal sealed class Autostart
{
    /// <summary>真正的 Run 機碼。**唯一一份字面值**，預設工廠用它。</summary>
    public const string DefaultSubkeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _subkeyPath;
    private readonly Action<string> _log;

    /// <param name="subkeyPath">
    /// 相對於 <c>HKEY_CURRENT_USER</c> 的子機碼路徑（例如
    /// <c>Software\Microsoft\Windows\CurrentVersion\Run</c>）。驗收會指向一個臨時機碼。
    /// </param>
    /// <param name="log">證據記錄。可為 null（不記錄，但行為不變）。</param>
    public Autostart(string subkeyPath, Action<string>? log = null)
    {
        _subkeyPath = subkeyPath ?? string.Empty;
        _log = log ?? (_ => { });
    }

    /// <summary>指向真實 Run 機碼的預設實例（常駐程式用這個）。</summary>
    public static Autostart Default(Action<string> log) => new(DefaultSubkeyPath, log);

    public string SubkeyPath => _subkeyPath;

    /// <summary>
    /// 應該寫進機碼的值：**加引號的絕對 exe 路徑**（<c>Environment.ProcessPath</c>）。
    ///
    /// 這是一個**純函式**（不碰登錄檔），所以「值長什麼樣」可以在完全不寫任何東西的情況下被斷言——
    /// 驗收腳本不必先污染使用者的機碼才能知道正確答案是什麼。
    /// 路徑取不到時回空字串：呼叫端據此拒絕啟用，而不是寫一個指向「無」的開機項目進去。
    /// </summary>
    public static string ExpectedValueFor(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return string.Empty;
        return "\"" + processPath.Trim() + "\"";
    }

    /// <summary>本行程的預期值。</summary>
    public string ExpectedValue() => ExpectedValueFor(Environment.ProcessPath);

    /// <summary>機碼裡目前的值；不存在或讀不到時回 null（兩者對「有沒有啟用」是同一個意思）。</summary>
    public string? CurrentValue()
    {
        if (_subkeyPath.Length == 0)
        {
            _log("autostart: no subkey path -> treating as not enabled");
            return null;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_subkeyPath, writable: false);
            object? value = key?.GetValue(ProductInfo.Name);
            return value as string;
        }
        catch (Exception ex)
        {
            // 讀不到（權限、機碼被政策鎖住）時回 null 並說出來：把它當成「沒啟用」是安全的，
            // 因為接下來的動作就是再寫一次。
            _log($"autostart: cannot read HKCU\\{_subkeyPath} ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// 是否已啟用。判準是「值存在且**就是我們預期的路徑**」——只看值存不存在是不夠的：
    /// 換了安裝位置之後，舊路徑留下的那個值會讓畫面顯示「已啟用」，實際上開機啟動的是舊的 exe。
    /// </summary>
    public bool IsEnabled()
    {
        string? current = CurrentValue();
        if (current == null) return false;

        string expected = ExpectedValue();
        if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
        {
            _log($"autostart: enabled but points somewhere else: registry='{current}' expected='{expected}'");
            return false;
        }
        return true;
    }

    /// <summary>寫入 Run 值。已經是同一個值時不重寫（避免每次啟動都產生一次無意義的登錄檔寫入）。</summary>
    public void Enable()
    {
        string expected = ExpectedValue();
        if (expected.Length == 0)
        {
            _log("autostart: REFUSED to enable (Environment.ProcessPath is empty; the entry would point nowhere)");
            return;
        }

        if (string.Equals(CurrentValue(), expected, StringComparison.OrdinalIgnoreCase))
        {
            _log($"autostart: already enabled with the expected value ({expected}); no write");
            return;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(_subkeyPath, writable: true)
                ?? throw new InvalidOperationException("CreateSubKey returned null");
            key.SetValue(ProductInfo.Name, expected, RegistryValueKind.String);
            _log($"autostart: ENABLED HKCU\\{_subkeyPath} value '{ProductInfo.Name}' = {expected}");
        }
        catch (Exception ex)
        {
            _log($"autostart: FAILED to enable ({ex.GetType().Name}: {ex.Message}); start with Windows is NOT set");
        }
    }

    /// <summary>移除 Run 值。值不存在＝no-op（不是錯誤），機碼本身留著（那底下還有別的程式）。</summary>
    public void Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_subkeyPath, writable: true);
            if (key == null)
            {
                _log($"autostart: already disabled (HKCU\\{_subkeyPath} does not exist); nothing to remove");
                return;
            }

            if (key.GetValue(ProductInfo.Name) == null)
            {
                _log($"autostart: already disabled (value '{ProductInfo.Name}' is absent); nothing to remove");
                return;
            }

            key.DeleteValue(ProductInfo.Name, throwOnMissingValue: false);
            _log($"autostart: DISABLED (removed '{ProductInfo.Name}' from HKCU\\{_subkeyPath})");
        }
        catch (Exception ex)
        {
            _log($"autostart: FAILED to disable ({ex.GetType().Name}: {ex.Message}); the entry may still be there");
        }
    }

    /// <summary>一行說完目前狀態（日誌與自測共用，避免兩種說法）。</summary>
    public string Describe() =>
        $"subkey=HKCU\\{_subkeyPath} name='{ProductInfo.Name}' expected={ExpectedValue()} " +
        $"current={(CurrentValue() ?? "(absent)")} enabled={IsEnabled()}";
}
