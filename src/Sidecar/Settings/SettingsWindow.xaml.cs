using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// 這個專案同時開了 UseWPF 與 UseWindowsForms（見 csproj 的註解），所以 System.Windows.Forms
// 也參與名稱解析，一堆型別會變成「模稜兩可」。設定頁要的**一律是 WPF 那一組**，所以在這裡
// 逐項釘死，而不是在每個地方打全名——打全名遲早會漏掉一處，然後變成語意不同的另一個型別。
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace Sidecar;

/// <summary>
/// 設定頁（規格 §8）的視窗本體。
///
/// 分工（刻意切乾淨，避免第二個權威）：
///   - **文字**：全部來自 <see cref="UiStrings"/>，於 <see cref="ApplyLanguage"/> 指派。
///     XAML 裡沒有任何人看得懂的字，也沒有綁定——畫面上的每個值都是這裡推上去的。
///   - **顏色**：全部來自 <see cref="SettingsUi.StyleKit"/>，於建構時套用一次。
///   - **存檔**：唯一走 <see cref="SettingsStore.Save"/>（原子寫入、可覆寫）。
///     本檔**沒有** <c>File.WriteAllText</c>，也永遠不會有。
///   - **欄位對應**：改值走 <see cref="SettingsStore.TryApply"/>（「鍵→欄位」的唯一擁有者），
///     不在這裡自己 switch。唯一例外是「語言下拉的顯示字 → 正規值」的還原（見 <see cref="CanonicalLanguage"/>）。
///   - **觸發鍵合法性**：只有 <see cref="SettingsStore.TryResolveTriggerKey"/> 說了算；
///     它回傳的 <see cref="TriggerKeyInfo.Name"/> 才是寫回檔案的值，<see cref="TriggerKeyInfo.Warning"/>
///     （規格 §8 的 Shift 警告）原樣顯示，不由 UI 自己編。
///
/// 這個視窗由 <see cref="SettingsUi"/> 在自己的 STA 執行緒上建立與顯示；
/// <c>onSaved</c> 因此是在那條 WPF 執行緒上被呼叫的。
/// </summary>
internal partial class SettingsWindow : Window
{
    /// <summary>規格 §8 允許直接挑的常用觸發鍵（其他單鍵走「按下新鍵」）。</summary>
    private static readonly string[] PresetTriggerKeys = { "Space", "Tab", "CapsLock", "Escape", "Shift" };

    /// <summary>
    /// 輸出資料夾的三個選項（規格 §9.5）。<c>Tag</c> 就是**寫回設定檔的值**，<c>Key</c> 是畫面上的標籤；
    /// 兩者刻意分開，因為設定檔只認 `''／desktop／路徑` 這三種寫法，而畫面上寫的是中文標籤。
    /// <c>custom</c> 不是存進檔案的寫法，它是「用旁邊那個文字框的路徑」這個 UI 狀態——
    /// 真正送進 <see cref="SettingsStore.TryApply"/> 的是文字框的內容，不是這個字。
    /// </summary>
    private static readonly (string Tag, string Key)[] OutputPresets =
    {
        ("", "out.source"), ("desktop", "out.desktop"), ("custom", "out.custom")
    };

    /// <summary>
    /// 壓縮方式的四個選項（M3 第三顆扇形，2026-10-06 使用者裁定：等級走設定頁）。
    /// <c>Tag</c> 就是**寫回設定檔的值**——直接取自 <see cref="CompressionPolicy"/>，
    /// 所以「有哪些等級」仍然只有那一個擁有者；這裡只是把標籤配上去。
    /// </summary>
    private static readonly (string Tag, string Key)[] CompressionPresets =
    {
        (CompressionPolicy.ModeSmall, "compress.small"),
        (CompressionPolicy.ModeStandard, "compress.standard"),
        (CompressionPolicy.ModeHigh, "compress.high"),
        (CompressionPolicy.ModeTargetSize, "compress.targetSize")
    };

    /// <summary>
    /// 上限那兩格的單位。**只有畫面才有單位**：設定檔存的是 KB 一個數字，
    /// 所以「1024 KB」與「1 MB」是同一個設定值，不會在 diff 裡看起來像兩筆不同的設定。
    /// </summary>
    private static readonly (string Tag, string Key)[] CompressionUnits =
    {
        ("kb", "unit.kb"), ("mb", "unit.mb")
    };

    private readonly string _path;
    private readonly Action<Settings>? _onSaved;
    private readonly Action<string>? _log;
    private readonly long _generation;

    /// <summary>目前要顯示的語言（正規值：system／zh-Hant／en）。</summary>
    private string _language;

    /// <summary>配色下拉目前的 id（五套色票的 id 在 <see cref="Palettes"/>）。</summary>
    private string _palette;

    /// <summary>目前 UI 上的觸發鍵正規名（例如 <c>Space</c>）。建構時由 <see cref="LoadFromSettings"/> 指派。</summary>
    private string _triggerKey = "Space";

    /// <summary>
    /// 目前 UI 上選的輸出資料夾選項：空字串＝來源檔旁邊（預設）／<c>desktop</c>／<c>custom</c>。
    /// 建構時由 <see cref="LoadFromSettings"/> 指派。寫回檔案的值由 <see cref="OutputValueFromUi"/> 決定。
    /// </summary>
    private string _outputPreset = "";

    /// <summary>
    /// 目前 UI 上選的壓縮方式：<see cref="CompressionPolicy.ModeSmall"/>／
    /// <see cref="CompressionPolicy.ModeStandard"/>／<see cref="CompressionPolicy.ModeHigh"/>／
    /// <see cref="CompressionPolicy.ModeTargetSize"/>。建構時由 <see cref="LoadFromSettings"/> 指派。
    /// </summary>
    private string _compressionPreset = CompressionPolicy.ModeStandard;

    /// <summary>上限那一格的顯示單位：<c>kb</c> 或 <c>mb</c>（只有畫面用它）。</summary>
    private string _compressionUnit = "kb";

    /// <summary>真的按下「儲存」且成功時，才記下來給呼叫端。</summary>
    private Settings _saved = new();

    private bool _hasSaved;

    /// <summary>正在等使用者按下新鍵（此時 PreviewKeyDown 會吃掉按鍵）。</summary>
    private bool _capturing;

    /// <summary>重新套用字串時抑制 <c>SelectionChanged</c>，否則會自己回寫一次設定。</summary>
    private bool _suppressComboEvents;

    /// <summary>第一次顯示時把焦點放到「按下新鍵」，讓鍵盤操作直接可用。</summary>
    private bool _focusPending = true;

    private string _statusKey = "status.idle";
    private object[] _statusArgs = Array.Empty<object>();
    private bool _statusIsError;

    public SettingsWindow(Settings current, string path, Action<Settings>? onSaved, Action<string>? log, long generation)
    {
        _path = path;
        _onSaved = onSaved;
        _log = log;
        _generation = generation;
        _language = current.Language;        _palette = Palettes.CurrentId;      // 設定檔在 Load 時已經套用過了，這裡只記住它

        InitializeComponent();

        SettingsUi.StyleKit.Apply(this);
        CaptureButton.Template = SettingsUi.StyleKit.ButtonTemplate();
        CaptureButton.Click += OnCaptureButtonClick;

        JpgSlider.ValueChanged += OnValueSliderChanged;
        WebpSlider.ValueChanged += OnValueSliderChanged;
        TimeoutSlider.ValueChanged += OnValueSliderChanged;

        StartupToggle.Click += OnStartupToggleClick;
        HotkeyToggle.Click += OnHotkeyToggleClick;
        DiagnosticsToggle.Click += OnDiagnosticsToggleClick;
        HotkeyBox.TextChanged += OnHotkeyTextChanged;

        TriggerPresetBox.SelectionChanged += OnTriggerPresetChanged;
        LanguageBox.SelectionChanged += OnLanguageChanged;        PaletteBox.SelectionChanged += OnPaletteChanged;
        OutputPresetBox.SelectionChanged += OnOutputPresetChanged;
        OutputPathBox.TextChanged += OnOutputPathChanged;

        CompressModeBox.SelectionChanged += OnCompressModeChanged;
        CompressUnitBox.SelectionChanged += OnCompressUnitChanged;
        CompressTargetBox.TextChanged += OnCompressTargetChanged;

        SaveButton.Click += OnSaveClick;
        CancelButton.Click += OnCancelClick;

        PreviewKeyDown += OnPreviewKeyDown;
        Activated += OnWindowActivated;

        LoadFromSettings(current);
        ApplyLanguage();
    }

    /// <summary>
    /// UI 執行緒的識別碼（診斷用）。「存檔成功」的回呼是在這條執行緒上被叫的，
    /// 呼叫端要跨執行緒時可以拿它來寫證據。
    /// </summary>
    public int UiThreadId => Environment.CurrentManagedThreadId;

    // =====================================================================================
    // 載入現值
    // =====================================================================================

    private void LoadFromSettings(Settings s)
    {
        _triggerKey = CanonicalTrigger(s.TriggerKey);

        StartupToggle.IsChecked = s.StartWithWindows;
        SettingsUi.StyleKit.SetToggleState(StartupToggle, s.StartWithWindows);

        DiagnosticsToggle.IsChecked = s.Diagnostics;
        SettingsUi.StyleKit.SetToggleState(DiagnosticsToggle, s.Diagnostics);

        JpgSlider.Value = Math.Clamp(s.JpgQuality, (int)JpgSlider.Minimum, (int)JpgSlider.Maximum);
        WebpSlider.Value = Math.Clamp(s.WebpQuality, (int)WebpSlider.Minimum, (int)WebpSlider.Maximum);
        TimeoutSlider.Value = Math.Clamp(s.CaptureTimeoutSeconds, (int)TimeoutSlider.Minimum, (int)TimeoutSlider.Maximum);

        // 緊急熱鍵「可關」＝空字串（SettingsStore.TryApply 對空字串的拒絕理由就是規格 §8 的「可關」）
        bool hotkeyOn = !string.IsNullOrWhiteSpace(s.EmergencyHotkey);
        HotkeyToggle.IsChecked = hotkeyOn;
        SettingsUi.StyleKit.SetToggleState(HotkeyToggle, hotkeyOn);
        HotkeyBox.Text = hotkeyOn ? s.EmergencyHotkey : "";
        HotkeyBox.IsEnabled = hotkeyOn;

        RebuildTriggerItems();
        RebuildLanguageItems();        RebuildPaletteItems();
        LoadOutputFolder(s.OutputFolder);
        RebuildOutputItems();
        RefreshOutput();

        LoadCompression(s.CompressionMode, s.CompressionTargetKb);
        RebuildCompressionItems();
        RefreshCompress();
    }

    /// <summary>把設定檔裡的觸發鍵正規化；解析不了就保留原字串並在 warning 行說出來（在 <see cref="RefreshTrigger"/>）。</summary>
    private string CanonicalTrigger(string text) =>
        SettingsStore.TryResolveTriggerKey(text, out TriggerKeyInfo info, out _) ? info.Name : (text ?? "").Trim();

    // =====================================================================================
    // 文字（唯一的指派點）
    // =====================================================================================

    /// <summary>
    /// 把所有文字重新指派一次。**語言切換時整窗重跑**，所以這裡是「畫面上所有字」的完整清單；
    /// 新增欄位忘了加進來，在英文模式下就會看到中文，一眼就看得出來。
    /// </summary>
    private void ApplyLanguage()
    {
        string L(string key) => UiStrings.Get(key, _language);

        Title = L("window.title");
        TitleText.Text = L("window.title");
        SubtitleText.Text = L("window.subtitle");

        BehaviourCardTitle.Text = L("page.behaviour");
        BehaviourCardDesc.Text = L("page.behaviourDesc");
        QualityCardTitle.Text = L("page.quality");
        QualityCardDesc.Text = L("page.qualityDesc");
        SafetyCardTitle.Text = L("page.safety");
        SafetyCardDesc.Text = L("page.safetyDesc");

        TriggerLabel.Text = L("field.triggerKey");
        CaptureButton.Content = _capturing ? L("btn.capturing") : L("btn.capture");

        StartupLabel.Text = L("field.startWithWindows");
        StartupStubBadge.Text = L("stub.badge");

        LanguageLabel.Text = L("field.language");        PaletteLabel.Text = L("field.palette");
        OutputLabel.Text = L("field.outputFolder");

        JpgLabel.Text = L("field.jpgQuality");
        WebpLabel.Text = L("field.webpQuality");

        NamingLabel.Text = L("field.namingCollision");
        NamingValueText.Text = L("naming.value");
        NamingReasonText.Text = L("naming.reason");

        TimeoutLabel.Text = L("field.timeout");
        CompressLabel.Text = L("field.compressionMode");

        HotkeyLabel.Text = L("field.emergencyHotkey");
        HotkeyStubText.Text = L("stub.emergencyHotkey");

        DiagnosticsLabel.Text = L("field.diagnostics");

        SaveButton.Content = L("btn.save");
        CancelButton.Content = L("btn.cancel");

        PathText.Text = $"{L("path.prefix")}{_path}" + (File.Exists(_path) ? "" : $" {L("path.missing")}");

        ApplyToggleLanguage(StartupToggle, StartupToggle.IsChecked == true);
        ApplyToggleLanguage(HotkeyToggle, HotkeyToggle.IsChecked == true);
        ApplyToggleLanguage(DiagnosticsToggle, DiagnosticsToggle.IsChecked == true);

        UpdateQualityLabels();

        RebuildTriggerItems();
        RebuildLanguageItems();        RebuildPaletteItems();
        RebuildOutputItems();
        RebuildCompressionItems();
        RefreshTrigger();
        RefreshOutput();
        RefreshCompress();
        RenderStatus();
    }

    /// <summary>下拉與開關的字，跟著語言換。</summary>
    private void ApplyToggleLanguage(ToggleButton toggle, bool on) =>
        toggle.Content = UiStrings.Get(on ? "toggle.on" : "toggle.off", _language);

    private void UpdateQualityLabels()
    {
        JpgValueText.Text = ((int)Math.Round(JpgSlider.Value)).ToString();
        WebpValueText.Text = ((int)Math.Round(WebpSlider.Value)).ToString();
        TimeoutValueText.Text = ((int)Math.Round(TimeoutSlider.Value)) + UiStrings.Get("unit.seconds", _language);
    }

    // =====================================================================================
    // 觸發鍵
    // =====================================================================================

    /// <summary>
    /// 重建觸發鍵下拉。清單＝規格 §8 明列的常用鍵；目前的值不在清單裡（例如 <c>F8</c>、<c>0xNN</c>）
    /// 就**多一項**顯示它，而不是假裝它不存在後靜默跳回空白鍵。
    /// </summary>
    private void RebuildTriggerItems()
    {
        bool previous = _suppressComboEvents;
        _suppressComboEvents = true;
        try
        {
            TriggerPresetBox.Items.Clear();
            foreach (string name in PresetTriggerKeys)
                TriggerPresetBox.Items.Add(MakeItem(UiStrings.Get($"vk.{name}", _language), name));

            if (Array.IndexOf(PresetTriggerKeys, _triggerKey) < 0)
                TriggerPresetBox.Items.Add(MakeItem(
                    string.Format(UiStrings.Get("key.customLabel", _language), _triggerKey), _triggerKey));

            TriggerPresetBox.SelectedItem = TriggerPresetBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _triggerKey, StringComparison.Ordinal));
        }
        finally
        {
            _suppressComboEvents = previous;
        }
    }

    private ComboBoxItem MakeItem(string text, string tag)
    {
        var item = new ComboBoxItem { Content = text, Tag = tag };
        SettingsUi.StyleKit.StyleComboItem(item);
        return item;
    }

    /// <summary>
    /// 更新觸發鍵那一行的「現在是什麼鍵」與規格警告（<see cref="TriggerKeyInfo.Warning"/>）。
    /// 警告文字**原樣**來自 <see cref="SettingsStore.TryResolveTriggerKey"/>，不受介面語言影響。
    /// </summary>
    private void RefreshTrigger()
    {
        if (SettingsStore.TryResolveTriggerKey(_triggerKey, out TriggerKeyInfo info, out string reason))
        {
            TriggerValueText.Text = SettingsStore.NameOf(info.Vk);
            TriggerWarningText.Text = info.Warning ?? "";
            return;
        }

        // 解析不了（例如檔案裡是組合鍵）：說出來，不要顯示一個看起來很正常的名字
        TriggerValueText.Text = UiStrings.Get("key.unknown", _language);
        TriggerWarningText.Text = string.IsNullOrEmpty(reason) ? "" : reason;
    }

    /// <summary>
    /// 使用者按了「按下新鍵」：下一次按鍵會被 <see cref="OnPreviewKeyDown"/> 吃掉並解析。
    /// 逾時沒有這回事——按 Esc 或點別的地方就取消（焦點離開會清掉旗標）。
    /// </summary>
    private void OnCaptureButtonClick(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        CaptureButton.Content = UiStrings.Get("btn.capturing", _language);
        TriggerValueText.Text = UiStrings.Get("key.captureHint", _language);
        Keyboard.Focus(this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        e.Handled = true;                       // 連 Tab／空白鍵都要吃掉，否則會變成移動焦點
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                 or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            TriggerValueText.Text = UiStrings.Get("key.captureHintModifier", _language);
            return;
        }

        if (key == Key.Escape && e.Key != Key.System)
        {
            StopCapturing();
            RefreshTrigger();
            return;
        }

        uint vk;
        try
        {
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        catch (Exception ex)
        {
            Report($"[settings] key capture failed: {ex.GetType().Name}: {ex.Message}");
            TriggerValueText.Text = UiStrings.Get("key.captureHintUnsupported", _language);
            return;
        }

        // 合法性仍然只有 SettingsStore 說了算（不自己猜哪些 VK 可用）：把 VK 走它的解析路徑驗一次。
        // 但**寫回的名字要取 SettingsStore.NameOf**——直接拿 "0x{vk:X2}" 去解析，TryResolveTriggerKey
        // 會忠實地把那個十六進位字串原樣回傳（它認得 0xNN 這種寫法），於是 F8 會被存成 "0x77"：
        // 功能等價，卻是給人看的設定檔裡最不該出現的東西（NameOf 就是「VK → 正式名稱」的權威）。
        string canonical = SettingsStore.NameOf(vk);
        if (!SettingsStore.TryResolveTriggerKey(canonical, out _, out string reason) ||
            !SettingsStore.TryResolveTriggerKey($"0x{vk:X2}", out _, out _))
        {
            Report($"[settings] captured VK 0x{vk:X2} ({canonical}) rejected: {reason}");
            TriggerValueText.Text = UiStrings.Get("key.captureHintUnsupported", _language);
            return;
        }

        // 警告要用「使用者真的按的那個鍵」去問（警告屬於按鍵語意，不屬於寫法）
        SettingsStore.TryResolveTriggerKey(canonical, out TriggerKeyInfo info, out _);

        _triggerKey = canonical;
        StopCapturing();
        RebuildTriggerItems();
        RefreshTrigger();

        if (info.Warning != null)
            Report($"[settings] trigger key warning: {info.Warning}");
        MarkDirty();
    }

    private void StopCapturing()
    {
        _capturing = false;
        CaptureButton.Content = UiStrings.Get("btn.capture", _language);
    }

    /// <summary>焦點跑掉就取消捕獲，不讓 UI 卡在「請按鍵…」。</summary>
    private void OnCaptureButtonLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturing) return;
        StopCapturing();
        RefreshTrigger();
    }

    private void OnTriggerPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (TriggerPresetBox.SelectedItem is not ComboBoxItem item || item.Tag is not string name) return;

        if (!SettingsStore.TryResolveTriggerKey(name, out TriggerKeyInfo info, out string reason))
        {
            Report($"[settings] preset trigger key '{name}' rejected: {reason}");
            return;
        }

        _triggerKey = info.Name;
        RefreshTrigger();
        if (info.Warning != null)
            Report($"[settings] trigger key warning: {info.Warning}");
        MarkDirty();
    }

    // =====================================================================================
    // 語言
    // =====================================================================================

    private void RebuildLanguageItems()
    {
        bool previous = _suppressComboEvents;
        _suppressComboEvents = true;
        try
        {
            LanguageBox.Items.Clear();
            foreach (string value in new[] { UiStrings.System, UiStrings.ZhHant, UiStrings.En })
                LanguageBox.Items.Add(MakeItem(UiStrings.Get($"lang.{value}", _language), value));

            LanguageBox.SelectedItem = LanguageBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _language, StringComparison.Ordinal));
        }
        finally
        {
            _suppressComboEvents = previous;
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (LanguageBox.SelectedItem is not ComboBoxItem item) return;

        string canonical = CanonicalLanguage(item.Tag as string ?? "");
        if (!SettingsStore.TryApply(_saved, "language", canonical, Report, out Settings next))
        {
            // 選到的值 SettingsStore 不接受：把畫面拉回目前值，並說出來
            Report($"[settings] language selection '{item.Tag}' rejected by SettingsStore");
            RebuildLanguageItems();        RebuildPaletteItems();
            return;
        }

        _saved = next;
        _language = canonical;
        ApplyLanguage();                 // 立即重刷整窗文字（下拉本身也重建，選取會被還原）
        MarkDirty();
    }

    /// <summary>
    /// 顯示字 → 正規值。**這是本檔唯一自己做的欄位轉換**：SettingsStore 沒有反向查表，
    /// 而 <c>TryApply</c> 要的是正規值；查不到就原樣送出，讓 SettingsStore 去拒絕並說原因。
    /// </summary>
    /// <summary>
    /// 配色下拉（2026-10-21）。顯示名跟著介面語言（Palette.NameZh / Palette.NameEn），
    /// 值是色票 id；**只送 id 給 SettingsStore**——認不得的 id 由它拒絕並說明，這裡不自己判斷。
    /// </summary>
    private void RebuildPaletteItems()
    {
        bool previous = _suppressComboEvents;
        _suppressComboEvents = true;
        try
        {
            PaletteBox.Items.Clear();
            bool en = string.Equals(UiStrings.Current, UiStrings.En, StringComparison.Ordinal);
            foreach (Palette palette in Palettes.Selectable)
                PaletteBox.Items.Add(MakeItem(en ? palette.NameEn : palette.NameZh, palette.Id));

            PaletteBox.SelectedItem = PaletteBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _palette, StringComparison.Ordinal));
        }
        finally
        {
            _suppressComboEvents = previous;
        }
    }

    /// <summary>選了另一套配色：當場換（Palettes.Current 是唯一狀態），並記進待存的設定。</summary>
    private void OnPaletteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (PaletteBox.SelectedItem is not ComboBoxItem item) return;

        string id = item.Tag as string ?? "";
        if (!SettingsStore.TryApply(_saved, "palette", id, Report, out Settings next))
        {
            Report($"[settings] palette selection '{id}' rejected by SettingsStore");
            RebuildPaletteItems();
            return;
        }

        _saved = next;
        _palette = Palettes.CurrentId;
        MarkDirty();
    }
    private string CanonicalLanguage(string tag)
    {
        if (string.Equals(tag, "system", StringComparison.OrdinalIgnoreCase)) return UiStrings.System;
        if (string.Equals(tag, "zh-Hant", StringComparison.OrdinalIgnoreCase)) return UiStrings.ZhHant;
        if (string.Equals(tag, "en", StringComparison.OrdinalIgnoreCase)) return UiStrings.En;
        return tag;
    }

    // =====================================================================================
    // 輸出資料夾（規格 §9.5，2026-10-05 使用者裁定）
    //
    // 「設定值」與「畫面」兩邊的寫法**不一樣**，而且必須一直不一樣：
    //   畫面＝標籤（來源檔旁邊／桌面／自訂資料夾），設定檔＝`''／desktop／資料夾路徑`。
    // 轉換的權威是 <see cref="SettingsStore.TryApply"/>（鍵→欄位的唯一擁有者）：
    // 這裡只把「選了哪一項」換成它的 Tag，再把 Tag 送進去，不自己判斷路徑可不可用。
    // =====================================================================================

    /// <summary>把下拉的項目重建一次（語言切換時整個重跑，所以標籤會跟著換）。</summary>
    private void RebuildOutputItems()
    {
        bool previous = _suppressComboEvents;
        _suppressComboEvents = true;
        try
        {
            OutputPresetBox.Items.Clear();
            foreach ((string tag, string key) in OutputPresets)
                OutputPresetBox.Items.Add(MakeItem(UiStrings.Get(key, _language), tag));

            OutputPresetBox.SelectedItem = OutputPresetBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _outputPreset, StringComparison.Ordinal));
        }
        finally
        {
            _suppressComboEvents = previous;
        }
    }

    /// <summary>
    /// 設定檔的值 → 畫面（<see cref="LoadFromSettings"/> 與 <see cref="ApplyEdited"/> 共用）。
    /// 認不得的一律當成「自訂資料夾」並把原文放進文字框——**不靜默改成預設**：
    /// 使用者自己改過設定檔時，他打的那個路徑要原樣出現在畫面上。
    /// </summary>
    private void LoadOutputFolder(string? value)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0 ||
            v.Equals("source", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("sibling", StringComparison.OrdinalIgnoreCase))
        {
            _outputPreset = "";
            OutputPathBox.Text = "";
            return;
        }

        if (v.Equals("desktop", StringComparison.OrdinalIgnoreCase))
        {
            _outputPreset = "desktop";
            OutputPathBox.Text = "";
            return;
        }

        _outputPreset = "custom";
        OutputPathBox.Text = v;
    }

    private void OnOutputPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (OutputPresetBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        _outputPreset = tag;
        RefreshOutput();
        MarkDirty();
    }

    private void OnOutputPathChanged(object sender, TextChangedEventArgs e)
    {
        RefreshOutput();
        MarkDirty();
    }

    /// <summary>
    /// 更新這一行的可編輯狀態與提示。合法性**只有 <see cref="SettingsStore.TryApply"/> 說了算**——
    /// 這裡刻意把文字框的內容餵進去問一次（與存檔走同一條路），而不是自己寫一份「什麼叫可用的路徑」。
    /// 那個 <c>_ => { }</c> 的日誌：使用者每打一個字都會被拒絕一次，寫進證據檔只會洗版。
    /// </summary>
    private void RefreshOutput()
    {
        bool custom = string.Equals(_outputPreset, "custom", StringComparison.Ordinal);
        OutputPathBox.IsEnabled = custom;

        bool usable = SettingsStore.TryApply(_saved, "outputFolder", OutputPathBox.Text ?? "",
                                             _ => { }, out Settings _);
        OutputNoticeText.Text = custom && !usable ? UiStrings.Get("out.needPath", _language) : "";
    }

    /// <summary>
    /// 這一格要送進 <see cref="SettingsStore.TryApply"/> 的值：選了自訂就送文字框的路徑（可能是空的，
    /// 那就會被拒絕並說出來，不是靜默存成別的東西），否則送 Tag 本身（`''` 或 `desktop`）。
    /// </summary>
    private string OutputValueFromUi() =>
        string.Equals(_outputPreset, "custom", StringComparison.Ordinal) ? (OutputPathBox.Text ?? "") : _outputPreset;

    // =====================================================================================
    // 壓縮方式（M3 第三顆扇形，2026-10-06）
    //
    // 使用者裁定：壓縮的等級**由設定頁決定**——輪盤上不會多出「小檔／標準／高畫質」三顆扇形，
    // 也沒有循環切換。所以這一組控制項就是那個決定的**唯一介面**，而它底下的權威仍然是
    // CompressionPolicy（有哪些等級）與 SettingsStore.TryApply（鍵→欄位、拒絕理由）。
    //
    // ⚠ 這一整段**一個例外都不准丟**：這個專案才剛因為設定頁的例外殺掉 UI 執行緒付過代價
    //   （2026-10-06）。所以「不合法」一律走既有的兩條路：
    //     ①行內提示（RefreshCompress）＝使用者還在打字時就看到「還不能存」；
    //     ②存檔被擋（BuildFromUi）＝按下去時指名道姓地說出哪一格、什麼值。
    //   兩條路都**先問 SettingsStore.TryApply**，不自己寫第二份「什麼叫合法的大小」。
    // =====================================================================================

    /// <summary>把兩個下拉的項目重建一次（語言切換時整個重跑）。與 <see cref="RebuildOutputItems"/> 同一個形狀。</summary>
    private void RebuildCompressionItems()
    {
        bool previous = _suppressComboEvents;
        _suppressComboEvents = true;
        try
        {
            CompressModeBox.Items.Clear();
            foreach ((string tag, string key) in CompressionPresets)
                CompressModeBox.Items.Add(MakeItem(UiStrings.Get(key, _language), tag));
            CompressModeBox.SelectedItem = CompressModeBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _compressionPreset, StringComparison.Ordinal));

            CompressUnitBox.Items.Clear();
            foreach ((string tag, string key) in CompressionUnits)
                CompressUnitBox.Items.Add(MakeItem(UiStrings.Get(key, _language), tag));
            CompressUnitBox.SelectedItem = CompressUnitBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _compressionUnit, StringComparison.Ordinal));
        }
        finally
        {
            _suppressComboEvents = previous;
        }
    }

    /// <summary>
    /// 設定檔的值 → 畫面（<see cref="LoadFromSettings"/> 與 <see cref="ApplyEdited"/> 共用）。
    ///
    /// 模式走 <see cref="CompressionPolicy.CanonicalMode"/>（認不得的收斂成標準——與
    /// <see cref="Settings.Normalized"/> 同一個權威，所以畫面上顯示的一定是引擎會用的那一個）。
    /// 上限的顯示單位是**挑回得去的那一個**：能整除 1024 就用 MB（2048 KB 顯示成 2 MB），
    /// 否則用 KB（1500 KB 就顯示 1500 KB）——存的是 KB，一個數字，不存單位。
    /// </summary>
    private void LoadCompression(string? mode, int targetKb)
    {
        _compressionPreset = CompressionPolicy.CanonicalMode(mode);

        int kb = Math.Clamp(targetKb, 0, CompressionPolicy.MaxTargetKb);
        if (kb > 0 && kb % 1024 == 0)
        {
            _compressionUnit = "mb";
            CompressTargetBox.Text = (kb / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            _compressionUnit = "kb";
            CompressTargetBox.Text = kb > 0
                ? kb.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";
        }
    }

    private void OnCompressModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (CompressModeBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        _compressionPreset = tag;
        RefreshCompress();
        MarkDirty();
    }

    private void OnCompressUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (CompressUnitBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        _compressionUnit = tag;
        RefreshCompress();
        MarkDirty();
    }

    private void OnCompressTargetChanged(object sender, TextChangedEventArgs e)
    {
        RefreshCompress();
        MarkDirty();
    }

    /// <summary>
    /// 更新這一行的可編輯狀態與提示。合法性**只有 <see cref="SettingsStore.TryApply"/> 說了算**
    /// ——這裡把文字框的內容餵進去問一次（與存檔走同一條路），而不是自己寫一份
    /// 「什麼叫可用的上限」。那個 <c>_ => { }</c> 的日誌：使用者每打一個字都會被拒絕一次，
    /// 寫進證據檔只會洗版（與 <see cref="RefreshOutput"/> 同一個理由）。
    /// </summary>
    private void RefreshCompress()
    {
        bool targetSize = string.Equals(_compressionPreset, CompressionPolicy.ModeTargetSize,
                                        StringComparison.Ordinal);
        CompressTargetBox.IsEnabled = targetSize;
        CompressUnitBox.IsEnabled = targetSize;

        bool usable = SettingsStore.TryApply(_saved, "compressionTargetKb", CompressionTargetValueFromUi(),
                                             _ => { }, out Settings _);
        CompressNoticeText.Text = targetSize && !usable
            ? UiStrings.Get("compress.needSize", _language)
            : "";
    }

    /// <summary>
    /// 這一格要送進 <see cref="SettingsStore.TryApply"/> 的值：**一定是 KB**（設定檔的單位）。
    ///
    /// 刻意**不**在這裡判斷「合不合法」：非數字原樣送出（讓 SettingsStore 說「不是整數」）、
    /// 0 與負數原樣送出（讓它說「至少要 1 KB」）、太大則送一個一定超出上限的整數
    /// （讓它說「上限是 1048576 KB」）。自己判斷的話，畫面上的理由與存檔時的理由會是兩份。
    ///
    /// ⚠ 溢位要自己擋：<c>long.TryParse</c> 過得了 10^18，而 ×1024 會無聲地變成負數。
    /// </summary>
    private string CompressionTargetValueFromUi()
    {
        string text = (CompressTargetBox.Text ?? "").Trim();
        if (text.Length == 0) return "";

        if (!long.TryParse(text, System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out long value) || value <= 0)
            return text;

        long kb = value;
        if (string.Equals(_compressionUnit, "mb", StringComparison.Ordinal))
        {
            if (value > CompressionPolicy.MaxTargetKb) return (CompressionPolicy.MaxTargetKb + 1).ToString();
            kb = value * 1024L;
        }

        return kb > CompressionPolicy.MaxTargetKb
            ? (CompressionPolicy.MaxTargetKb + 1).ToString()
            : kb.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 這一格要不要進存檔的欄位清單。
    ///
    /// **模式不是「壓縮到」時就不送**：那個值對引擎沒有意義，而送它進去只會讓一個
    /// 被停用的文字框裡殘留的字（例如使用者打了半個數字又改回「標準」）擋住整筆存檔。
    /// 不送＝保留設定檔裡原本的值，也就是「你沒動它」——這比「悄悄清成 0」誠實。
    /// </summary>
    private bool ShouldSendCompressionTarget =>
        string.Equals(_compressionPreset, CompressionPolicy.ModeTargetSize, StringComparison.Ordinal);

    // =====================================================================================
    // 開關與滑桿
    // =====================================================================================

    private void OnStartupToggleClick(object sender, RoutedEventArgs e) =>
        StartupToggleClick(StartupToggle.IsChecked == true);

    private void StartupToggleClick(bool on)
    {
        SettingsUi.StyleKit.SetToggleState(StartupToggle, on);
        ApplyToggleLanguage(StartupToggle, on);
        MarkDirty();
    }

    private void OnDiagnosticsToggleClick(object sender, RoutedEventArgs e)
    {
        bool on = DiagnosticsToggle.IsChecked == true;
        SettingsUi.StyleKit.SetToggleState(DiagnosticsToggle, on);
        ApplyToggleLanguage(DiagnosticsToggle, on);
        MarkDirty();
    }

    private void OnHotkeyToggleClick(object sender, RoutedEventArgs e)
    {
        bool on = HotkeyToggle.IsChecked == true;
        SettingsUi.StyleKit.SetToggleState(HotkeyToggle, on);
        ApplyToggleLanguage(HotkeyToggle, on);
        HotkeyBox.IsEnabled = on;
        MarkDirty();
    }

    private void OnHotkeyTextChanged(object sender, TextChangedEventArgs e) => MarkDirty();

    private void OnValueSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateQualityLabels();
        MarkDirty();
    }

    // =====================================================================================
    // 儲存
    // =====================================================================================

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!BuildFromUi(out Settings next, out string? badKey, out string? badReason))
        {
            SetStatus("status.badField", isError: true,
                      UiStrings.Get($"field.{badKey}", _language), badReason ?? "");
            Report($"[settings] save blocked: {badKey} rejected ({badReason})");
            return;
        }

        try
        {
            SettingsStore.Save(next, _path, Report);
        }
        catch (Exception ex)
        {
            // 存檔失敗**一定要說出來**（SettingsStore.Save 已經先寫過一則，這裡再給使用者看的）
            SetStatus("status.saveFailed", isError: true, $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        _saved = next;
        _triggerKey = next.TriggerKey;
        _hasSaved = true;

        try
        {
            _onSaved?.Invoke(next);
        }
        catch (Exception ex)
        {
            // 呼叫端的回呼壞了，不該讓「已經存好的設定」看起來像存檔失敗
            SetStatus("status.saveFailed", isError: true,
                      $"onSaved callback threw {ex.GetType().Name}: {ex.Message}");
            Report($"[settings] onSaved callback threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        SetStatus("status.saved", isError: false, SettingsStore.Describe(next));
        RefreshTrigger();
    }

    /// <summary>
    /// 把一份設定推回畫面控制項上（<see cref="SettingsUi.SelfTestSave"/> 的勾子入口）。
    ///
    /// 這是「值進 UI」的**單一擁有者**：勾子只能經由它改變畫面，
    /// 之後仍然按真正的「儲存」按鈕，存檔路徑與真人操作完全相同——
    /// 勾子自己再寫一份存檔邏輯就會產生第二個權威，那正是這類測試勾子最容易騙人的地方。
    /// 一定要在 UI 執行緒上呼叫（<see cref="SettingsUi"/> 會負責排隊）。
    /// </summary>
    internal void ApplyEdited(Settings edited)
    {
        _language = edited.Language;        _palette = Palettes.CurrentId;     // 語言是下拉的狀態，LoadFromSettings 不含它
        LoadFromSettings(edited);        // 與開啟時「載入現值」走同一條路
        ApplyLanguage();                 // 文字重新指派（語言可能變了）
    }

    /// <summary>使用者真的要這份設定（給呼叫端在關窗後取用）。</summary>
    internal Settings SavedSettings => _saved;

    /// <summary>這個視窗正在編輯哪一個檔案（勾子回報證據時要說出來）。</summary>
    internal string SettingsPath => _path;

    /// <summary>這次開啟期間有沒有成功儲存過。</summary>
    internal bool HasSaved => _hasSaved;

    /// <summary>這一輪的世代編號（<see cref="SettingsUi"/> 用來判斷「已經被取代」）。</summary>
    internal long Generation => _generation;

    /// <summary>
    /// 把 UI 上的值組成一份新設定。**每一個欄位都走 <see cref="SettingsStore.TryApply"/>**
    /// （鍵→欄位的唯一擁有者），不是自己 switch。任何一項被拒絕就整筆不存，並指名道姓。
    /// </summary>
    private bool BuildFromUi(out Settings next, out string? badKey, out string? badReason)
    {
        next = _saved;
        badKey = null;
        badReason = null;

        bool hotkeyOn = HotkeyToggle.IsChecked == true;
        List<KeyValuePair<string, string>> fields = new()
        {
            new("triggerKey", _triggerKey),
            new("startWithWindows", (StartupToggle.IsChecked == true).ToString()),
            new("language", _language),            new("palette", _palette),
            new("jpgQuality", ((int)Math.Round(JpgSlider.Value)).ToString()),
            new("webpQuality", ((int)Math.Round(WebpSlider.Value)).ToString()),
            new("captureTimeoutSeconds", ((int)Math.Round(TimeoutSlider.Value)).ToString()),
            new("emergencyHotkey", hotkeyOn ? HotkeyBox.Text.Trim() : ""),
            new("diagnostics", (DiagnosticsToggle.IsChecked == true).ToString()),
            new("outputFolder", OutputValueFromUi()),
            // 壓縮方式（M3 第三顆扇形）。模式的四選一由下拉決定，寫回檔案的是正規寫法；
            // 上限只在「壓縮到」時才送（見 ShouldSendCompressionTarget）。
            new("compressionMode", _compressionPreset)
        };
        if (ShouldSendCompressionTarget)
            fields.Add(new("compressionTargetKb", CompressionTargetValueFromUi()));

        foreach (KeyValuePair<string, string> field in fields)
        {
            if (SettingsStore.TryApply(next, field.Key, field.Value, Report, out Settings updated))
            {
                next = updated;
                continue;
            }

            badKey = field.Key;
            badReason = field.Value.Length == 0 ? "<empty>" : field.Value;
            return false;
        }

        return true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    // =====================================================================================
    // 狀態列
    // =====================================================================================

    private void MarkDirty()
    {
        if (_statusKey == "status.dirty") return;
        SetStatus("status.dirty", isError: false);
    }

    private void SetStatus(string key, bool isError, params object[] args)
    {
        _statusKey = key;
        _statusIsError = isError;
        _statusArgs = args ?? Array.Empty<object>();
        RenderStatus();
    }

    /// <summary>只重畫狀態列——語言切換時會連 <see cref="ApplyLanguage"/> 一起重畫。</summary>
    private void RenderStatus()
    {
        StatusText.Text = _statusArgs.Length == 0
            ? UiStrings.Get(_statusKey, _language)
            : string.Format(UiStrings.Get(_statusKey, _language), _statusArgs);
        StatusText.Foreground = _statusIsError ? SettingsUi.StyleKit.FailFg : SettingsUi.StyleKit.TextDim;
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (!_focusPending) return;
        _focusPending = false;
        CaptureButton.Focus();
    }

    // =====================================================================================
    // 診斷
    // =====================================================================================

    /// <summary>
    /// 寫證據。<paramref name="_log"/> 由呼叫端提供、可為 null；
    /// **絕不因為日誌失敗而中斷 UI**（那是外部程式碼）。
    /// </summary>
    private void Report(string message)
    {
        try
        {
            _log?.Invoke(message);
        }
        catch (Exception)
        {
            // 呼叫端的日誌壞了，設定頁仍然要能用
        }
    }
}
