namespace Sidecar;

/// <summary>
/// 介面字型的**唯一權威**（2026-10-21，加入日文／韓文／簡中之後）。
///
/// 為什麼要有一個地方決定：這條產品線有兩套畫字的路徑——
///   GDI+（<see cref="WheelPresenter"/> 的扇區與膠囊、<see cref="ProgressPresenter"/> 的進度卡）
///   只能吃**一個**字型名；
///   WPF（設定頁、裁切、合併、QR）吃的是逗號分隔的堆疊。
/// 兩條路徑若不指向同一套決定，同一個字會在不同視窗長得不一樣，而且沒有人會發現
/// （dump 一張一張看，看不出「這張跟那張不是同一個字型」）。
///
/// ⚠ 中文與英文**刻意維持原樣**：它們目前靠 Windows 的字型連結把漢字交給
/// Microsoft JhengHei 畫，設定頁與輪盤的 dump 逐位元相同就是證據（2026-10-21 量過）。
/// 只有日／韓／簡中換主字型——**日文用中文字型畫會出現日本人不認的字形**
/// （直、骨、込 那一類的字形差異），這是行家一眼就會看出來的品質問題，不是偏好問題。
///
/// 這是一個純函式：沒有狀態、沒有 I/O，唯一讀的是 <see cref="UiStrings.Current"/>。
/// </summary>
internal static class Typeface
{
    /// <summary>GDI+ 用的單一字型名（依目前介面語言）。</summary>
    public static string Primary => UiStrings.Current switch
    {
        UiStrings.Ja => "Yu Gothic UI",
        UiStrings.Ko => "Malgun Gothic",
        UiStrings.ZhHans => "Microsoft YaHei UI",
        _ => "Segoe UI",
    };

    /// <summary>
    /// WPF 用的堆疊。⚠ 中文／英文那一串**連空白都不能動**：它就是 2026-10-21 之前
    /// 的樣子，改了 dump 就不會再逐位元相同。
    /// </summary>
    public static string Stack => UiStrings.Current switch
    {
        UiStrings.Ja => "Yu Gothic UI,Segoe UI,Microsoft JhengHei UI,Tahoma",
        UiStrings.Ko => "Malgun Gothic,Segoe UI,Microsoft JhengHei UI,Tahoma",
        UiStrings.ZhHans => "Microsoft YaHei UI,Segoe UI,Tahoma",
        _ => "Segoe UI, Microsoft JhengHei UI, Microsoft YaHei UI, Tahoma",
    };
}
