using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public string WhisperModelSize { get; set; } = "base.en";

    // Action mode push-to-talk — default: Ctrl+Shift+Space. Sends a screenshot with the
    // question; Claude can point at (or, in clicky-android, tap) something on screen.
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0004; // MOD_CONTROL | MOD_SHIFT
    public uint HotkeyVirtualKey { get; set; } = 0x20; // VK_SPACE

    // Answer mode push-to-talk — default: Ctrl+Shift+A. Pure Q&A, no screenshot is
    // captured or sent at all -- for when you don't want the current screen shared.
    public uint AnswerHotkeyModifiers { get; set; } = 0x0002 | 0x0004; // MOD_CONTROL | MOD_SHIFT
    public uint AnswerHotkeyVirtualKey { get; set; } = 0x41; // VK_A

    // Overlay settings
    public bool ShowCursorOverlay { get; set; } = true;

    // ── Persistence ────────────────────────────────────────────────────────

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClickyWindowsLocal",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

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
