using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

// 這個專案同時開了 UseWPF 與 UseWindowsForms，同名型別會「模稜兩可」（見 SettingsUi.cs 的說明）。
// 裁切視窗要的一律是 WPF 那一組，所以逐項釘死，避免哪天漏打全名而悄悄換成 WinForms 的型別。
// 這裡刻意**只**引入明確無歧義的命名空間，其餘靠這些別名。
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using RectangleShape = System.Windows.Shapes.Rectangle;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Sidecar;

/// <summary>
/// 獨立裁切視窗（使用者 2026-10-06 拍板：裁切的互動**不在輪盤上**）。
///
/// 它是一個純 WPF 視窗、**沒有 XAML**：整個畫面在程式裡組出來，顏色全部來自
/// <see cref="SettingsUi.StyleKit"/>（＝<see cref="WheelDesign"/> 的既有權杖），
/// 所以「配色只有一個權威」與「XAML 內 0 個硬編碼色」這兩條紀律都不必另外維護一份。
///
/// ## 這個視窗裡的三個「座標系」
///   ① **畫面座標**（stage，<see cref="StageWidthPx"/>×<see cref="StageHeightPx"/> 的固定畫布）；
///   ② **影像像素座標**（預覽那張圖自己的像素）；
///   ③ **正規化座標**（0..1，唯一會離開這個視窗的東西）。
///   ①↔② 由 <see cref="_scale"/>／<see cref="_imageLeft"/> 這組顯示對應換算；②↔③ 一律走
///   <see cref="CropMath"/>（**不在這裡自己乘除**，否則批次的換算就有兩份）。
///   拖曳的數學全部寫在②上（在螢幕上拖 20px 就該是 20px 的位移，與圖多大無關），
///   按下「確認」時才轉成③。
///
/// ## 不准丟例外
///   這個專案剛為「事件處理常式丟例外殺掉 UI 執行緒」付出過代價（InvariantGlobalization 那次）。
///   所以：每一個輸入（拖曳、比例切換、程式化設定矩形、確認）都經過 <see cref="Sanitize"/>，
///   它**保證** <see cref="_rect"/> 永遠在影像內、且寬高 ≥ 1 px；退化輸入只會顯示提示，
///   不會丟出去。視窗的建構與顯示失敗由 <see cref="CropUi"/> 那個 catch 接手。
/// </summary>
internal sealed class CropWindow : Window
{
    /// <summary>預覽畫布大小（固定）。固定是為了讓離屏 dump 逐次可比——它不隨視窗大小變動。</summary>
    public const int StageWidthPx = 860;
    public const int StageHeightPx = 520;

    /// <summary>螢幕上的把手判定半徑（px）。9px 是「抓得到、又不會蓋掉半張圖」的量級。</summary>
    private const double HandleHitPx = 9;

    private readonly BitmapSource _frame;
    private readonly int _imageWidth;
    private readonly int _imageHeight;
    private readonly int _batchCount;
    private readonly string _fileName;
    private readonly Action<string>? _log;

    private readonly Canvas _stage = new();
    private readonly Canvas _overlay = new();
    private readonly Image _image = new();
    private readonly TextBlock _infoText = new();
    private readonly TextBlock _rectText = new();
    private readonly TextBlock _noticeText = new();
    private readonly Dictionary<string, ToggleButton> _ratioButtons = new();
    private readonly List<RectangleShape> _shade = new();
    private RectangleShape _selectionBorder = new();
    private readonly List<RectangleShape> _handles = new();

    // 顯示對應（①↔②）
    private double _scale = 1;
    private double _imageLeft, _imageTop, _displayWidth, _displayHeight;

    /// <summary>目前的裁切矩形，**影像像素座標**。任何時刻都滿足 <see cref="Sanitize"/> 的保證。</summary>
    private Int32Rect _rect;
    private string _ratioId = CropRatios.DefaultId;

    // 拖曳狀態
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartImage;
    private Int32Rect _dragStartRect;

    /// <summary>使用者按下「確認」時的結果（正規化）。null＝還沒確認。</summary>
    public CropSpec? Result { get; private set; }

    /// <summary>這張視窗正在顯示的影像尺寸（自測斷言用；也是換算的輸入）。</summary>
    public int ImageWidth => _imageWidth;
    public int ImageHeight => _imageHeight;

    /// <summary>目前的比例預設 id。</summary>
    public string RatioId => _ratioId;

    /// <summary>目前的矩形（影像像素）。</summary>
    public Int32Rect RectInPixels => _rect;

    /// <summary>確認鈕／取消鈕（自測要按的是**真的按鈕**，不是另一條內部路徑）。</summary>
    public Button OkButton { get; }
    public Button CancelButton { get; }

    private enum DragMode
    {
        None, New, Move,
        Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight
    }

    public CropWindow(string fileName, int batchCount, BitmapSource frame, string ratioId,
                      Action<string>? log)
    {
        _fileName = fileName;
        _batchCount = batchCount;
        _frame = frame;
        _imageWidth = frame.PixelWidth;
        _imageHeight = frame.PixelHeight;
        _log = log;
        _ratioId = CropRatios.CanonicalId(ratioId);

        Title = ProductInfo.Name;
        Width = 940;
        Height = 810;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SettingsUi.StyleKit.WindowBg;
        Foreground = SettingsUi.StyleKit.TextMain;
        FontFamily = SettingsUi.StyleKit.Font;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 標題
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 預覽
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 比例
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 提示
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按鈕
        Content = root;

        BuildHeader(root);
        BuildStage(root);
        BuildRatioRow(root);
        BuildNotice(root);

        OkButton = new Button { Content = "確認 (Enter)", Width = 128, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
        CancelButton = new Button { Content = "取消 (Esc)", Width = 128, Height = 32 };
        SettingsUi.StyleKit.PrimaryButton(OkButton);
        SettingsUi.StyleKit.SecondaryButton(CancelButton);
        OkButton.Click += (_, _) => Confirm();
        CancelButton.Click += (_, _) => CancelWithLog("cancel button");

        var footer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        footer.Children.Add(OkButton);
        footer.Children.Add(CancelButton);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        // 起始矩形：**全圖**（使用者拖之前先看得到整張圖），再由比例預設縮進去。
        // 預設是「自由」⇒ 一開始就是全圖；使用者一拖就蓋掉它。刻意不預設「中央 1:1」：
        // 那會在沒有人拖的情況下先幫使用者做一個他沒做的決定。
        _rect = new Int32Rect(0, 0, Math.Max(1, _imageWidth), Math.Max(1, _imageHeight));
        SyncRatioButtons();
        Sanitize();
        UpdateVisuals();

        _log?.Invoke($"crop ui: window created for {_fileName} ({_imageWidth}x{_imageHeight}, " +
                     $"batch={_batchCount}, ratio={_ratioId}, stage={StageWidthPx}x{StageHeightPx})");
    }

    // =====================================================================================
    // 畫面
    // =====================================================================================

    private void BuildHeader(Grid root)
    {
        var header = new StackPanel();
        var title = new TextBlock { Text = "裁切", FontSize = 21, FontWeight = FontWeights.SemiBold };
        title.Foreground = SettingsUi.StyleKit.TextMain;
        var subtitle = new TextBlock
        {
            Text = "在圖上拖一個矩形（可移動、可拉四邊與四角）· Enter 確認 · Esc 取消",
            FontSize = 12, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap
        };
        subtitle.Foreground = SettingsUi.StyleKit.TextDim;
        header.Children.Add(title);
        header.Children.Add(subtitle);
        Grid.SetRow(header, 0);
        root.Children.Add(header);
    }

    private void BuildStage(Grid root)
    {
        var border = new Border
        {
            Width = StageWidthPx, Height = StageHeightPx,
            Background = SettingsUi.StyleKit.SurfaceBg,
            BorderBrush = SettingsUi.StyleKit.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipToBounds = true
        };

        _stage.Width = StageWidthPx;
        _stage.Height = StageHeightPx;
        _stage.Background = SettingsUi.StyleKit.SurfaceBg;
        _stage.ClipToBounds = true;

        // 顯示對應：把影像等比縮進畫布（置中）。這一組數字是①②兩個座標系之間唯一的橋。
        // ⚠ 顯示尺寸**不可以**寫回 _imageWidth／_imageHeight：那一組是影像自己的像素數，
        // 換算矩形、比例、正規化全部靠它；兩者混用會讓「在縮圖上拖 20px」變成「裁掉 20 個像素」。
        _scale = Math.Min((double)StageWidthPx / Math.Max(1, _imageWidth),
                          (double)StageHeightPx / Math.Max(1, _imageHeight));
        if (_scale <= 0 || double.IsNaN(_scale) || double.IsInfinity(_scale)) _scale = 1;
        // 顯示尺寸取整到整數 px：滑鼠座標的換算才不會出現半像素的漂移。
        _displayWidth = Math.Max(1, Math.Round(_imageWidth * _scale, MidpointRounding.AwayFromZero));
        _displayHeight = Math.Max(1, Math.Round(_imageHeight * _scale, MidpointRounding.AwayFromZero));
        _imageLeft = Math.Floor((StageWidthPx - _displayWidth) / 2);
        _imageTop = Math.Floor((StageHeightPx - _displayHeight) / 2);

        _image.Source = _frame;
        _image.Width = _displayWidth;
        _image.Height = _displayHeight;
        Canvas.SetLeft(_image, _imageLeft);
        Canvas.SetTop(_image, _imageTop);
        // 放大時用最近鄰（看得見真正的像素），縮小時用高品質（看得清楚構圖）。
        RenderOptions.SetBitmapScalingMode(_image,
            _displayWidth >= _frame.PixelWidth ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _stage.Children.Add(_image);

        // 遮罩：選取框**以外**的四塊。用它而不是半透明填色，是因為四個矩形的位置可以逐次更新，
        // 而「一整塊挖洞」需要 PathGeometry／CombinedGeometry（多一個要維護的幾何）。
        for (int i = 0; i < 4; i++)
        {
            var shade = new RectangleShape { Fill = SettingsUi.StyleKit.CropMask, IsHitTestVisible = false };
            _shade.Add(shade);
            _overlay.Children.Add(shade);
        }

        _selectionBorder = new RectangleShape
        {
            Stroke = SettingsUi.StyleKit.SelectionStroke,
            StrokeThickness = 2,
            Fill = null,
            IsHitTestVisible = false
        };
        _overlay.Children.Add(_selectionBorder);

        // 8 個把手：4 角＋4 邊中點。畫出來的尺寸與判定半徑是兩件事（判定半徑見 HandleHitPx）：
        // 畫太大會蓋住影像，抓不到又會讓「拉邊」變成不可能。
        for (int i = 0; i < 8; i++)
        {
            var handle = new RectangleShape
            {
                Fill = SettingsUi.StyleKit.HandleFill,
                Stroke = SettingsUi.StyleKit.SelectionStroke,
                StrokeThickness = 1,
                Width = 9, Height = 9,
                IsHitTestVisible = false
            };
            _handles.Add(handle);
            _overlay.Children.Add(handle);
        }

        _overlay.Width = StageWidthPx;
        _overlay.Height = StageHeightPx;
        _overlay.Background = System.Windows.Media.Brushes.Transparent;   // 透明但**可命中**：整個畫布都收得到拖曳
        _overlay.Cursor = Cursors.Cross;
        _stage.Children.Add(_overlay);

        _overlay.MouseLeftButtonDown += OnStageMouseDown;
        _overlay.MouseMove += OnStageMouseMove;
        _overlay.MouseLeftButtonUp += OnStageMouseUp;

        border.Child = _stage;
        Grid.SetRow(border, 1);
        root.Children.Add(border);

        // 資訊列：檔名／影像尺寸／目前矩形的像素值與正規化值。使用者與驗收都靠它讀出「現在框的是什麼」。
        var info = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _infoText.FontSize = 12;
        _infoText.Foreground = SettingsUi.StyleKit.TextFaint;
        _rectText.FontSize = 12;
        _rectText.Margin = new Thickness(16, 0, 0, 0);
        _rectText.Foreground = SettingsUi.StyleKit.TextMain;
        info.Children.Add(_infoText);
        info.Children.Add(_rectText);
        Grid.SetRow(info, 2);
        root.Children.Add(info);
        // 比例鈕那一列（Grid row 3）由 BuildRatioRow 放進來；這裡先把 row 2 讓給資訊列，
        // 所以比例列掛在 row 2 的**下面**——見 BuildRatioRow。
    }

    private void BuildRatioRow(Grid root)
    {
        // row 2 已經是資訊列，比例列掛在同一個 row 的下方（用 StackPanel 疊起來會讓 Grid 的列數
        // 與順序變得容易搞錯；這裡改成把比例列塞進 row 2 的 StackPanel 更動一行都不必，
        // 直接放到 row 3、資訊列留在 row 2）。
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var label = new TextBlock
        {
            Text = "比例", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        label.Foreground = SettingsUi.StyleKit.TextFaint;
        row.Children.Add(label);

        foreach (CropRatioPreset preset in CropRatios.Presets)
        {
            var button = new ToggleButton
            {
                Content = preset.Label,
                Width = 76, Height = 28, Margin = new Thickness(0, 0, 8, 0),
                Background = SettingsUi.StyleKit.SurfaceBg,
                Foreground = SettingsUi.StyleKit.TextMain,
                BorderBrush = SettingsUi.StyleKit.CardBorder,
                FontFamily = SettingsUi.StyleKit.Font,
                Template = SettingsUi.StyleKit.ToggleTemplate(),
                Cursor = Cursors.Hand
            };
            string id = preset.Id;
            button.Click += (_, _) => SelectRatio(id);
            _ratioButtons[id] = button;
            row.Children.Add(button);
        }

        var hint = new TextBlock
        {
            Text = "（同一比例會套用到這一批的每一張圖，每一張依自己的尺寸換算）",
            FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        hint.Foreground = SettingsUi.StyleKit.TextFaint;
        row.Children.Add(hint);

        Grid.SetRow(row, 3);
        root.Children.Add(row);
    }

    private void BuildNotice(Grid root)
    {
        _noticeText.FontSize = 12;
        _noticeText.Margin = new Thickness(0, 8, 0, 0);
        _noticeText.TextWrapping = TextWrapping.Wrap;
        _noticeText.Foreground = SettingsUi.StyleKit.WarningFg;
        _noticeText.Text = "";
        Grid.SetRow(_noticeText, 4);
        root.Children.Add(_noticeText);
    }

    /// <summary>把目前的狀態畫到畫面上。**唯一一處更新視覺**，任何狀態變更之後都要叫它。</summary>
    private void UpdateVisuals()
    {
        double left = _imageLeft + _rect.X * _scale;
        double top = _imageTop + _rect.Y * _scale;
        double width = _rect.Width * _scale;
        double height = _rect.Height * _scale;
        // 至少畫得出 1px：極小的選取（1 影像 px）在縮圖上可能不到 1 螢幕 px，線會整個消失。
        if (width < 2) width = 2;
        if (height < 2) height = 2;

        // 四塊遮罩（上／下／左／右）
        Set(_shade[0], 0, 0, StageWidthPx, top);
        Set(_shade[1], 0, top + height, StageWidthPx, Math.Max(0, StageHeightPx - (top + height)));
        Set(_shade[2], 0, top, left, height);
        Set(_shade[3], left + width, top, Math.Max(0, StageWidthPx - (left + width)), height);

        Canvas.SetLeft(_selectionBorder, left);
        Canvas.SetTop(_selectionBorder, top);
        _selectionBorder.Width = width;
        _selectionBorder.Height = height;

        // 8 個把手：0..3 角（左上／右上／左下／右下）、4..7 邊（上／下／左／右）
        SetHandle(0, left, top);
        SetHandle(1, left + width, top);
        SetHandle(2, left, top + height);
        SetHandle(3, left + width, top + height);
        SetHandle(4, left + width / 2, top);
        SetHandle(5, left + width / 2, top + height);
        SetHandle(6, left, top + height / 2);
        SetHandle(7, left + width, top + height / 2);

        CropSpec normalized = CropMath.ToNormalized(_rect, _imageWidth, _imageHeight, _ratioId);
        _infoText.Text = $"{_fileName}　{_imageWidth}×{_imageHeight} px" +
                         (_batchCount > 1 ? $"　（這一批共 {_batchCount} 張，每一張依自己的尺寸換算）" : "");
        _rectText.Text = $"選取 {_rect.Width}×{_rect.Height} px　正規化 " +
                         $"{normalized.X.ToString("0.####", CultureInfo.InvariantCulture)}," +
                         $"{normalized.Y.ToString("0.####", CultureInfo.InvariantCulture)}," +
                         $"{normalized.Width.ToString("0.####", CultureInfo.InvariantCulture)}," +
                         $"{normalized.Height.ToString("0.####", CultureInfo.InvariantCulture)}";
        SyncRatioButtons();
    }

    private static void Set(RectangleShape rectangle, double x, double y, double w, double h)
    {
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        rectangle.Width = Math.Max(0, w);
        rectangle.Height = Math.Max(0, h);
    }

    private void SetHandle(int index, double centreX, double centreY)
    {
        RectangleShape handle = _handles[index];
        Canvas.SetLeft(handle, centreX - handle.Width / 2);
        Canvas.SetTop(handle, centreY - handle.Height / 2);
    }

    private void SyncRatioButtons()
    {
        foreach ((string id, ToggleButton button) in _ratioButtons)
            SettingsUi.StyleKit.SetToggleState(button, string.Equals(id, _ratioId, StringComparison.Ordinal));
    }

    /// <summary>畫面座標 → 影像像素座標（①→②）。超出畫布一律夾進影像內。</summary>
    private Point ToImage(Point stagePoint) => new(
        Math.Clamp((stagePoint.X - _imageLeft) / _scale, 0, _imageWidth),
        Math.Clamp((stagePoint.Y - _imageTop) / _scale, 0, _imageHeight));

    // =====================================================================================
    // 互動
    // =====================================================================================

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Point p = e.GetPosition(_overlay);
            BeginDrag(p, out string note);
            if (note.Length > 0) Notice(note);
            _overlay.CaptureMouse();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            // 事件處理常式**不准把例外丟出去**：那會穿過 WPF 的 dispatcher frame，
            // 而這個專案已經為同一件事付過代價。記下來、繼續活著。
            Notice($"拖曳開始時發生未預期的錯誤（{ex.GetType().Name}）——已忽略，視窗仍可使用");
            _log?.Invoke($"crop ui: mouse down threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        try
        {
            if (_dragMode == DragMode.None)
            {
                _stage.Cursor = CursorFor(ToImage(e.GetPosition(_overlay)));
                return;
            }
            ContinueDrag(e.GetPosition(_overlay));
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _dragMode = DragMode.None;
            Notice($"拖曳時發生未預期的錯誤（{ex.GetType().Name}）——已停止這次拖曳，視窗仍可使用");
            _log?.Invoke($"crop ui: mouse move threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            EndDrag();
            _overlay.ReleaseMouseCapture();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _dragMode = DragMode.None;
            _log?.Invoke($"crop ui: mouse up threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 開始一次拖曳。<paramref name="stagePoint"/> 是**畫面座標**。
    /// 命中判定：先看有沒有抓到把手／邊（＝調整大小），再看是不是在矩形內（＝搬移），
    /// 其餘＝重新畫一個。
    /// </summary>
    private void BeginDrag(Point stagePoint, out string note)
    {
        note = "";
        Point image = ToImage(stagePoint);
        double left = _imageLeft + _rect.X * _scale;
        double top = _imageTop + _rect.Y * _scale;
        double right = left + _rect.Width * _scale;
        double bottom = top + _rect.Height * _scale;
        double x = stagePoint.X, y = stagePoint.Y;

        bool nearLeft = Math.Abs(x - left) <= HandleHitPx;
        bool nearRight = Math.Abs(x - right) <= HandleHitPx;
        bool nearTop = Math.Abs(y - top) <= HandleHitPx;
        bool nearBottom = Math.Abs(y - bottom) <= HandleHitPx;
        bool insideX = x >= left - HandleHitPx && x <= right + HandleHitPx;
        bool insideY = y >= top - HandleHitPx && y <= bottom + HandleHitPx;

        _dragMode =
            nearLeft && nearTop ? DragMode.TopLeft :
            nearRight && nearTop ? DragMode.TopRight :
            nearLeft && nearBottom ? DragMode.BottomLeft :
            nearRight && nearBottom ? DragMode.BottomRight :
            nearLeft && insideY ? DragMode.Left :
            nearRight && insideY ? DragMode.Right :
            nearTop && insideX ? DragMode.Top :
            nearBottom && insideX ? DragMode.Bottom :
            (insideX && insideY && _rect.Width > 2 && _rect.Height > 2) ? DragMode.Move :
            DragMode.New;

        _dragStartImage = image;
        _dragStartRect = _rect;

        if (_dragMode == DragMode.New)
        {
            // 新畫一個：起點就是錨點（1 影像 px 起跳，之後由 ContinueDrag 撐開）
            _rect = new Int32Rect(
                Math.Clamp((int)Math.Round(image.X), 0, _imageWidth - 1),
                Math.Clamp((int)Math.Round(image.Y), 0, _imageHeight - 1), 1, 1);
            _rect = Constrain(_rect, DragMode.New);
            Notice("");
        }
        UpdateVisuals();
    }

    /// <summary>拖曳中。<paramref name="stagePoint"/> 是**畫面座標**。</summary>
    private void ContinueDrag(Point stagePoint)
    {
        Point image = ToImage(stagePoint);
        int dx = (int)Math.Round(image.X - _dragStartImage.X);
        int dy = (int)Math.Round(image.Y - _dragStartImage.Y);
        Int32Rect r = _dragStartRect;

        switch (_dragMode)
        {
            case DragMode.Move:
                r.X = Math.Clamp(r.X + dx, 0, Math.Max(0, _imageWidth - r.Width));
                r.Y = Math.Clamp(r.Y + dy, 0, Math.Max(0, _imageHeight - r.Height));
                break;

            case DragMode.New:
            case DragMode.Left:
            case DragMode.Right:
            case DragMode.Top:
            case DragMode.Bottom:
            case DragMode.TopLeft:
            case DragMode.TopRight:
            case DragMode.BottomLeft:
            case DragMode.BottomRight:
                int left = r.X, top = r.Y, right = r.X + r.Width, bottom = r.Y + r.Height;
                if (_dragMode is DragMode.New)
                {
                    // 新畫：起點固定，另一個角跟著滑鼠走（方向由 dx／dy 的正負決定）
                    left = Math.Min(_dragStartRect.X, _dragStartRect.X + dx);
                    top = Math.Min(_dragStartRect.Y, _dragStartRect.Y + dy);
                    right = Math.Max(_dragStartRect.X, _dragStartRect.X + dx);
                    bottom = Math.Max(_dragStartRect.Y, _dragStartRect.Y + dy);
                }
                else
                {
                    // 每一條邊各自夾進 [0, 對邊 ∓ 1]：**先夾再組矩形**，所以寬高永遠 ≥ 1，
                    // 拉到 0 寬、拉出邊界這兩件事都在這裡被吸收掉，不會往下傳。
                    if (_dragMode is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft)
                        left = Math.Clamp(r.X + dx, 0, r.X + r.Width - 1);
                    if (_dragMode is DragMode.Right or DragMode.TopRight or DragMode.BottomRight)
                        right = Math.Clamp(r.X + r.Width + dx, r.X + 1, _imageWidth);
                    if (_dragMode is DragMode.Top or DragMode.TopLeft or DragMode.TopRight)
                        top = Math.Clamp(r.Y + dy, 0, r.Y + r.Height - 1);
                    if (_dragMode is DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight)
                        bottom = Math.Clamp(r.Y + r.Height + dy, r.Y + 1, _imageHeight);
                }
                int l = Math.Clamp(Math.Min(left, right - 1), 0, Math.Max(0, _imageWidth - 1));
                int t = Math.Clamp(Math.Min(top, bottom - 1), 0, Math.Max(0, _imageHeight - 1));
                int w = Math.Clamp(right - left, 1, _imageWidth - l);
                int h = Math.Clamp(bottom - top, 1, _imageHeight - t);
                r = new Int32Rect(l, t, w, h);
                break;
        }

        _rect = Constrain(r, _dragMode);
        UpdateVisuals();
    }

    private void EndDrag()
    {
        _dragMode = DragMode.None;
        Sanitize();
        UpdateVisuals();
    }

    private System.Windows.Input.Cursor CursorFor(Point imagePoint)
    {
        double left = _imageLeft + _rect.X * _scale;
        double top = _imageTop + _rect.Y * _scale;
        double right = left + _rect.Width * _scale;
        double bottom = top + _rect.Height * _scale;
        Point stagePoint = new(_imageLeft + imagePoint.X * _scale, _imageTop + imagePoint.Y * _scale);
        bool nearLeft = Math.Abs(stagePoint.X - left) <= HandleHitPx;
        bool nearRight = Math.Abs(stagePoint.X - right) <= HandleHitPx;
        bool nearTop = Math.Abs(stagePoint.Y - top) <= HandleHitPx;
        bool nearBottom = Math.Abs(stagePoint.Y - bottom) <= HandleHitPx;
        if ((nearLeft && nearTop) || (nearRight && nearBottom)) return Cursors.SizeNWSE;
        if ((nearRight && nearTop) || (nearLeft && nearBottom)) return Cursors.SizeNESW;
        if (nearLeft || nearRight) return Cursors.SizeWE;
        if (nearTop || nearBottom) return Cursors.SizeNS;
        return Cursors.Cross;
    }

    /// <summary>
    /// 把矩形收斂成合法的一組：①夾進影像、②寬高 ≥ 1 px、③套用比例預設。
    /// **這是唯一的收斂處**——拖曳、比例切換、程式化設定、開窗的起始值全部經過它，
    /// 所以「什麼是合法的矩形」只有一份規則，而且它保證不丟例外。
    /// </summary>
    private Int32Rect Sanitize() => _rect = Constrain(_rect, DragMode.Move);

    private Int32Rect Constrain(Int32Rect box, DragMode mode)
    {
        int imageW = Math.Max(1, _imageWidth), imageH = Math.Max(1, _imageHeight);

        // ① 夾進影像（寬高各自先夾到 ≥ 1，再夾位置）
        int w = Math.Clamp(box.Width, 1, imageW);
        int h = Math.Clamp(box.Height, 1, imageH);
        int x = Math.Clamp(box.X, 0, imageW - w);
        int y = Math.Clamp(box.Y, 0, imageH - h);

        // ② 比例預設：在框內取「與目標比例相符、面積最大、依錨點定位」的那一個。
        if (CropRatios.TargetRatio(_ratioId, imageW, imageH) is { } ratio && ratio > 0)
        {
            int fitW, fitH;
            if (w >= h * ratio) { fitH = h; fitW = (int)Math.Round(h * ratio, MidpointRounding.AwayFromZero); }
            else { fitW = w; fitH = (int)Math.Round(w / ratio, MidpointRounding.AwayFromZero); }
            fitW = Math.Clamp(fitW, 1, imageW);
            fitH = Math.Clamp(fitH, 1, imageH);

            int anchorLeft = mode switch
            {
                DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft => x + w - fitW,
                DragMode.Right or DragMode.TopRight or DragMode.BottomRight => x,
                _ => x + (w - fitW) / 2
            };
            int anchorTop = mode switch
            {
                DragMode.Top or DragMode.TopLeft or DragMode.TopRight => y + h - fitH,
                DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight => y,
                _ => y + (h - fitH) / 2
            };
            x = Math.Clamp(anchorLeft, 0, imageW - fitW);
            y = Math.Clamp(anchorTop, 0, imageH - fitH);
            w = fitW;
            h = fitH;
        }

        return new Int32Rect(x, y, w, h);
    }

    /// <summary>切換比例預設：保留矩形的**中心**，並讓它當場符合新的比例（退化時只顯示提示）。</summary>
    private void SelectRatio(string id)
    {
        try
        {
            _ratioId = CropRatios.CanonicalId(id);
            Int32Rect before = _rect;
            _rect = Constrain(_rect, DragMode.Move);
            CropSpec normalized = CropMath.ToNormalized(_rect, _imageWidth, _imageHeight, _ratioId);
            Notice($"比例＝{CropRatios.LabelOf(_ratioId)}" +
                   (before.Width != _rect.Width || before.Height != _rect.Height
                       ? $"：矩形由 {before.Width}×{before.Height} 調整為 {_rect.Width}×{_rect.Height} px"
                       : "：矩形不變"));
            _log?.Invoke($"crop ui: ratio -> {_ratioId} " +
                         $"rect={_rect.X},{_rect.Y},{_rect.Width},{_rect.Height} {normalized.Describe()}");
            UpdateVisuals();
        }
        catch (Exception ex)
        {
            Notice($"切換比例時發生未預期的錯誤（{ex.GetType().Name}）——已忽略");
            _log?.Invoke($"crop ui: SelectRatio threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Notice(string text) => _noticeText.Text = text;

    // =====================================================================================
    // 確認／取消
    // =====================================================================================

    /// <summary>
    /// 確認。**在這個視窗就先擋掉「等於整張圖」**：引擎也會拒絕它（<see cref="ConversionError.CropNothingToDo"/>），
    /// 但讓使用者當場看到提示比讓他拿到一列失敗訊息好；而且這一條與引擎用的是**同一個判準**
    /// （矩形是否覆蓋整張圖），不是兩套規則。
    /// </summary>
    private void Confirm()
    {
        try
        {
            Sanitize();
            if (_rect.Width >= _imageWidth && _rect.Height >= _imageHeight)
            {
                Notice("選取範圍等於整張圖，沒有東西可以裁——請拖出一個比較小的矩形（或按 Esc 取消）");
                _log?.Invoke($"crop ui: confirm refused (the rectangle covers the whole " +
                             $"{_imageWidth}x{_imageHeight} image)");
                return;
            }

            Result = CropMath.ToNormalized(_rect, _imageWidth, _imageHeight, _ratioId);
            _log?.Invoke($"crop ui: confirmed {Result.Describe()} " +
                         $"pixels={_rect.X},{_rect.Y},{_rect.Width},{_rect.Height} on {_imageWidth}x{_imageHeight}");
            Close();
        }
        catch (Exception ex)
        {
            // 連確認都不可以丟例外：留下提示、把視窗留著讓使用者能做別的事。
            Notice($"確認時發生未預期的錯誤（{ex.GetType().Name}）——沒有任何東西被送出");
            _log?.Invoke($"crop ui: Confirm threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelWithLog(string how)
    {
        try
        {
            Result = null;
            _log?.Invoke($"crop ui: cancelled ({how})");
            Close();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"crop ui: cancel threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Enter／Esc 走**這裡**而不是按鈕的預設行為：比例鈕是 ToggleButton，Space／Enter 在它身上
        // 是「切換」，會把使用者的確認吃掉。Preview 在它們之前，所以這裡標記 Handled 就贏了。
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

    // =====================================================================================
    // 自測接縫（驗收證據用；正式路徑不經過這裡）
    //
    // 為什麼需要它們：**跑套件時絕不碰使用者的滑鼠**（本專案 2026-10-06 的新規則）。
    // 所以「拖曳」必須能在不產生任何真實輸入的情況下被驅動，而且驅動的必須是**同一條路徑**
    // ——這裡呼叫的 BeginDrag／ContinueDrag／EndDrag 就是滑鼠事件呼叫的那三個方法，
    // 中間沒有任何「測試專用」的分支。真正抓不到的只有「游標形狀」與「手感」。
    // =====================================================================================

    /// <summary>
    /// 程式化設定矩形（**正規化**座標）。走的是與拖曳同一條換算（<see cref="CropMath.TryToPixels"/>
    /// 的邊界取整 ＋ <see cref="Constrain"/>），不是另一套「測試用」的數學。
    ///
    /// ⚠ 這裡**不**拒絕「等於整張圖」：使用者真的可以把整張框起來，然後按確認——那正是
    /// <see cref="Confirm"/> 要當場擋下來的那一格。若自測在這裡就拒絕，那個「拒絕」會變成
    /// 自測自己拒絕自己，而<strong>確認鈕到底有沒有把關</strong>就永遠沒被驗到。
    /// 空的矩形（轉成像素後寬或高 ≤ 0）則是真的設定不進去，回傳 false。
    /// </summary>
    public bool SelfTestSetRect(double x, double y, double w, double h)
    {
        CropOutcome outcome = CropMath.TryToPixels(new CropSpec(x, y, w, h, _ratioId),
                                                   _imageWidth, _imageHeight,
                                                   out Int32Rect rect, out List<string> notes);
        if (outcome == CropOutcome.EmptyRect)
        {
            Notice($"自測：矩形 {x},{y},{w},{h} 在這個尺寸上是空的");
            return false;
        }
        // ⚠ <see cref="CropOutcome.FullFrame"/> 的時候，TryToPixels **不會**填 rect（那條路是「沒東西可裁」，
        // 不是「裁成這個矩形」）。當成 0 去用的話會變成一個 1×1 的矩形，然後確認鈕就會照著裁下去——
        // 也就是說，這裡若不小心，「整張圖被確認鈕擋下來」那一格會變成「偷偷裁了 1×1 還說通過」。
        // 使用者真的畫得出的那個矩形就是整張圖本身：明講。
        Int32Rect target = outcome == CropOutcome.FullFrame
            ? new Int32Rect(0, 0, _imageWidth, _imageHeight)
            : new Int32Rect(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height));
        _rect = Constrain(target, DragMode.Move);
        Notice("");
        UpdateVisuals();
        return true;
    }

    /// <summary>
    /// 一次程式化拖曳的**型別化**結果（自測要比對的是數字，不是 <c>trace</c> 那個給人看的字串——
    /// 用字串比對會讓「提示文字換了一行」之類的無關變動也算成「矩形變了」）。
    /// </summary>
    /// <param name="Mode">BeginDrag 當場命中的模式（<c>Move</c>／<c>Right</c>／<c>BottomRight</c>…）。
    ///   「拖曳有沒有作用」與「命中的是哪一條路徑」是兩件事，所以它必須是可斷言的值。</param>
    /// <param name="Before">BeginDrag **之前**的矩形。</param>
    /// <param name="AfterBegin">BeginDrag **之後**的矩形（重新畫的那條路徑在這裡就變了）。</param>
    /// <param name="AfterMove">ContinueDrag **之後**的矩形＝<strong>拖曳中</strong>的值。
    ///   ⚠ 實測（2026-10-06，不是推論）：ContinueDrag 收尾會呼叫 <see cref="Constrain"/>，而它的位置
    ///   夾制與 Move 分支自己那兩條 <c>Math.Clamp</c> 的**範圍完全相同**（都是
    ///   <c>[0, 影像寬 − 矩形寬]</c>），所以「單獨把其中一條的上界改壞」在**任何**時點都看不出來
    ///   ——把 ContinueDrag 的上界改成 <c>W</c> 之後，整個自測仍然全綠（連拖曳中的值都一樣）。
    ///   要讓矩形跑出影像必須兩層一起放寬，而自測的「拖到邊界」那一格抓的就是它。
    ///   所以這裡的價值是「<strong>拖曳中的值也必須已經貼齊邊緣</strong>」，不是「抓得到某一行的錯」。</param>
    /// <param name="AfterEnd">EndDrag **之後**的矩形。</param>
    internal readonly record struct DragRun(string Mode, string Note,
                                            Int32Rect Before, Int32Rect AfterBegin,
                                            Int32Rect AfterMove, Int32Rect AfterEnd);

    /// <summary>程式化拖曳：把畫面座標的起點與終點餵進**真正的那三個方法**。</summary>
    public DragRun SelfTestDrag(Point from, Point to, List<string> trace)
    {
        Int32Rect before = _rect;
        BeginDrag(from, out string note);
        string mode = _dragMode.ToString();
        Int32Rect afterBegin = _rect;
        trace.Add($"begin from=({from.X:0.#},{from.Y:0.#}) mode={mode} note='{note}' " +
                  $"rect={_rect.X},{_rect.Y},{_rect.Width},{_rect.Height}");
        ContinueDrag(to);
        Int32Rect afterMove = _rect;
        trace.Add($"move to=({to.X:0.#},{to.Y:0.#}) rect={_rect.X},{_rect.Y},{_rect.Width},{_rect.Height}");
        EndDrag();
        trace.Add($"end rect={_rect.X},{_rect.Y},{_rect.Width},{_rect.Height}");
        return new DragRun(mode, note, before, afterBegin, afterMove, _rect);
    }

    /// <summary>
    /// ①②兩個座標系之間的橋（自測用）：影像現在是怎麼被縮進這張畫布的。
    ///
    /// 拖曳的每一個換算都靠這一組數字（<see cref="ToImage"/>、<see cref="BeginDrag"/> 的命中判定、
    /// <see cref="UpdateVisuals"/> 的繪製），所以自測要從「影像像素」算出「畫面座標」時，
    /// 唯一的正確來源就是這裡。自己再算一次 <c>min(860/W, 520/H)</c> 會變成第二份規則，
    /// 而它與視窗不一致時，那一格會紅在錯誤的地方（紅在測試的算式上，不是紅在產品上）。
    /// </summary>
    public (double Scale, double ImageLeft, double ImageTop, double DisplayWidth, double DisplayHeight)
        SelfTestStageMapping() => (_scale, _imageLeft, _imageTop, _displayWidth, _displayHeight);

    /// <summary>
    /// 一句話的狀態快照（自測的斷言對象）。刻意把**判準**也放進來而不是只放數字：
    /// <c>ratio-ok</c> 是「這個矩形符不符合目前選的比例」的單一實作——驗收腳本若自己再算一次，
    /// 那個算式就會變成第二份規則，而它與產品的算式不一致時，套件會紅在錯誤的地方。
    /// 容差 1 %：換算的最後一步是整數取整（<see cref="CropMath"/> 的邊界取整），最多差半個像素。
    /// </summary>
    public string SelfTestState()
    {
        System.Windows.Interop.HwndSource? source = PresentationSource.FromVisual(this)
            as System.Windows.Interop.HwndSource;
        var ratio = CurrentRatioCheck();
        return $"loaded={IsLoaded} visible={IsVisible} " +
               $"hwnd={(source != null ? "0x" + source.Handle.ToInt64().ToString("X") : "(none)")} " +
               $"image={_imageWidth}x{_imageHeight} ratio={_ratioId} " +
               $"rect={_rect.X},{_rect.Y},{_rect.Width},{_rect.Height} " +
               $"ratio-ok={ratio.Ok} ratio-target={ratio.target:0.####} ratio-actual={ratio.actual:0.####} " +
               $"normalized={CropMath.ToNormalized(_rect, _imageWidth, _imageHeight, _ratioId).Describe()} " +
               $"stage={StageWidthPx}x{StageHeightPx} confirmed={(Result != null)} " +
               $"scroll={ScrollableExtentCount()} " +
               $"notice='{_noticeText.Text}'";
    }

    /// <summary>
    /// 可捲動範圍 &gt; 0 的 <see cref="ScrollViewer"/> 個數。應該是 0：這個視窗的每一塊都算得剛好，
    /// 一旦有人加了長文字或縮小了視窗，就會出現捲軸——而**捲軸把內容切掉**這件事，
    /// 從一張截圖上看得出來、卻沒有任何腳本讀得出來。所以它必須是一個可以斷言的數字。
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

    /// <summary>
    /// 目前的矩形比例與目標比例（<see cref="CropRatios.TargetRatio"/>）的差距。
    /// 「自由」沒有目標值 ⇒ 一律算符合（它本來就不該被改動）。容差 1 %（相對誤差）。
    /// </summary>
    private (bool Ok, double target, double actual) CurrentRatioCheck()
    {
        double actual = _rect.Height > 0 ? (double)_rect.Width / _rect.Height : 0;
        double? target = CropRatios.TargetRatio(_ratioId, _imageWidth, _imageHeight);
        if (target is not { } t || t <= 0) return (true, 0, actual);
        double error = Math.Abs(actual - t) / t;
        return (error <= 0.01, t, actual);
    }

    /// <summary>自測：按下**真正的確認鈕**（與使用者按的是同一個按鈕事件）。</summary>
    public void SelfTestConfirm() =>
        OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>自測：按下**真正的取消鈕**。</summary>
    public void SelfTestCancel() =>
        CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>自測：按下**真正的比例鈕**（走它自己的 Click 處理常式，不是呼叫 SelectRatio）。</summary>
    public bool SelfTestSelectRatio(string id)
    {
        if (!_ratioButtons.TryGetValue(CropRatios.CanonicalId(id), out ToggleButton? button)) return false;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>自測：目前那一行提示文字（退化輸入的處置就是顯示提示，所以它必須可被斷言）。</summary>
    public string SelfTestNotice => _noticeText.Text;

    /// <summary>
    /// 把這張視窗畫成一張 PNG（**離屏合成**，不是螢幕擷取）。
    /// 與 <see cref="SettingsUi.DumpFrame"/> 同一條路徑、同一個「不是空白圖」的檢查：
    /// 回 true 保證檔案裡有真的畫面。空白圖在「有沒有捲軸」這種題目上會給出假通過。
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
