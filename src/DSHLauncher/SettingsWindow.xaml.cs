using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DSHLauncher.Services;

namespace DSHLauncher;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private LauncherConfig _config;

    public SettingsWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;
        _config = _configService.Load();
        LoadHighResIcon();

        ContentGrid.SizeChanged += (_, _) => ContentGrid.Clip = new RectangleGeometry(new Rect(0, 0, ContentGrid.ActualWidth, ContentGrid.ActualHeight), 18, 18);
        PopulateFields();

        if (_configService.LastLoadWarning is not null)
        {
            MessageBox.Show(this, _configService.LastLoadWarning, "Configuration recovered", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PopulateFields()
    {
        TrustedHostsTextBox.Text = _config.TrustedHosts is { Count: > 0 } ? string.Join(Environment.NewLine, _config.TrustedHosts) : string.Empty;
        CustomArgsTextBox.Text = _config.CustomArgs ?? string.Empty;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var normalizedHosts = new List<string>();
            var rawHosts = TrustedHostsTextBox.Text
                .Split(new[] { "\r\n", "\r", "\n", ",", ";", " " }, StringSplitOptions.RemoveEmptyEntries)
                .Select(h => h.Trim())
                .Where(h => !string.IsNullOrWhiteSpace(h) && !h.StartsWith('#'));

            foreach (var rawHost in rawHosts)
            {
                if (!TrustedAuthority.TryNormalize(rawHost, out var authority, out var error))
                    throw new InvalidOperationException($"Invalid trusted authority '{rawHost}': {error}");
                if (!normalizedHosts.Contains(authority, StringComparer.OrdinalIgnoreCase)) normalizedHosts.Add(authority);
            }

            var customArgs = CustomArgsTextBox.Text.Trim();
            _ = WindowsCommandLine.ParseAdditionalArguments(customArgs);

            _config.TrustedHosts = normalizedHosts;
            _config.CustomArgs = customArgs;
            _configService.Save(_config);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Settings were not saved", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void OpenConfigFileButton_Click(object sender, RoutedEventArgs e)
    {
        try { _configService.OpenConfigFile(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to open config.json", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void LoadHighResIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/DeepSeekHarness.ico", UriKind.Absolute);
            var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var highResFrame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            if (highResFrame is not null) AppIconImage.Source = highResFrame;
        }
        catch { }
    }
}
