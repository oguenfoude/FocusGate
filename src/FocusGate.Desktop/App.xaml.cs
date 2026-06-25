using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using FocusGate.Core.Services;
using FocusGate.Desktop.Data;
using FocusGate.Desktop.Views;
using H.NotifyIcon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;

namespace FocusGate.Desktop;

public partial class App : Application
{
    public static string DbPath { get; private set; } = string.Empty;
    public static string MachineId { get; private set; } = string.Empty;
    private static Mutex? _mutex;
    public static TaskbarIcon? TrayIcon { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        var accentColor = Color.FromRgb(0x10, 0xb9, 0x81);
        ApplicationAccentColorManager.Apply(accentColor, ApplicationTheme.Dark);

        _mutex = new Mutex(true, @"Global\FocusGate_Desktop", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        TrayIcon = new TaskbarIcon
        {
            ToolTipText = "FocusGate",
            Visibility = Visibility.Visible
        };
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
                TrayIcon.Icon = new System.Drawing.Icon(iconPath);
        }
        catch { }
        TrayIcon.DoubleClickCommand = new DelegateCommand(() =>
        {
            var window = Current.MainWindow;
            if (window != null)
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }
        });
        var menu = new System.Windows.Controls.ContextMenu();
        var showItem = new System.Windows.Controls.MenuItem { Header = "Show Window" };
        showItem.Click += (s, e) =>
        {
            var window = Current.MainWindow;
            if (window != null)
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }
        };
        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += async (s, e) =>
        {
            var result = MessageBox.Show(
                "Exit FocusGate?\n\nAll modem monitoring will stop.",
                "Exit FocusGate",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await using var client = new System.IO.Pipes.NamedPipeClientStream(".", "FocusGate_Restart", System.IO.Pipes.PipeDirection.Out);
                    await client.ConnectAsync(2000);
                    await using var writer = new StreamWriter(client);
                    await writer.WriteLineAsync("stop");
                    await writer.FlushAsync();
                }
                catch { }
                await Task.Delay(1000);
                Current.Shutdown();
            }
        };
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exitItem);
        TrayIcon.ContextMenu = menu;

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            try
            {
                var logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] UNHANDLED: {ex}\n");
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            try
            {
                var logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] UNOBSERVED: {args.Exception}\n");
            }
            catch { }
            args.SetObserved();
        };

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                var logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] DISPATCHER: {args.Exception}\n");
            }
            catch { }
            args.Handled = true;
        };

        try
        {
            DbPath = PathService.DatabasePath;

            var splash = new LoadingWindow();
            splash.Show();

            int waited = 0;
            while (!File.Exists(DbPath) && waited < 15000)
            {
                await Task.Delay(500);
                waited += 500;
                splash.UpdateStatus($"Waiting for database... ({waited / 1000}s)");
            }

            if (!File.Exists(DbPath))
            {
                splash.Close();
                MessageBox.Show($"Database not found:\n{DbPath}\n\nPlease run FocusGate first.",
                    "FocusGate", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            splash.UpdateStatus("Loading configuration...");
            await Task.Delay(300);

            MachineId = ReadMachineId();

            splash.UpdateStatus("Connecting to database...");
            await Task.Delay(300);

            var services = new ServiceCollection();
            services.AddDbContext<ReadOnlyDbContext>(options =>
                options.UseSqlite($"Data Source={DbPath};Mode=ReadOnly;"));
            var provider = services.BuildServiceProvider();

            splash.UpdateStatus("Loading modem data...");
            await Task.Delay(300);

            var mainWindow = new Views.MainWindow(provider);
            splash.Close();
            mainWindow.Show();
            mainWindow.Activate();
            mainWindow.Topmost = true;
            mainWindow.Topmost = false;
        }
        catch (Exception ex)
        {
            try
            {
                var logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] ERROR: {ex}\n");
            }
            catch { }

            MessageBox.Show(
                $"FocusGate Desktop failed to start:\n\n{ex.Message}\n\n{ex.InnerException?.Message}",
                "FocusGate Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string ReadMachineId()
    {
        try
        {
            var configPath = PathService.ConfigPath;
            if (!File.Exists(configPath))
                return "";

            var json = File.ReadAllText(configPath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("machine.id", out var val))
                return val.GetString() ?? "";
        }
        catch { }
        return "";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TrayIcon?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

internal class DelegateCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public DelegateCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
