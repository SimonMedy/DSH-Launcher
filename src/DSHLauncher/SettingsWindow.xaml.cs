using System.IO;
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
        ContentGrid.SizeChanged += (_, _) =>
        {
            ContentGrid.Clip = new RectangleGeometry(
                new Rect(0, 0, ContentGrid.ActualWidth, ContentGrid.ActualHeight),
                18, 18);
        };

        PopulateFields();

        if (!string.IsNullOrWhiteSpace(_configService.LastRecoveryWarning))
        {
            MessageBox.Show(
                _configService.LastRecoveryWarning,
                "DSH Launcher - Configuration recovery",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void PopulateFields()
    {
        TrustedHostsTextBox.Text = _config.TrustedHosts is { Count: > 0 }
            ? string.Join(Environment.NewLine, _config.TrustedHosts)
            : string.Empty;
        CustomArgsTextBox.Text = _config.CustomArgs ?? string.Empty;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var rawHosts = TrustedHostsTextBox.Text
            .Split(new[] { "\r\n", "\r", "\n", ",", ";", " " }, StringSplitOptions.RemoveEmptyEntries)
            .Select(h => h.Trim())
            .Where(h => !string.IsNullOrWhiteSpace(h) && !h.StartsWith("#"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var normalizedHosts = new List<string>();
        foreach (var host in rawHosts)
        {
            if (!TrustedAuthority.TryNormalize(host, out var normalized, out var error))
            {
                MessageBox.Show(error, "Invalid trusted authority", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            normalizedHosts.Add(normalized);
        }

        var customArgs = CustomArgsTextBox.Text.Trim();
        if (!CommandLineTokenizer.TryTokenize(customArgs, out _, out var customArgsError))
        {
            MessageBox.Show(customArgsError, "Invalid additional arguments", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var nextConfig = new LauncherConfig
        {
            TrustedHosts = normalizedHosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CustomArgs = customArgs
        };

        try
        {
            _configService.Save(nextConfig);
            _config = nextConfig;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"The configuration could not be saved. The previous settings are still active.\n\n{ex.Message}",
                "DSH Launcher - Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OpenConfigFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _configService.OpenConfigFile();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to open config.json", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadHighResIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/DeepSeekHarness.ico", UriKind.Absolute);
            var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var highResFrame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            if (highResFrame is not null)
            {
                AppIconImage.Source = highResFrame;
            }
        }
        catch
        {
            // Keep fallback icon.
        }
    }
}
