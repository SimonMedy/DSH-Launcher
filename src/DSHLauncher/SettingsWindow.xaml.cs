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
    }

    private void PopulateFields()
    {
        if (_config.TrustedHosts is { Count: > 0 })
        {
            TrustedHostsTextBox.Text = string.Join(Environment.NewLine, _config.TrustedHosts);
        }
        else
        {
            TrustedHostsTextBox.Text = string.Empty;
        }

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

        _config.TrustedHosts = rawHosts;
        _config.CustomArgs = CustomArgsTextBox.Text.Trim();

        _configService.Save(_config);

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
        _configService.OpenConfigFile();
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
            // Keep fallback
        }
    }
}
