using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Media;
using System.Windows;
using FocusGate.Desktop.Data;
using Microsoft.Extensions.DependencyInjection;

namespace FocusGate.Desktop.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IServiceProvider _services;
    private bool _isMuted = false;

    public bool IsMuted => _isMuted;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        var machineId = App.MachineId;
        MachineIdText.Text = string.IsNullOrEmpty(machineId) ? "" : $"Machine: {machineId[..8]}...";

        ContentFrame.Navigated += ContentFrame_Navigated;
        NavigateToOverview();

        Closing += OnClosing;
    }

    public void NavigateToOverview()
    {
        var page = new ModemsOverviewPage(_services, this);
        ContentFrame.Navigate(page);
    }

    public void NavigateToDetail(int modemId)
    {
        var page = new ModemDetailPage(_services, this, modemId);
        ContentFrame.Navigate(page);
    }

    public void PlayNotificationSound()
    {
        if (_isMuted) return;
        try
        {
            SystemSounds.Beep.Play();
        }
        catch { }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        MuteButton.Icon = _isMuted
            ? new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.SpeakerOff24)
            : new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.SpeakerMute24);
        MuteButton.Foreground = _isMuted
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xef, 0x44, 0x44))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xb9, 0x81));
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Restart FocusGate Hardware?\n\nThe Hardware service will shut down and restart. This may take a few seconds.",
            "Restart Hardware",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            await using var client = new NamedPipeClientStream(".", "FocusGate_Restart", PipeDirection.Out);
            await client.ConnectAsync(5000);

            await using var writer = new StreamWriter(client);
            await writer.WriteLineAsync("restart");
            await writer.FlushAsync();

            var exeDir = AppContext.BaseDirectory;
            var hardwarePath = Path.Combine(exeDir, "FocusGate.exe");
            if (!File.Exists(hardwarePath))
                hardwarePath = Path.Combine(exeDir, "..", "FocusGate.exe");

            if (File.Exists(hardwarePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = hardwarePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(hardwarePath)!
                });
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not restart Hardware:\n{ex.Message}\n\nTry restarting manually.",
                "Restart Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Stop FocusGate?\n\nAll modem monitoring will stop. The system will shut down safely.",
            "Stop FocusGate",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            await using var client = new NamedPipeClientStream(".", "FocusGate_Restart", PipeDirection.Out);
            await client.ConnectAsync(5000);

            await using var writer = new StreamWriter(client);
            await writer.WriteLineAsync("stop");
            await writer.FlushAsync();

            await Task.Delay(1000);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not stop FocusGate:\n{ex.Message}\n\nTry stopping manually.",
                "Stop Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ContentFrame_Navigated(object sender, System.Windows.Navigation.NavigationEventArgs e)
    {
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
