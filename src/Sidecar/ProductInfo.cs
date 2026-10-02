namespace Sidecar;

/// <summary>
/// 產品名稱的**唯一擁有者**（2026-10-02 使用者定案：Confern）。
///
/// 為什麼要有這個檔案：名字會出現在 exe 名、`%LOCALAPPDATA%` 底下的路徑、視窗類別名、視窗標題，
/// 以及之後的托盤提示與開機自動啟動的登錄檔機碼。散在各處硬寫的話，改名會變成
/// 「改了九成、剩一成還在用舊名字」——而且**不會有任何錯誤**，只會在使用者的 AppData 底下
/// 長出兩套目錄、或托盤顯示舊名字。單一擁有者讓「改名」是一件改了就會全部一致的事。
///
/// 注意：C# 命名空間仍是 `Sidecar`（歷史名稱）。命名空間不是使用者可見的東西，全專案改名是
/// 獨立的機械式清理，刻意不混進這次的產品改名——混進去會讓「產品改名」與「程式碼重構」
/// 兩件事的 diff 疊在一起，出事時分不出是哪一件造成的。
/// </summary>
internal static class ProductInfo
{
    /// <summary>產品名稱。使用者可見處一律引用這裡，不要再硬寫字面值。</summary>
    public const string Name = "Confern";

    /// <summary>
    /// 小寫識別字：日誌檔名、pipe 名、mutex 名、暫存檔名等技術用途。
    /// 由 <see cref="Name"/> 推導，不是第二份字面值——名字改一次就會全部跟著改。
    /// </summary>
    public static string Slug => Name.ToLowerInvariant();

    /// <summary>`%LOCALAPPDATA%\Confern`：設定、日誌、暫存日記都在這裡（目錄由各擁有者自行建立）。</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
}
