using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

// 這個專案同時開了 UseWPF 與 UseWindowsForms，同名型別會「模稜兩可」（見 SettingsUi.cs 的說明）。
// 這個視窗要的一律是 WPF 那一組，所以逐項釘死，避免哪天漏打全名而悄悄換成 WinForms 的型別。
// 只引入明確無歧義的命名空間，其餘靠這些別名（與 CropWindow.cs 同一條紀律）。
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
// ⚠ 這一條是**必須**的，不是潔癖：`System.Windows.Shapes` 裡有一個 `Path`（圖形），
//    而這個檔案用的一律是 `System.IO.Path`（檔名運算）。不釘死的話每一個
//    `Path.GetFileName` 都是 CS0104（實測：不加這一行會一次冒出 7 個模稜兩可的錯誤）。
using Path = System.IO.Path;
using Point = System.Windows.Point;
using RectangleShape = System.Windows.Shapes.Rectangle;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Sidecar;

/// <summary>
/// 合併 PDF 的**排序視窗**（使用者 2026-10-07 裁定的下一刀）。
///
/// ## 它存在的理由
///   `merge-pdf` 是**唯一一個「多輸入、一輸出、而且順序有意義」**的工具。其他五顆扇形的輸入是
///   一組無序的檔（每一個各自產生一個結果），所以「順序」在她們身上根本不存在。這裡不一樣：
///   `a;b;c` 與 `c;b;a` 是兩個不同的請求，會產出兩個不同的檔（見 <see cref="ConversionEngine.MergePdf"/>
///   的排序語意）。也就是說**順序是這個工具唯一的實質輸入**，而它只有使用者知道——所以它必須被問，
///   不能由程式猜（例如「照檔名排」：使用者的那疊紙不是照檔名排的）。
///
/// ## 使用者做得到的三件事（其他都不做）
///   ① **拖曳排序**：把一列往上／往下拖，順序當場改變；
///   ② **移除**：把不該合併的那一份移掉；
///   ③ **確認**／**取消**：確認之後才依**畫面上顯示的順序**提交；取消則什麼都不做。
///   刻意**沒有**加入「複製一份」「自動排序」「頁面預覽」這類功能：這一刀要解決的是順序，
///   而每一個多出來的功能都是一條要驗的路。
///
/// ## 順序就是畫面（這一格是這個視窗的整個契約）
///   <see cref="Order"/>（＝確認時交出去的東西）**就是** <see cref="_order"/>，
///   而畫面的列是由 <see cref="_order"/> 依序畫出來的、每一列的編號也由它推導。
///   所以「畫面上顯示的順序＝輸出頁序」不是靠兩份東西保持一致，而是**只有一份東西**。
///   <see cref="SelfTestOrder"/> 讀的也是同一份，不是另外記一份「給測試看的順序」。
///
/// ## 為什麼不用 WPF 的 DragDrop
///   `DragDrop.DoDragDrop` 會**進入一個內部的模態迴圈**（它自己抽訊息），而這個專案的自測一律走
///   可程式化的接縫、絕不合成滑鼠輸入。用一個會吃掉訊息的 API 會讓「拖曳」這一格只能靠真滑鼠驗，
///   也就是**驗不到**。所以拖曳是自己做的：按下記住起點、移動時換算成列位移、放開才定案
///   （見 <see cref="BeginDrag"/>／<see cref="ContinueDrag"/>／<see cref="EndDrag"/>），
///   而 <see cref="SelfTestDrag"/> 餵進去的正是那三個方法——與真滑鼠**同一條路**，中間沒有測試分支。
///
/// ## 不准丟例外
///   這個專案為「事件處理常式丟例外殺掉 UI 執行緒」付過代價（見 CropWindow 的說明）。所以每一個
///   輸入（拖曳、移除、確認、程式化設定順序）都經過 <see cref="Sanitize"/>，它保證
///   <see cref="_order"/> 永遠是一份「非空、每一項都在原始批次裡、沒有重複」的清單。
///   退化輸入只顯示提示，不丟出去；建構與顯示失敗由 <see cref="MergeUi"/> 那個 catch 接手。
/// </summary>
internal sealed class MergeWindow : Window
{
    /// <summary>一列的高度（px）。拖曳的換算要用它，所以它是常數而不是算出來的。</summary>
    public const double RowHeightPx = 40;

    /// <summary>列與列之間的間距（px）。拖曳換算的「一列」＝ <see cref="RowHeightPx"/> ＋ 這個值。</summary>
    public const double RowGapPx = 6;

    /// <summary>清單一次顯示幾列。超出的部分靠「上／下」分頁鈕（刻意**不用** ScrollViewer，
    /// 理由見 <see cref="BuildList"/>）。</summary>
    public const int WindowRowsPx = 10;

    private readonly string[] _original;
    private readonly Action<string>? _log;

    /// <summary>目前的順序。**這是唯一的權威**：畫面、編號、確認交出去的值全部由它推導。</summary>
    private readonly List<string> _order = new();

    /// <summary>每一份的頁數（讀不到＝null）。只在建構時讀一次。</summary>
    private readonly Dictionary<string, int?> _pageCounts = new(StringComparer.Ordinal);

    private readonly StackPanel _list = new();
    private readonly TextBlock _summaryText = new();
    private readonly TextBlock _noticeText = new();
    private readonly TextBlock _rangeText = new();
    private Button _upButton = new();
    private Button _downButton = new();

    /// <summary>畫面上目前畫出來的列（＝ <see cref="_order"/> 的一個視窗），順序與它一致。</summary>
    private readonly List<Border> _rows = new();

    /// <summary>清單視窗的第一列在 <see cref="_order"/> 裡的索引。</summary>
    private int _listTop;

    // 拖曳狀態（見 BeginDrag／ContinueDrag／EndDrag）
    private int _dragFrom = -1;
    private int _dragTo = -1;
    private Point _dragStart;

    /// <summary>使用者按下「確認」時的順序。null ＝ 還沒確認（取消之後也是 null）。</summary>
    public string[]? Order { get; private set; }

    /// <summary>確認鈕／取消鈕（自測要按的是**真的按鈕**，不是另一條內部路徑）。</summary>
    public Button OkButton { get; }
    public Button CancelButton { get; }

    public MergeWindow(IReadOnlyList<string> paths, Action<string>? log)
    {
        _log = log;
        _original = paths?.ToArray() ?? Array.Empty<string>();
        foreach (string path in _original) _order.Add(path);
        foreach (string path in _original)
            if (!_pageCounts.ContainsKey(path)) _pageCounts[path] = ConversionEngine.TryReadPdfPageCount(path);

        Title = ProductInfo.Name;
        Width = 720;
        Height = 760;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SettingsUi.StyleKit.WindowBg;
        Foreground = SettingsUi.StyleKit.TextMain;
        FontFamily = SettingsUi.StyleKit.Font;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 標題
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 清單
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 分頁列
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 提示
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按鈕
        Content = root;

        BuildHeader(root);
        BuildList(root);
        BuildPager(root);
        BuildNotice(root);

        // ⚠ 兩顆按鈕**必須先建好**再交給 BuildFooter（與 CropWindow 同一個順序）：它們是唯讀的
        //    自動屬性，只能在建構函式裡被指派一次；把建立藏在 BuildFooter 裡就沒有地方可以指派。
        OkButton = new Button { Content = "確認 (Enter)", Width = 128, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
        CancelButton = new Button { Content = "取消 (Esc)", Width = 128, Height = 32 };
        SettingsUi.StyleKit.PrimaryButton(OkButton);
        SettingsUi.StyleKit.SecondaryButton(CancelButton);
        OkButton.Click += (_, _) => Confirm();
        CancelButton.Click += (_, _) => CancelWithLog("cancel button");

        BuildFooter(root);

        Sanitize();
        RebuildRows();
        _log?.Invoke($"merge ui: window created for {_order.Count} file(s) in the given order " +
                     $"=[{string.Join(",", _order.Select(Path.GetFileName))}]");
    }

    // =====================================================================================
    // 畫面
    // =====================================================================================

    private void BuildHeader(Grid root)
    {
        var header = new StackPanel();
        var title = new TextBlock { Text = "合併 PDF：排順序", FontSize = 21, FontWeight = FontWeights.SemiBold };
        title.Foreground = SettingsUi.StyleKit.TextMain;

        // 這一句是整個視窗的說明。它必須說出**兩件使用者會懷疑的事**：
        //   ① 順序有意義（不是裝飾，會改變結果）；
        //   ② 取消不會產生任何檔案（否則「取消」看起來像「照原順序合併」）。
        var subtitle = new TextBlock
        {
            Text = "上面的順序就是要合併的順序（第 1 個檔的頁面會在最前面）。" +
                   "拖曳右邊的把手可以上下移動，垃圾桶可以移除。確認之後才合併，取消不會產生任何檔案。",
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        subtitle.Foreground = SettingsUi.StyleKit.TextDim;

        header.Children.Add(title);
        header.Children.Add(subtitle);
        Grid.SetRow(header, 0);
        root.Children.Add(header);
    }

    /// <summary>
    /// 清單本體。
    ///
    /// ⚠ **刻意不用 <c>ScrollViewer</c>**：這個專案有一條「視窗裡不可以出現有可捲動範圍的捲軸」
    ///   的斷言（CropWindow 的 <c>scroll</c>），理由是**捲軸把內容切掉**這件事從截圖看得出來、
    ///   卻沒有任何腳本讀得出來。所以「檔案比畫面多」的處置是**自己的一組上／下分頁鈕**，
    ///   它讓「現在看到的是第幾到第幾列、總共幾列」變成畫面上的一行可讀文字，而不是一個要拖的 bar。
    /// </summary>
    private void BuildList(Grid root)
    {
        var border = new Border
        {
            Background = SettingsUi.StyleKit.SurfaceBg,
            BorderBrush = SettingsUi.StyleKit.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(8)
        };
        _list.Orientation = System.Windows.Controls.Orientation.Vertical;
        border.Child = _list;
        Grid.SetRow(border, 1);
        root.Children.Add(border);
    }

    private void BuildPager(Grid root)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };

        _upButton = new Button { Content = "上", Width = 56, Height = 26 };
        _downButton = new Button { Content = "下", Width = 56, Height = 26 };
        SettingsUi.StyleKit.SecondaryButton(_upButton);
        SettingsUi.StyleKit.SecondaryButton(_downButton);
        _upButton.Click += (_, _) => Page(-1);
        _downButton.Click += (_, _) => Page(+1);

        _rangeText.FontSize = 12;
        _rangeText.Margin = new Thickness(10, 0, 0, 0);
        _rangeText.VerticalAlignment = VerticalAlignment.Center;
        _rangeText.Foreground = SettingsUi.StyleKit.TextFaint;

        _summaryText.FontSize = 12;
        _summaryText.Margin = new Thickness(10, 0, 0, 0);
        _summaryText.VerticalAlignment = VerticalAlignment.Center;
        _summaryText.Foreground = SettingsUi.StyleKit.TextMain;

        row.Children.Add(_upButton);
        row.Children.Add(_downButton);
        row.Children.Add(_rangeText);
        row.Children.Add(_summaryText);
        Grid.SetRow(row, 2);
        root.Children.Add(row);
    }

    private void BuildNotice(Grid root)
    {
        _noticeText.FontSize = 12;
        _noticeText.Margin = new Thickness(0, 8, 0, 0);
        _noticeText.TextWrapping = TextWrapping.Wrap;
        _noticeText.Foreground = SettingsUi.StyleKit.WarningFg;
        _noticeText.Text = "";
        Grid.SetRow(_noticeText, 3);
        root.Children.Add(_noticeText);
    }

    private void BuildFooter(Grid root)
    {
        var footer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        footer.Children.Add(OkButton);
        footer.Children.Add(CancelButton);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
    }

    // =====================================================================================
    // 順序 → 畫面（**單向**：畫面永遠是從 _order 畫出來的，沒有任何「畫面自己記的順序」）
    // =====================================================================================

    /// <summary>重畫清單。任何順序變更之後都必須叫它；它同時重算編號、分頁範圍與摘要。</summary>
    private void RebuildRows()
    {
        _list.Children.Clear();
        _rows.Clear();

        int maxTop = Math.Max(0, _order.Count - WindowRowsPx);
        _listTop = Math.Clamp(_listTop, 0, maxTop);
        int last = Math.Min(_order.Count, _listTop + WindowRowsPx);

        for (int i = _listTop; i < last; i++)
        {
            Border row = BuildRow(i);
            _rows.Add(row);
            _list.Children.Add(row);
        }

        _rangeText.Text = $"顯示第 {(_order.Count == 0 ? 0 : _listTop + 1)}-{last} 列，共 {_order.Count} 列";
        _upButton.IsEnabled = _listTop > 0;
        _downButton.IsEnabled = last < _order.Count;

        int? total = 0;
        int unreadable = 0;
        foreach (string path in _order)
        {
            int? count = _pageCounts.TryGetValue(path, out int? c) ? c : null;
            if (count is { } n && total is { } sum) total = sum + n;
            else { total = null; }
            if (count == null) unreadable++;
        }
        // 摘要說的是「這一份合併檔會有幾頁」。讀不到就不報總數——0 與「不知道」是不同的主張，
        // 而把讀不到的那一份算成 0 頁會讓使用者以為它不存在。
        _summaryText.Text = total is { } pages
            ? $"合計 {pages} 頁"
            : $"合計頁數未知（{unreadable} 個檔讀不到頁數）";
    }

    /// <summary>畫一列。編號由 <paramref name="index"/> 推導，所以「畫面上的編號」與「真正的順序」不可能不一致。</summary>
    private Border BuildRow(int index)
    {
        string path = _order[index];
        string name = Path.GetFileName(path);
        int? count = _pageCounts.TryGetValue(path, out int? c) ? c : null;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });    // 編號
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 檔名
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // 頁數
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // 把手
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // 移除

        var number = new TextBlock
        {
            Text = $"{index + 1}.",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        number.Foreground = SettingsUi.StyleKit.TextMain;
        Grid.SetColumn(number, 0);
        grid.Children.Add(number);

        var label = new TextBlock
        {
            Text = name,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = path
        };
        label.Foreground = SettingsUi.StyleKit.TextMain;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var pages = new TextBlock
        {
            // 「讀不到頁數」說出來，不寫 0：0 頁是一個具體的主張，而這裡的主張是「不知道」。
            Text = count is { } n ? $"{n} 頁" : "頁數讀不到",
            FontSize = 12,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        pages.Foreground = count is { } ? SettingsUi.StyleKit.TextDim : SettingsUi.StyleKit.WarningFg;
        Grid.SetColumn(pages, 2);
        grid.Children.Add(pages);

        // 「把手」是一個**視覺提示**：整列都可拖，但只有把手會把游標換成拖曳形狀。
        // 刻意不做成唯一的可拖區域——小小的拖拉目標本身就是「拖不動」的常見原因。
        var grip = new TextBlock
        {
            Text = "\u2261",
            FontSize = 18,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeNS,
            ToolTip = "按住這裡上下拖曳來調整順序"
        };
        grip.Foreground = SettingsUi.StyleKit.TextFaint;
        Grid.SetColumn(grip, 3);
        grid.Children.Add(grip);

        var remove = new Button
        {
            Content = "移除",
            Width = 62,
            Height = 26,
            Margin = new Thickness(12, 0, 0, 0),
            ToolTip = "把這一份從合併清單裡拿掉"
        };
        SettingsUi.StyleKit.SecondaryButton(remove);
        int captured = index;
        remove.Click += (_, _) => RemoveRow(captured);
        Grid.SetColumn(remove, 4);
        grid.Children.Add(remove);

        var row = new Border
        {
            Height = RowHeightPx,
            Margin = new Thickness(0, 0, 0, RowGapPx),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 0, 10, 0),
            BorderThickness = new Thickness(1),
            // 底色／邊框照方向 D：被拖曳的那一列用既有的選取色票，其餘用透明。
            Background = Brushes.Transparent,
            BorderBrush = SettingsUi.StyleKit.CardBorder,
            Child = grid,
            Cursor = Cursors.SizeAll,
            ToolTip = "拖曳這一列可以調整順序"
        };
        if (index == _dragFrom && _dragFrom >= 0)
        {
            row.Background = SettingsUi.StyleKit.SelectedBg;
            row.BorderBrush = SettingsUi.StyleKit.SecondaryBorder;
        }

        // 真滑鼠的三個事件。它們呼叫的**就是**自測呼叫的那三個方法（見類別說明）。
        row.MouseLeftButtonDown += (_, e) => BeginDrag(e.GetPosition(_list));
        row.MouseMove += (_, e) => ContinueDrag(e.GetPosition(_list));
        row.MouseLeftButtonUp += (_, _) => EndDrag();
        return row;
    }

    // =====================================================================================
    // 順序的變更（拖曳、移除）—— 這一區是「順序語意」的實作，畫面只是它的投影
    // =====================================================================================

    /// <summary>一列的位移量（px）。＝ 列高 ＋ 列距，所以「拖過一列」剛好是移動一格。</summary>
    private static double RowPitchPx => RowHeightPx + RowGapPx;

    /// <summary>按下：記住起點。畫面上的座標（相對於清單）在拖曳期間是穩定的，因為清單本身不會捲動。</summary>
    private void BeginDrag(Point listPoint)
    {
        _dragFrom = IndexAt(listPoint);
        _dragTo = _dragFrom;
        _dragStart = listPoint;
        RebuildRows();
    }

    /// <summary>
    /// 移動：把目前的位移換算成「相對於起點的列數」。
    ///
    /// ⚠ 目標索引一律從 <see cref="_dragStart"/> 重算，**不是**累加（每次加減 1）。累加的話來回拖
    ///   同一段距離不會回到原位——而「拖回原處應該還原」正是使用者對拖曳排序最基本的期待，
    ///   也是一個很容易只有真滑鼠才發現的缺陷。重算讓它是一對一的。
    /// </summary>
    private void ContinueDrag(Point listPoint)
    {
        if (_dragFrom < 0) return;
        double delta = listPoint.Y - _dragStart.Y;
        int steps = (int)Math.Round(delta / RowPitchPx, MidpointRounding.AwayFromZero);
        int target = Math.Clamp(_dragFrom + steps, 0, _order.Count - 1);
        if (target == _dragTo) return;
        _dragTo = target;
        _log?.Invoke($"merge ui: dragging row {_dragFrom + 1} -> {target + 1} " +
                     $"(deltaY={delta:0.#}px, pitch={RowPitchPx:0.#}px)");
        RebuildRows();
    }

    /// <summary>放開：定案。**只有到這一步順序才真的改變**——拖曳中途顯示的是意圖，不是結果。</summary>
    private void EndDrag()
    {
        if (_dragFrom < 0) return;
        MoveTo(_dragFrom, _dragTo);
        _dragFrom = -1;
        _dragTo = -1;
        RebuildRows();
    }

    /// <summary>
    /// 把第 <paramref name="from"/> 列搬到第 <paramref name="to"/> 列。回傳「順序真的變了嗎」。
    /// **這是唯一一處改動順序的地方**（拖曳、移除後重畫、自測全部經過它），所以順序的合法性
    /// 只需要在一個地方維護。
    /// </summary>
    private bool MoveTo(int from, int to)
    {
        try
        {
            if (from < 0 || from >= _order.Count) return false;
            to = Math.Clamp(to, 0, _order.Count - 1);
            if (to == from) return false;

            string moved = _order[from];
            _order.RemoveAt(from);
            _order.Insert(to, moved);
            _log?.Invoke($"merge ui: order changed: {Path.GetFileName(moved)} moved from position " +
                         $"{from + 1} to {to + 1}; now=[{string.Join(",", _order.Select(Path.GetFileName))}]");
            Sanitize();
            return true;
        }
        catch (Exception ex)
        {
            // 拖曳的數學壞掉不可以殺掉 UI 執行緒（見類別說明）。
            Notice($"移動時發生未預期的錯誤（{ex.GetType().Name}）——順序沒有改變");
            _log?.Invoke($"merge ui: MoveTo threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 移除一列。**拒絕把最後一份移掉**：0 個輸入的合併不是「合併一個空檔」，而是一個沒有意義的
    /// 請求（引擎也會拒絕它）。讓使用者當場看到提示，比讓他拿到一行失敗訊息好。
    /// </summary>
    private void RemoveRow(int index)
    {
        try
        {
            if (index < 0 || index >= _order.Count) return;
            if (_order.Count <= 1)
            {
                Notice("至少要留一份檔案才能合併——最後這一份不能移除（要放棄就按 Esc 取消）");
                _log?.Invoke("merge ui: remove refused (it is the last file; a merge needs at least one)");
                return;
            }
            string removed = _order[index];
            _order.RemoveAt(index);
            Notice("");
            _log?.Invoke($"merge ui: removed {Path.GetFileName(removed)} from position {index + 1}; " +
                         $"now=[{string.Join(",", _order.Select(Path.GetFileName))}]");
            Sanitize();
            RebuildRows();
        }
        catch (Exception ex)
        {
            Notice($"移除時發生未預期的錯誤（{ex.GetType().Name}）——清單沒有改變");
            _log?.Invoke($"merge ui: RemoveRow threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>清單座標 → 列索引。<paramref name="listPoint"/> 是相對於清單面板的座標。</summary>
    private int IndexAt(Point listPoint)
    {
        if (_order.Count == 0) return -1;
        int row = (int)Math.Floor(listPoint.Y / RowPitchPx);
        return Math.Clamp(_listTop + row, 0, _order.Count - 1);
    }

    /// <summary>
    /// 分頁。它**只改變「看到哪一段」**，不改變順序——所以它不經過 <see cref="Sanitize"/>，
    /// 也不碰 <see cref="_order"/>。
    /// </summary>
    private void Page(int direction)
    {
        int maxTop = Math.Max(0, _order.Count - WindowRowsPx);
        int next = Math.Clamp(_listTop + direction * WindowRowsPx, 0, maxTop);
        if (next == _listTop) return;
        _listTop = next;
        _dragFrom = -1;
        _dragTo = -1;
        RebuildRows();
    }

    /// <summary>
    /// 順序的收斂：非空、每一項都在原始批次裡、沒有重複。
    ///
    /// **這是唯一的收斂處**（拖曳、移除、程式化設定、開窗的起始值全部經過它），所以「什麼是一份
    /// 合法的順序」只有一份規則，而且它保證不丟例外。它同時是「移除」與「拖曳」共同的防線：
    /// 任何讓清單變成空的路徑都會在這裡被擋下來。
    /// </summary>
    private void Sanitize()
    {
        // ① 去掉重複（同一份檔在同一個順序裡出現兩次沒有任何意義）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = _order.Count - 1; i >= 0; i--)
            if (!seen.Add(_order[i]))
                _order.RemoveAt(i);

        // ② 去掉不在原始批次裡的（正常路徑不會發生，但它保證清單不會被注入外來路徑）
        var allowed = new HashSet<string>(_original, StringComparer.Ordinal);
        _order.RemoveAll(p => !allowed.Contains(p));

        // ③ 非空：空了就回到「原始批次」，而不是留一個不能合併的空清單
        if (_order.Count == 0)
            foreach (string path in _original) _order.Add(path);
    }

    // =====================================================================================
    // 確認／取消
    // =====================================================================================

    /// <summary>
    /// 確認。交出去的是**目前畫面上的順序**（<see cref="Order"/>）。
    ///
    /// ⚠ 這裡再 <see cref="Sanitize"/> 一次不是多餘的：確認是唯一有副作用的一刻，而它的輸入是
    ///   「使用者剛剛拖出來的狀態」。在副作用之前把不變量再確認一次，是這個專案對「使用者輸入」
    ///   的既有作法（見 CropWindow.Confirm）。
    /// </summary>
    private void Confirm()
    {
        try
        {
            Sanitize();
            if (_order.Count == 0)
            {
                Notice("沒有任何檔案可以合併——請至少留一份，或按 Esc 取消");
                _log?.Invoke("merge ui: confirm refused (the order is empty)");
                return;
            }
            Order = _order.ToArray();
            _log?.Invoke($"merge ui: confirmed {Order.Length} file(s) in this order " +
                         $"=[{string.Join(",", Order.Select(Path.GetFileName))}]");
            Close();
        }
        catch (Exception ex)
        {
            // 連確認都不可以丟例外：留下提示、把視窗留著讓使用者能做別的事。
            Notice($"確認時發生未預期的錯誤（{ex.GetType().Name}）——沒有任何東西被送出");
            _log?.Invoke($"merge ui: Confirm threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelWithLog(string how)
    {
        try
        {
            Order = null;
            _log?.Invoke($"merge ui: cancelled ({how}) - nothing is merged and no file is written");
            Close();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"merge ui: cancel threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Enter／Esc 走**這裡**而不是按鈕的預設行為：清單裡有 Button，Space／Enter 在它們身上是
        // 「按下那顆鈕」，會把使用者的確認吃掉。Preview 在它們之前，所以這裡標記 Handled 就贏了。
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            CancelWithLog("esc key");
        }
    }

    private void Notice(string text) => _noticeText.Text = text;

    // =====================================================================================
    // 自測接縫（驗收證據用；正式路徑不經過這裡）
    //
    // 為什麼需要它們：**跑套件時絕不碰使用者的滑鼠**（本專案 2026-10-06 的新規則）。
    // 所以「拖曳排序」必須能在不產生任何真實輸入的情況下被驅動，而且驅動的必須是**同一條路徑**
    // ——下面 <see cref="SelfTestDrag"/> 呼叫的 BeginDrag／ContinueDrag／EndDrag 就是滑鼠事件
    // 呼叫的那三個方法，中間沒有任何「測試專用」的分支。真正抓不到的只有「手感」與「游標形狀」。
    // =====================================================================================

    /// <summary>自測：目前的順序（**讀的就是畫面用的那一份**，不是另一份給測試看的清單）。</summary>
    public string[] SelfTestOrder() => _order.ToArray();

    /// <summary>自測：一句話的順序快照，格式固定好讓腳本可以逐項比對：
    /// <c>order=1:a.pdf(2)|2:b.pdf(1)</c>。頁數讀不到時是 <c>?</c>，不是 0。</summary>
    public string SelfTestOrderLine()
    {
        var parts = new List<string>();
        for (int i = 0; i < _order.Count; i++)
        {
            string name = Path.GetFileName(_order[i]);
            int? count = _pageCounts.TryGetValue(_order[i], out int? c) ? c : null;
            parts.Add($"{i + 1}:{name}({(count is { } n ? n.ToString() : "?")})");
        }
        return "order=" + string.Join("|", parts);
    }

    /// <summary>
    /// 自測：一次程式化拖曳（**餵進真正的那三個方法**）。回傳「這一拖有沒有改變順序」。
    ///
    /// ⚠ 判準是**順序有沒有變**，不是「命中哪一列」——命中哪一列已經被 `fromIndex` 決定了
    ///   （起點是那一列的中心），所以再去讀那個值只會證明它自己。真正要看的是產品有沒有動。
    /// </summary>
    public bool SelfTestDrag(int fromIndex, double deltaRows)
    {
        if (fromIndex < 0 || fromIndex >= _order.Count) return false;
        // 起點取那一列的中心（不是列的上緣）：中心是使用者「抓住一列」時最自然的位置，
        // 而 IndexAt 的邊界落在列與列之間，所以從中心出發的換算最不容易踩到進位爭議。
        double y = (fromIndex - _listTop) * RowPitchPx + RowHeightPx / 2;
        string[] before = SelfTestOrder();
        BeginDrag(new Point(20, y));
        ContinueDrag(new Point(20, y + deltaRows * RowPitchPx));
        EndDrag();
        return !before.SequenceEqual(SelfTestOrder(), StringComparer.Ordinal);
    }

    /// <summary>自測：拖曳之後**回到原處**時順序必須完全還原（見 <see cref="ContinueDrag"/> 的說明）。</summary>
    public bool SelfTestDragAndReturn(int fromIndex, double deltaRows)
    {
        if (fromIndex < 0 || fromIndex >= _order.Count) return false;
        double y = (fromIndex - _listTop) * RowPitchPx + RowHeightPx / 2;
        string[] before = SelfTestOrder();
        BeginDrag(new Point(20, y));
        ContinueDrag(new Point(20, y + deltaRows * RowPitchPx));
        ContinueDrag(new Point(20, y));
        EndDrag();
        return before.SequenceEqual(SelfTestOrder(), StringComparer.Ordinal);
    }

    /// <summary>自測：按下第 <paramref name="index"/> 列上**真正的移除鈕**（不是呼叫 RemoveRow）。</summary>
    public bool SelfTestRemoveRow(int index)
    {
        int slot = index - _listTop;
        if (slot < 0 || slot >= _rows.Count) return false;
        Button? remove = FindRemoveButton(_rows[slot]);
        if (remove == null) return false;
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>自測：按下**真正的分頁鈕**。</summary>
    public bool SelfTestPage(int direction)
    {
        Button button = direction < 0 ? _upButton : _downButton;
        if (!button.IsEnabled) return false;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>自測：按下**真正的確認鈕**（與使用者按的是同一個按鈕事件）。</summary>
    public void SelfTestConfirm() => OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>自測：按下**真正的取消鈕**。</summary>
    public void SelfTestCancel() => CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>自測：目前那一行提示文字（退化輸入的處置就是顯示提示，所以它必須可被斷言）。</summary>
    public string SelfTestNotice => _noticeText.Text;

    /// <summary>自測：畫面上畫出來的列數與編號（證明「畫面」與 <see cref="SelfTestOrder"/> 一致）。</summary>
    public string SelfTestRowsLine()
    {
        var parts = new List<string>();
        for (int i = 0; i < _rows.Count; i++)
            parts.Add($"{_listTop + i + 1}");
        return $"rows={_rows.Count} slots=[{string.Join(",", parts)}] " +
               $"top={_listTop + 1} window={WindowRowsPx}";
    }

    /// <summary>
    /// 自測：一句話的狀態快照（形狀比照 <see cref="CropWindow.SelfTestState"/>）。
    /// 它刻意把**判準**放進來（<c>scroll</c>、<c>orderIsUnique</c>、<c>orderIsSubset</c>），
    /// 而不是只放數字：驗收自己在腳本裡重算那些條件就是第二份規則。
    /// </summary>
    public string SelfTestState()
    {
        System.Windows.Interop.HwndSource? source = PresentationSource.FromVisual(this)
            as System.Windows.Interop.HwndSource;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool unique = true;
        foreach (string p in _order) if (!seen.Add(p)) unique = false;
        var allowed = new HashSet<string>(_original, StringComparer.Ordinal);
        bool subset = _order.All(allowed.Contains);

        return $"loaded={IsLoaded} visible={IsVisible} " +
               $"hwnd={(source != null ? "0x" + source.Handle.ToInt64().ToString("X") : "(none)")} " +
               $"files={_order.Count} original={_original.Length} " +
               $"confirmed={(Order != null)} unique={unique} subset={subset} " +
               $"rows={_rows.Count} scroll={ScrollableExtentCount()} " +
               $"notice='{_noticeText.Text}' {SelfTestOrderLine()}";
    }

    /// <summary>
    /// 可捲動範圍 &gt; 0 的 <see cref="ScrollViewer"/> 個數。應該是 0——理由與
    /// <see cref="CropWindow"/> 完全相同（捲軸把內容切掉這件事，只有可斷言的數字看得出來）。
    /// </summary>
    private int ScrollableExtentCount()
    {
        int count = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            if (node is ScrollViewer viewer &&
                (viewer.ScrollableHeight > 0 || viewer.ScrollableWidth > 0))
                count++;
            int children = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < children; i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        return count;
    }

    /// <summary>在視覺樹裡找那一列上的移除鈕（自測要按的是真的按鈕，所以必須找得到它）。</summary>
    private static Button? FindRemoveButton(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            if (node is Button button &&
                button.Content is string text &&
                string.Equals(text, "移除", StringComparison.Ordinal))
                return button;
            int children = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < children; i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    /// <summary>
    /// 把這張視窗畫成一張 PNG（**離屏合成**，不是螢幕擷取）。與 <see cref="CropWindow.TryDump"/>
    /// 同一條路徑、同一個「不是空白圖」的檢查：空白圖在「有沒有捲軸」這種題目上會給出假通過。
    /// </summary>
    public bool TryDump(string path, out string detail)
    {
        detail = "";
        try
        {
            UpdateLayout();
            int pw = (int)Math.Ceiling(ActualWidth);
            int ph = (int)Math.Ceiling(ActualHeight);
            if (pw <= 0 || ph <= 0)
            {
                detail = $"the window has no size ({ActualWidth}x{ActualHeight})";
                return false;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(pw * dpi.DpiScaleX),
                                             (int)Math.Ceiling(ph * dpi.DpiScaleY),
                                             96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            byte[] bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                detail = "the PNG encoder produced 0 bytes";
                return false;
            }

            int distinct = SettingsUi.CountDistinctColors(bytes);
            if (distinct < 2)
            {
                detail = $"the captured frame is blank ({distinct} distinct colour(s) in {pw}x{ph})";
                return false;
            }

            File.WriteAllBytes(path, bytes);
            detail = $"{pw}x{ph} @{dpi.DpiScaleX:0.##}x, {bytes.Length} bytes, {distinct} distinct colours";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
