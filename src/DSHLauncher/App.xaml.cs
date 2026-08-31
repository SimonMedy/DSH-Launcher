using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows;
using DSHLauncher.Services;
using Forms = System.Windows.Forms;

namespace DSHLauncher;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\DSHLauncher";

    private Mutex? _singleInstanceMutex;
    private Forms.NotifyIcon? _trayIcon;
    private HarnessService? _harness;
    private MainWindow? _popup;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            OpenPublishedHarnessInBrowser();
            Shutdown();
            return;
        }

        try
        {
            _harness = new HarnessService();
            _popup = new MainWindow(_harness);

            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The launcher executable path is unavailable.");
            var appIcon = Icon.ExtractAssociatedIcon(executablePath)
                ?? throw new InvalidOperationException("The launcher icon could not be loaded.");

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = appIcon,
                Text = "DeepSeek Harness",
                Visible = true
            };

            _trayIcon.MouseUp += (_, args) =>
            {
                if (args.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right)
                {
                    Dispatcher.Invoke(TogglePopup);
                }
            };

            _trayIcon.DoubleClick += (_, _) => _harness.OpenWebInterface();

            _harness.StateChanged += (_, _) => Dispatcher.Invoke(UpdateTrayText);
            _harness.StatusMessageChanged += (_, _) => Dispatcher.Invoke(UpdateTrayText);

            await _harness.StartAsync(openBrowserWhenReady: false);

            if (!string.IsNullOrWhiteSpace(_harness.Config.LastRecoveryWarning))
            {
                System.Windows.MessageBox.Show(
                    _harness.Config.LastRecoveryWarning,
                    "DSH Launcher - Configuration recovery",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"DSH Launcher could not start.\n\n{ex.Message}",
                "DSH Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown();
        }
    }

    private void TogglePopup()
    {
        if (_popup is null)
        {
            return;
        }

        if (_popup.IsVisible)
        {
            _popup.Hide();
            return;
        }

        _popup.ShowNearTray();
    }

    private void UpdateTrayText()
    {
        if (_trayIcon is null || _harness is null)
        {
            return;
        }

        var text = $"DeepSeek Harness - {_harness.StatusMessage}";
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private static void OpenPublishedHarnessInBrowser()
    {
        try
        {
            if (!HarnessService.TryGetPublishedWebUrl(out var webUrl))
            {
                System.Windows.MessageBox.Show(
                    "DSH Launcher is already running, but its web interface is either not ready yet or " +
                    "requires an authenticated browser handoff. Use the running launcher's tray popup to open it safely.",
                    "DSH Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = webUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // The running instance remains available from the tray.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
        }

        _harness?.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // Ignore release errors during shutdown.
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
