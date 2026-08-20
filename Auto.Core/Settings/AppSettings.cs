using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClickyWindows.Helpers;

namespace ClickyWindows.Settings;

public class AppSettings
{
    // Only remaining cloud dependency — Claude does the actual seeing/reasoning/pointing.
    public string AnthropicApiKey { get; set; } = "";

    // Optional Cloudflare proxy URL (leave empty to call Anthropic directly)
    public string ClaudeProxyUrl { get; set; } = "";

    // Claude model
    public string ClaudeModel { get; set; } = "claude-sonnet-4-6";

    // Local Whisper model size: tiny | base | small | medium. Bigger = more accurate,
    // slower, more RAM. Downloaded once on first run and cached under whisper-models/.
    // Ignored when UseRemoteServer is true -- the server picks its own model size.
    public string WhisperModelSize { get; set; } = "medium.en";

    // Remote inference: run Whisper transcription and the Claude call on an Auto.Server
    // instance instead of on this machine -- for a client with no GPU (local Whisper would
    // be too slow to feel live) and/or no Anthropic key of its own. When true,
    // AnthropicApiKey/ClaudeModel/ClaudeProxyUrl/WhisperModelSize above are all unused;
    // the server's own settings decide those. See Auto.Core/Services/RemoteInferenceBackend.cs.
    public bool UseRemoteServer { get; set; } = false;
    public string RemoteServerUrl { get; set; } = "";   // e.g. "https://clicky.example.com"
    public string RemoteServerToken { get; set; } = ""; // must match the server's ServerAuthToken

    // Server-only: the shared secret clients must send as "Authorization: Bearer <token>".
    // Unused by Windows/Mac clients -- only Auto.Server reads this field, from its own
    // separate settings.json (see AppPaths.AppFolderName = "ClickyServer" in its Program.cs).
    public string ServerAuthToken { get; set; } = "";

    // Hotkey field values are raw platform key codes -- each platform's HotkeyService
    // interprets HotkeyModifiers/HotkeyVirtualKey using its own OS conventions (Win32
    // MOD_*/VK_* on Windows, Carbon modifier masks/keycodes on macOS). Settings live in
    // separate per-platform app-data folders (see AppPaths), so the same field names
    // safely carry different meanings on each platform. The literal defaults below are
    // Windows' (Ctrl+Shift+1-4, Win32 codes) -- the Mac entry point overwrites them with
    // macOS-appropriate values on that platform's first-ever run, see Program.cs there.
    //
    // Number-key combos (1-4) rather than letters/Space: far less likely to already be
    // claimed by some other running app's global shortcut than common combos like
    // Ctrl+Shift+Space or Ctrl+Shift+A -- a real conflict crashed the Windows app once.

    // Action mode push-to-talk — default: Ctrl+Shift+1. Sends a screenshot with the
    // question; Claude can point at (or, in clicky-android, tap) something on screen.
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0004; // Win32 MOD_CONTROL | MOD_SHIFT
    public uint HotkeyVirtualKey { get; set; } = 0x31; // Win32 VK_1

    // Answer mode push-to-talk — default: Ctrl+Shift+2. Pure Q&A, no screenshot is
    // captured or sent at all -- for when you don't want the current screen shared.
    public uint AnswerHotkeyModifiers { get; set; } = 0x0002 | 0x0004;
    public uint AnswerHotkeyVirtualKey { get; set; } = 0x32; // Win32 VK_2

    // System Audio mode push-to-talk — default: Ctrl+Shift+3. Listens to whatever's
    // currently playing through speakers (a call, a video) via loopback instead
    // of the microphone, transcribes that, and reacts to it. No screenshot either.
    public uint SystemAudioHotkeyModifiers { get; set; } = 0x0002 | 0x0004;
    public uint SystemAudioHotkeyVirtualKey { get; set; } = 0x33; // Win32 VK_3

    // Screenshot Q&A — default: Ctrl+Shift+4. A single tap (no hold, no mic) captures one
    // screenshot and asks Claude to answer every question visible in it.
    public uint ScreenshotQaHotkeyModifiers { get; set; } = 0x0002 | 0x0004;
    public uint ScreenshotQaHotkeyVirtualKey { get; set; } = 0x34; // Win32 VK_4

    // macOS only: the CoreAudio input device to use for System Audio mode -- there's no
    // native loopback API, so this points at a virtual device (e.g. "BlackHole 2ch")
    // instead of the real microphone. Empty/unset on Windows, where loopback is native.
    public string SystemAudioInputDeviceName { get; set; } = "";

    // Overlay settings
    public bool ShowCursorOverlay { get; set; } = true;

    // ── Persistence ────────────────────────────────────────────────────────

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppPaths.AppFolderName,
        "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static bool Exists() => File.Exists(SettingsPath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { /* ignore, use defaults */ }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* ignore */ }
    }

    // Convenience: which API to call for Claude
    public string ClaudeApiUrl =>
        !string.IsNullOrWhiteSpace(ClaudeProxyUrl)
            ? ClaudeProxyUrl
            : "https://api.anthropic.com/v1/messages";
}
