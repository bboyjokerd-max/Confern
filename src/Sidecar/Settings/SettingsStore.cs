namespace Sidecar;

/// <summary>
/// 設定（規格 §8）。預設值就是規格值——使用者不開設定檔也有正確行為。
/// 檔案位置：`%LOCALAPPDATA%\Sidecar\settings.json`。
/// </summary>
internal sealed record Settings
{
    public int JpgQuality { get; init; } = 85;              // 規格 §8：60–100
    public int WebpQuality { get; init; } = 85;
    public string TriggerKey { get; init; } = "Space";      // 空白鍵
    public bool StartWithWindows { get; init; } = true;
    public string Language { get; init; } = "system";
    public string NamingCollision { get; init; } = "auto-suffix";   // 固定 `(1)`，不詢問
    public int CaptureTimeoutSeconds { get; init; } = 30;   // 5–120
    public string EmergencyHotkey { get; init; } = "Ctrl+Alt+Shift+Q";
    public bool Diagnostics { get; init; } = false;

    /// <summary>把超出範圍的值夾回規格允許的區間，並回報改了什麼（不靜默）。</summary>
    public Settings Normalized(out List<string> notes)
    {
        notes = new List<string>();
        int jpg = Math.Clamp(JpgQuality, 60, 100);
        int webp = Math.Clamp(WebpQuality, 60, 100);
        int capture = Math.Clamp(CaptureTimeoutSeconds, 5, 120);
        if (jpg != JpgQuality) notes.Add($"JpgQuality {JpgQuality} -> {jpg} (spec 8: 60-100)");
        if (webp != WebpQuality) notes.Add($"WebpQuality {WebpQuality} -> {webp} (spec 8: 60-100)");
        if (capture != CaptureTimeoutSeconds) notes.Add($"CaptureTimeoutSeconds {CaptureTimeoutSeconds} -> {capture} (spec 8: 5-120)");
        return this with { JpgQuality = jpg, WebpQuality = webp, CaptureTimeoutSeconds = capture };
    }

    /// <summary>目標格式要用的編碼品質（只有 JPG／WebP 有意義）。</summary>
    public int QualityFor(string targetFormat) => targetFormat switch
    {
        "JPG" => JpgQuality,
        "WEBP" => WebpQuality,
        _ => 100
    };
}

internal static class SettingsStore
{
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sidecar", "settings.json");

    public static Settings Load(string? path, Action<string> log)
    {
        path ??= DefaultPath();
        Settings loaded;
        if (!File.Exists(path))
        {
            log($"settings: no file at {path}; using spec defaults (jpgQuality=85, trigger=Space, capture=30s)");
            return new Settings();
        }
        try
        {
            loaded = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch (Exception ex)
        {
            // 壞掉的設定檔**不可以**讓程式起不來，也不可以靜默忽略
            log($"settings: {path} is unreadable ({ex.GetType().Name}: {ex.Message}) -> using spec defaults");
            return new Settings();
        }

        Settings normalized = loaded.Normalized(out List<string> notes);
        foreach (string note in notes) log($"settings: clamped {note}");
        log($"settings: loaded from {path} jpgQuality={normalized.JpgQuality} " +
            $"webpQuality={normalized.WebpQuality} trigger={normalized.TriggerKey} " +
            $"capture={normalized.CaptureTimeoutSeconds}s diagnostics={normalized.Diagnostics}");
        return normalized;
    }
}
