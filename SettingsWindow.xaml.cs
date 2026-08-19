using System.Windows;
using System.Windows.Controls;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// In-app settings editor — replaces asking the user to hand-edit settings.json in
/// Notepad. Shown on first run (blocking, until a valid Anthropic key is entered) and
/// reachable any time after via the tray menu's "Settings..." item.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>True if the user saved (vs. cancelled/closed).</summary>
    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ApiKeyBox.Password = _settings.AnthropicApiKey;
        ModelBox.Text = _settings.ClaudeModel;
        ProxyUrlBox.Text = _settings.ClaudeProxyUrl;

        var sizeToSelect = string.IsNullOrWhiteSpace(_settings.WhisperModelSize) ? "base.en" : _settings.WhisperModelSize;
        foreach (ComboBoxItem item in WhisperSizeBox.Items)
        {
            if ((string)item.Content == sizeToSelect)
            {
                WhisperSizeBox.SelectedItem = item;
                break;
            }
        }
        WhisperSizeBox.SelectedItem ??= WhisperSizeBox.Items
            .Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Content == "base.en") ?? WhisperSizeBox.Items[0];
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            System.Windows.MessageBox.Show(this, "Anthropic API key is required.", "Clicky Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.AnthropicApiKey = key;
        _settings.ClaudeModel = string.IsNullOrWhiteSpace(ModelBox.Text) ? "claude-sonnet-4-6" : ModelBox.Text.Trim();
        _settings.ClaudeProxyUrl = ProxyUrlBox.Text.Trim();
        _settings.WhisperModelSize = (WhisperSizeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "base.en";
        _settings.Save();

        Saved = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Saved = false;
        DialogResult = false;
        Close();
    }
}
