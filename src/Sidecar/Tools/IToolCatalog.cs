namespace Sidecar;

/// <summary>
/// 工具輪盤的圖示。**由程式繪製的線稿**，不引入圖片資源（本專案目前沒有圖示資產，
/// 而設定頁已經立下「XAML 內 0 個硬編碼色、0 個文字字面值」的紀律；
/// 引進一張 PNG 會同時破壞配色唯一權威與離線不變式）。
/// </summary>
internal enum ToolIcon
{
    /// <summary>順時針 90 度的轉向箭頭（兩個弧形箭頭）。</summary>
    Rotate90,

    /// <summary>
    /// 被劃掉的中繼資料標籤（tag）——「把檔案裡那些看不見的標籤去掉」。
    /// 選這個形狀是因為 EXIF／ICC／文字註解在容器裡是**同一種東西**（掛在影像資料外的區段），
    /// 而 17px 的線稿畫不了「三種各畫一個」；標籤＋斜線是唯一一個能在這個尺寸辨識的形狀。
    /// </summary>
    StripMetadata
}

/// <summary>
/// 一個工具。**注意 <see cref="Label"/> 與第一層的格式名是同一個視覺階層**——
/// 兩者都畫在扇區上，因此都刻意用大寫英文短詞（不進 i18n：扇區只有 80×24 px 的空間，
/// 而中文在該尺寸下的字重與英文不一致，參考圖的扇區也全是英文）。
/// 圓心膠囊與說明文字才是中文。
/// </summary>
/// <param name="Id">穩定識別碼。會進日誌與 worker 協議，**不得就地改字**（改了等於換一個工具）。</param>
/// <param name="Label">扇區標籤（大寫英文）。</param>
/// <param name="Description">圓心膠囊的一句話（zh-Hant）——參考設計的「中心顯示 hover 動作」。</param>
/// <param name="Icon">線稿圖示。</param>
internal sealed record ToolDescriptor(string Id, string Label, string Description, ToolIcon Icon);

/// <summary>
/// 工具知識的**唯一查詢介面**（規格 §9.4:390 的工具輪盤擴充點）。
///
/// 分工比照 <see cref="IFormatCatalog"/>：<see cref="WheelPresenter"/> 只拿到「一排字串」，
/// **不得自己判斷有哪些工具、也不得自己決定某一格能不能用**。工具的清單與適用性都屬於工具知識，
/// 只有這個介面說了算。
///
/// **只回已實作且適用的工具**：介面不得顯示還不能執行的工具。設定頁已經有「存得下但沒生效」
/// 的前例，那個教訓是「畫面上有的東西必須是真的」。
/// </summary>
internal interface IToolCatalog
{
    /// <summary>目前**已實作**的工具。順序即扇區順序（自正上方起、順時針）。</summary>
    IReadOnlyList<ToolDescriptor> Tools { get; }

    /// <summary>
    /// 這一批檔案適用的工具。拒絕語意沿用第一層（規格 §4.3）：不是 <see cref="PayloadStatus.Ready"/>
    /// 就一個工具都不給，呼叫端照既有規則顯示拒絕訊息。
    /// </summary>
    IReadOnlyList<ToolDescriptor> ToolsFor(PayloadVerdict verdict);
}
