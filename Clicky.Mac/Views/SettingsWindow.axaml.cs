using Avalonia.Controls;
using Avalonia.Interactivity;
using ClickyWindows.Settings;

namespace ClickyMac.Views;

/// <summary>
/// Minimal settings dialog -- Avalonia port of the WPF SettingsWindow, same fields.
/// Not modal (Avalonia's ShowDialog needs an owner window, and this app has none by
/// design -- it's tray-only); callers await WaitForCloseAsync() instead and check Saved.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TaskCompletionSource _closed = new();

    public bool Saved { get; private set; }

    // Parameterless overload only so Avalonia's XAML tooling (hot reload/previewer) can
    // instantiate this type -- actual usage always goes through the constructor below.
    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ApiKeyBox.Text = settings.AnthropicApiKey;
        ModelBox.Text = settings.ClaudeModel;
        ProxyUrlBox.Text = settings.ClaudeProxyUrl;
        SystemAudioDeviceBox.Text = settings.SystemAudioInputDeviceName;

        foreach (var item in WhisperModelBox.Items)
        {
            if (item is ComboBoxItem { Content: string content } && content == settings.WhisperModelSize)
            {
                WhisperModelBox.SelectedItem = item;
                break;
            }
        }
        WhisperModelBox.SelectedItem ??= WhisperModelBox.Items[7]; // medium.en

        Closed += (_, _) => _closed.TrySetResult();
    }

    public Task WaitForCloseAsync() => _closed.Task;

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _settings.AnthropicApiKey = ApiKeyBox.Text?.Trim() ?? "";
        _settings.ClaudeModel = string.IsNullOrWhiteSpace(ModelBox.Text) ? _settings.ClaudeModel : ModelBox.Text.Trim();
        _settings.ClaudeProxyUrl = ProxyUrlBox.Text?.Trim() ?? "";
        _settings.SystemAudioInputDeviceName = SystemAudioDeviceBox.Text?.Trim() ?? "";
        if (WhisperModelBox.SelectedItem is ComboBoxItem { Content: string size })
            _settings.WhisperModelSize = size;

        _settings.Save();
        Saved = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
