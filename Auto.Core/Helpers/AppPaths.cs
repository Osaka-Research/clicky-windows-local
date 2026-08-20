namespace Auto.Helpers;

/// <summary>
/// The app-data folder name used for settings, logs, and the cached Whisper model.
/// Each platform entry point sets this once, before constructing anything that logs or
/// touches disk. Deliberately different per platform (Windows: "Auto", Mac: "AutoMac",
/// server: "AutoServer") rather than one shared name -- the Mac (Avalonia) and server
/// builds both run fine on Windows too, which is how they've actually been built and
/// tested this whole project (no Mac/Linux host available) -- so all three need separate
/// folders to coexist on the same dev machine without clobbering each other's
/// settings.json/model cache.
/// </summary>
public static class AppPaths
{
    public static string AppFolderName { get; set; } = "Auto";
}
