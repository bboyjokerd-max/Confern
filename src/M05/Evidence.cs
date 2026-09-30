namespace M05;

/// <summary>
/// 證據記錄。狀態列採「原地覆寫單行」，逐行事件寫入證據檔——兩者分離，
/// 避免長時執行時洗版（使用者的輸出慣例）。
/// 註：M05 是 WinExe（不留主控台視窗，否則會搶走前景焦點、破壞偵測），
/// 因此 Console 輸出在正常執行時是 no-op；證據以檔案為準。
/// </summary>
internal sealed class Evidence : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly bool _quiet;

    public string Path { get; }

    public Evidence(string path, bool quiet)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        _quiet = quiet;
        Line($"=== M05 session {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ===");
    }

    /// 逐行事件（可稽核的證據）
    public void Line(string text)
    {
        _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
        if (!_quiet) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
    }

    /// 單行狀態（原地覆寫，不洗版）
    public void Status(string text) => Console.Error.Write("\r" + text.PadRight(78));

    public void Dispose()
    {
        Line("=== session end ===");
        _writer.Dispose();
    }
}
