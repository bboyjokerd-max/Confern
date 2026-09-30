namespace Sidecar;

/// <summary>
/// 證據記錄。狀態列採「原地覆寫單行」，逐行事件寫入證據檔——兩者分離，
/// 避免長時執行時洗版（使用者輸出慣例：預設安靜）。
/// 註：Sidecar 是 WinExe，一般常駐模式下 Console 輸出為 no-op；證據以檔案為準。
/// </summary>
internal sealed class Evidence : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly bool _quiet;

    public string Path { get; }

    public static string DefaultPath()
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sidecar", "logs");
        return System.IO.Path.Combine(dir, "sidecar.log");
    }

    public Evidence(string path, bool quiet)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        _quiet = quiet;
        Line($"=== Sidecar session {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ===");
    }

    /// <summary>逐行事件（可稽核的證據）。</summary>
    public void Line(string text)
    {
        _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
        if (!_quiet) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
    }

    /// <summary>單行狀態（原地覆寫，不洗版）。</summary>
    public void Status(string text) => Console.Error.Write("\r" + text.PadRight(78));

    public void Dispose()
    {
        Line("=== session end ===");
        _writer.Dispose();
    }
}
