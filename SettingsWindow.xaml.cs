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
        UseRemoteServerBox.IsChecked = _settings.UseRemoteServer;
        RemoteServerUrlBox.Text = _settings.RemoteServerUrl;
        RemoteServerTokenBox.Password = _settings.RemoteServerToken;
        RemoteServerPanel.Visibility = _settings.UseRemoteServer ? Visibility.Visible : Visibility.Collapsed;

        var sizeToSelect = string.IsNullOrWhiteSpace(_settings.WhisperModelSize) ? "medium.en" : _settings.WhisperModelSize;
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
            .FirstOrDefault(i => (string)i.Content == "medium.en") ?? WhisperSizeBox.Items[0];
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        bool useRemote = UseRemoteServerBox.IsChecked == true;
        var key = ApiKeyBox.Password.Trim();
        var remoteUrl = RemoteServerUrlBox.Text.Trim();

        if (!useRemote && string.IsNullOrWhiteSpace(key))
        {
            System.Windows.MessageBox.Show(this,
                "Either an Anthropic API key or a remote server URL is required.", "Clicky Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (useRemote && string.IsNullOrWhiteSpace(remoteUrl))
        {
            System.Windows.MessageBox.Show(this, "Server URL is required when using a remote server.",
                "Clicky Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.AnthropicApiKey = key;
        _settings.ClaudeModel = string.IsNullOrWhiteSpace(ModelBox.Text) ? "claude-sonnet-4-6" : ModelBox.Text.Trim();
        _settings.ClaudeProxyUrl = ProxyUrlBox.Text.Trim();
        _settings.WhisperModelSize = (WhisperSizeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "medium.en";
        _settings.UseRemoteServer = useRemote;
        _settings.RemoteServerUrl = remoteUrl;
        _settings.RemoteServerToken = RemoteServerTokenBox.Password.Trim();
        _settings.Save();

        Saved = true;
        DialogResult = true;
        Close();
    }

    private void OnRemoteServerToggled(object sender, RoutedEventArgs e)
    {
        RemoteServerPanel.Visibility = UseRemoteServerBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Saved = false;
        DialogResult = false;
        Close();
    }
}
