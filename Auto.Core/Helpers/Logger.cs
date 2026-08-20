namespace ClickyWindows.Helpers;

/// <summary>
/// Simple file logger. Writes timestamped lines to the app-data folder (named via
/// AppPaths.AppFolderName) \ auto.log. Also calls an optional UI callback (for
/// tray balloon tips on errors).
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string? _logPath;

    public static Action<string>? OnError;   // hooked by App to show balloon tip
    public static Action<string>? OnInfo;    // hooked by App to show balloon tip

    // Lazy, not a static constructor: AppPaths.AppFolderName must be set by the
    // platform entry point (Windows: "Auto", Mac: "AutoMac", server: "AutoServer")
    // before the log file location is decided.
    private static string LogPath
    {
        get
        {
            if (_logPath == null)
            {
                _logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    AppPaths.AppFolderName, "auto.log");
                try
                {
                    var dir = Path.GetDirectoryName(_logPath)!;
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(_logPath, $"=== Auto log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                }
                catch { }
            }
            return _logPath;
        }
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
