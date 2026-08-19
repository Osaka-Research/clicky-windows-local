namespace ClickyWindows.Helpers;

/// <summary>
/// Simple file logger. Writes timestamped lines to %APPDATA%\ClickyWindows\clicky.log.
/// Also calls an optional UI callback (for tray balloon tips on errors).
/// </summary>
public static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClickyWindowsLocal", "clicky.log");

    private static readonly object _lock = new();

    public static Action<string>? OnError;   // hooked by App to show balloon tip
    public static Action<string>? OnInfo;    // hooked by App to show balloon tip

    static Logger()
    {
        // Ensure directory exists and truncate old log on startup
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(LogPath, $"=== Clicky log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch { }
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { }
        }
    }

    public static void Info(string message)
    {
        Log($"[INFO] {message}");
        OnInfo?.Invoke(message);
    }

    public static void Error(string message)
    {
        Log($"[ERROR] {message}");
        OnError?.Invoke(message);
    }

    public static string LogFilePath => LogPath;
}
