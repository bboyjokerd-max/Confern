namespace Sidecar;

/// <summary>
/// 長路徑（規格 §5：&gt; 260 字元要支援）。
///
/// 用 `\\?\` 前綴讓 Win32 直接接受超長路徑——比依賴 manifest 的 `longPathAware` 可靠：
/// 後者會隨使用者系統設定而變，前者不變。前綴要求**已完整正規化的絕對路徑**，
/// 而且一旦加上就不能再出現 `.`／`..`（`\\?\` 會關掉路徑正規化），所以一律先 `GetFullPath`。
/// </summary>
internal static class LongPath
{
    private const int MaxClassicPath = 260;

    public static string Prepare(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.Length < MaxClassicPath) return path;

        // UNC（\\server\share\…）要用 \\?\UNC\ 形式
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + Path.GetFullPath(path);
    }
}
