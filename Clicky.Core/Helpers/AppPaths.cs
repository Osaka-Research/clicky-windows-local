namespace ClickyWindows.Helpers;

/// <summary>
/// The app-data folder name used for settings, logs, and the cached Whisper model.
/// Each platform entry point sets this once, before constructing anything that logs
/// or touches disk. Defaults to the Windows app's existing folder name so the current
/// Windows install (live settings.json, cached model) keeps working unchanged; the
/// Mac entry point overrides it to "Clicky" before touching any Core service.
/// </summary>
public static class AppPaths
{
    public static string AppFolderName { get; set; } = "ClickyWindowsLocal";
}
