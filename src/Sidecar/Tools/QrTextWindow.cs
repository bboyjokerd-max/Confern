using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// 這個專案同時開了 UseWPF 與 UseWindowsForms，同名型別會「模稜兩可」（見 CropUi.cs 的說明）。
// 這個視窗要的一律是 WPF 那一組，所以逐項釘死——尤其是 Application／TextBox／Clipboard，
// 它們在 System.Windows.Forms 都有同名兄弟，漏打一次就會悄悄換成另一組（而且照樣編譯過）。
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Sidecar;

/// <summary>
/// 「解出來的內容不是網址」時顯示文字的視窗（工具 `read-qr`，2026-10-07 使用者裁定的產品語意）。
///
/// ## 它只做兩件事，而且不做第三件
///   ①**顯示**解出來的文字——唯讀但可以選取（WPF 的唯讀 TextBox 本來就支援選取與 Ctrl+C，
///     這裡再加上一條明確的 Ctrl+C 繫結，因為「可以複製」是使用者的要求，不該取決於
///     焦點是不是剛好落在文字框上）；
///   ②一個**複製**按鈕——按下去才寫剪貼簿。「寫剪貼簿」是一個副作用，所以它必須是用者的動作，
///     不可以開窗就寫（那會在使用者沒要求的情況下蓋掉他剪貼簿裡的東西）。
///   它**不產生任何檔案**、不改來源檔、不寫任何設定（見 <see cref="QrTextRouter"/>）。
///
/// ## 形狀照 <see cref="CropWindow"/>（本產品「怎麼開一個自己的 WPF 視窗」的既有樣板）
///   · 純 WPF、**沒有 XAML**：整個畫面在程式裡組出來；
///   · 顏色一律來自 <see cref="SettingsUi.StyleKit"/>（＝<see cref="WheelDesign"/> 的既有權杖），
///     所以「配色只有一個權威」與「0 個硬編碼色」這兩條紀律不必另外維護一份；
///   · 事件處理常式**不丟例外**（本專案為「事件處理常式丟例外殺掉 UI 執行緒」付過代價）。
///
/// 生命週期（單一實例、開過再開、關閉後行程照常結束）不在這裡，在 <see cref="QrTextUi"/>。
/// </summary>
internal sealed class QrTextWindow : Window
{
    /// <summary>文字框的可視高度。固定的理由與裁切視窗的畫布相同：版面可重現。</summary>
    public const double TextHeight = 260;

    private readonly string _text;
    private readonly string _sourceName;
    private readonly Action<string>? _log;

    private readonly TextBox _body;

    /// <summary>「複製」按鈕（自測按的是**真的這一顆**，不是另一條內部路徑）。</summary>
    public Button CopyButton { get; }

    /// <summary>剪貼簿寫入成功時的字（自測要斷言「複製真的把字放進剪貼簿」）。</summary>
    private string _copied = "";

    /// <summary>這張視窗正在顯示的**原始**字串（自測要做逐字元比對；不經任何加工）。</summary>
    public string TextContent => _body.Text;

    /// <summary>來源檔的檔名（標題列顯示的那一個；判定不讀它）。</summary>
    public string SourceName => _sourceName;

    /// <summary>最近一次「複製」寫進剪貼簿的字。</summary>
    public string CopiedText => _copied;

    public QrTextWindow(string text, string sourceName, Action<string>? log)
    {
        _text = text ?? "";
        _sourceName = sourceName;
        _log = log;

        Title = ProductInfo.Name;
        Width = 720;
        Height = 470;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SettingsUi.StyleKit.WindowBg;
        Foreground = SettingsUi.StyleKit.TextMain;
        FontFamily = SettingsUi.StyleKit.Font;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 標題
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 文字
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 提示
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按鈕
        Content = root;

        BuildHeader(root);

        _body = new TextBox
        {
            Text = _text,
            IsReadOnly = true,
            // 唯讀但**可以選取**：這兩個是分開的開關，只設 IsReadOnly 會讓游標與選取都停用，
            // 於是「可以複製」這句話在畫面上是假的（Ctrl+C 要靠選取）。
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = SettingsUi.StyleKit.Font,
            FontSize = 14,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 14, 0, 0),
            Background = SettingsUi.StyleKit.SurfaceBg,
            Foreground = SettingsUi.StyleKit.TextMain,
            BorderBrush = SettingsUi.StyleKit.CardBorder,
            BorderThickness = new Thickness(1),
            Height = TextHeight,
            VerticalAlignment = VerticalAlignment.Top
        };
        // 明確的 Ctrl+C：`CommandBindings` 上那一條才是 WPF 真正的複製指令，
        // 而它預設綁在 TextBox 的類別繫結上——上面那一條 KeyBinding 是保險，
        // 讓「按下去一定複製」不取決於焦點。
        _body.InputBindings.Add(new KeyBinding(ApplicationCommands.Copy, Key.C, ModifierKeys.Control));
        Grid.SetRow(_body, 1);
        root.Children.Add(_body);

        var hint = new TextBlock
        {
            Text = "這是圖片裡解出來的內容（沒有產生任何檔案）。可以選取，Ctrl+C 或下面的按鈕複製。",
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        hint.Foreground = SettingsUi.StyleKit.TextFaint;
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);

        CopyButton = new Button { Content = "複製", Width = 128, Height = 32 };
        SettingsUi.StyleKit.PrimaryButton(CopyButton);
        CopyButton.Click += (_, _) => Copy();

        var footer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        footer.Children.Add(CopyButton);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        _log?.Invoke($"qr text ui: window created for {_sourceName} ({_text.Length} chars, " +
                     "read-only + selectable, one explicit copy button)");
    }

    private void BuildHeader(Grid root)
    {
        var header = new StackPanel();
        var title = new TextBlock { Text = "讀 QR", FontSize = 21, FontWeight = FontWeights.SemiBold };
        title.Foreground = SettingsUi.StyleKit.TextMain;
        var subtitle = new TextBlock
        {
            Text = $"{_sourceName}　（不是網址，所以只顯示在這裡）",
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
    /// 把文字放進剪貼簿。**失敗不可以丟出去**（剪貼簿會被別的行程鎖住是常態），
    /// 而「安靜地什麼都沒發生」也不行——失敗會留在日誌裡，而且 <see cref="CopiedText"/>
    /// 保持空字串，自測因此看得出「按了但沒複製到」。
    /// </summary>
    private void Copy()
    {
        try
        {
            Clipboard.SetText(_text);
            _copied = _text;
            _log?.Invoke($"qr text ui: copied {_text.Length} chars to the clipboard (user pressed the button)");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"qr text ui: the clipboard refused the text ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// 自測：按下**真正的複製鈕**（<c>RaiseEvent</c> 打的就是使用者按下去的那一個 Click 事件，
    /// 不是去呼叫私有的 <c>Copy()</c>——後者能證明的事情，正好就是它自己）。
    /// </summary>
    public void SelfTestCopy() => CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>自測：把焦點給文字框（證明「唯讀但可選取」的那條路是活著的）。</summary>
    public bool SelfTestFocusBody()
    {
        _body.Focus();
        return _body.IsKeyboardFocused || _body.IsFocused;
    }

    /// <summary>
    /// 自測用的一句話狀態（形狀比照 <see cref="CropWindow.SelfTestState"/>）。
    /// 它刻意把**判準**放進來（<c>selectable</c>、<c>hasHwnd</c>），而不是只放數字：
    /// 「唯讀但可以選取」是一組旗標的組合，驗收自己在腳本裡重算那一組就是第二份規則。
    /// </summary>
    public string SelfTestState()
    {
        System.Windows.Interop.HwndSource? source =
            PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        // 唯讀 + 可見游標 + 可以選取：三個一起才等於「使用者選得到字」。
        bool selectable = _body.IsReadOnly && _body.IsReadOnlyCaretVisible;
        return $"loaded={IsLoaded} visible={IsVisible} " +
               $"hwnd={(source != null ? "0x" + source.Handle.ToInt64().ToString("X") : "(none)")} " +
               $"chars={_body.Text.Length} selectable={selectable} " +
               $"copyButton='{CopyButton.Content}' copiedChars={_copied.Length} " +
               $"source='{_sourceName}'";
    }
}
