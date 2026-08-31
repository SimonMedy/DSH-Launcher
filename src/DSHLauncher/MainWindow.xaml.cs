using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DSHLauncher.Services;
using Forms = System.Windows.Forms;

namespace DSHLauncher;

public partial class MainWindow : Window
{
    private readonly HarnessService _harness;
    private bool _actionInProgress;

    public MainWindow(HarnessService harness)
    {
        InitializeComponent();
        LoadHighResIcon();
        ContentGrid.SizeChanged += (_, _) =>
        {
            ContentGrid.Clip = new RectangleGeometry(
                new Rect(0, 0, ContentGrid.ActualWidth, ContentGrid.ActualHeight),
                18, 18);
        };
        _harness = harness;
        _harness.StateChanged += Harness_StateChanged;
        _harness.StatusMessageChanged += Harness_StatusMessageChanged;
        UpdateState(_harness.State);
    }

    public void ShowNearTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();

        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var cursorDip = transform.Transform(new System.Windows.Point(cursor.X, cursor.Y));
        var workTopLeft = transform.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var workBottomRight = transform.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));

        var desiredLeft = cursorDip.X - ActualWidth + 40;
        var desiredTop = cursorDip.Y - ActualHeight;
        Left = Math.Clamp(desiredLeft, workTopLeft.X - 4, workBottomRight.X - ActualWidth + 4);
        Top = Math.Clamp(desiredTop, workTopLeft.Y - 4, workBottomRight.Y - ActualHeight + 4);

        Activate();
    }

    private void Harness_StateChanged(object? sender, HarnessState state)
    {
        Dispatcher.Invoke(() => UpdateState(state));
    }

    private void Harness_StatusMessageChanged(object? sender, string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
        });
    }

    private void UpdateState(HarnessState state)
    {
        StatusText.Text = _harness.StatusMessage;
        StatusDot.Fill = state switch
        {
            HarnessState.Running => new SolidColorBrush(System.Windows.Media.Color.FromRgb(77, 214, 170)),
            HarnessState.Starting => new SolidColorBrush(System.Windows.Media.Color.FromRgb(88, 166, 255)),
            HarnessState.Failed => new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 107, 125)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(111, 138, 157))
        };
        var isBusy = _actionInProgress || state == HarnessState.Starting;

        OpenButton.IsEnabled = state == HarnessState.Running;
        UpdateButton.IsEnabled = !isBusy;
        RestartButton.IsEnabled = !isBusy;
        SettingsButton.IsEnabled = !isBusy;
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_actionInProgress)
        {
            Hide();
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        _harness.OpenWebInterface();
        Hide();
    }

    private void HarnessLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _harness.OpenHarnessLogs();
        Hide();
    }

    private void LauncherLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _harness.OpenLauncherLogs();
        Hide();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        var settingsWindow = new SettingsWindow(_harness.Config);
        if (settingsWindow.ShowDialog() == true)
        {
            if (!_actionInProgress)
            {
                _actionInProgress = true;
                RestartButton.IsEnabled = false;
                UpdateButton.IsEnabled = false;

                try
                {
                    await _harness.RestartAsync();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        $"DeepSeek Harness could not be restarted with the new settings.\n\n{ex.Message}",
                        "DSH Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    _actionInProgress = false;
                    UpdateState(_harness.State);
                }
            }
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress)
        {
            return;
        }

        _actionInProgress = true;
        UpdateState(_harness.State);

        try
        {
            await _harness.UpdateAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"DeepSeek Harness could not be updated.\n\n{ex.Message}",
                "DSH Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _actionInProgress = false;
            UpdateState(_harness.State);
        }
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress)
        {
            return;
        }

        _actionInProgress = true;
        RestartButton.IsEnabled = false;

        try
        {
            await _harness.RestartAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"DeepSeek Harness could not be restarted.\n\n{ex.Message}",
                "DSH Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _actionInProgress = false;
            UpdateState(_harness.State);
            Hide();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress || _harness.State == HarnessState.Starting)
        {
            System.Windows.MessageBox.Show(
                "DeepSeek Harness is currently starting, installing, updating, or restarting.\n\n" +
                "Wait for the current operation to finish before exiting. DSH Launcher will not interrupt " +
                "an npm installation because doing so could leave the global Harness installation incomplete.",
                "DSH Launcher - Operation in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Hide();

        try
        {
            await _harness.StopAsync();
        }
        catch
        {
            // Ignore errors only after no package/start operation is in progress,
            // so normal shutdown can still proceed.
        }

        System.Windows.Application.Current.Shutdown();
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
            // Keep default XAML source on failure
        }
    }
}
