using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusGate.Core.Enums;
using FocusGate.Desktop.Data;
using FocusGate.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FocusGate.Desktop.Views;

public partial class ModemDetailPage : Page
{
    private readonly IServiceProvider _services;
    private readonly MainWindow _mainWindow;
    private readonly int _modemId;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private int _previousSmsCount = 0;

    public ModemDetailPage(IServiceProvider services, MainWindow mainWindow, int modemId)
    {
        InitializeComponent();
        _services = services;
        _mainWindow = mainWindow;
        _modemId = modemId;

        _refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (s, e) => await LoadDataAsync();

        Loaded += async (s, e) =>
        {
            await LoadDataAsync();
            _refreshTimer.Start();
        };

        Unloaded += (s, e) => _refreshTimer.Stop();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            using var db = scope.ServiceProvider.GetRequiredService<ReadOnlyDbContext>();

            var modem = await db.Modems.FirstOrDefaultAsync(m => m.Id == _modemId);

            var sims = await db.SimCards.Where(s => s.ModemId == _modemId).ToListAsync();
            if (modem != null) modem.SimCards = sims;

            var activeSim = modem?.SimCards.FirstOrDefault(s => s.IsActive);

            PhoneNumberText.Text = activeSim?.PhoneNumber != 0
                ? activeSim!.PhoneNumber.ToString()
                : "---";
            BalanceText.Text = $"{activeSim?.Balance ?? 0m:N2} DA";

            ImeiText.Text = modem?.IMEI ?? "---";

            bool isOnline = modem?.Status == ModemStatus.Online;
            StatusText.Text = isOnline ? "Online" : "Offline";
            StatusText.Foreground = isOnline
                ? (Brush)new BrushConverter().ConvertFromString("#10b981")!
                : (Brush)new BrushConverter().ConvertFromString("#71717a")!;
            StatusIndicator.Fill = isOnline
                ? (Brush)new BrushConverter().ConvertFromString("#10b981")!
                : (Brush)new BrushConverter().ConvertFromString("#52525b")!;

            var simIds = await db.SimCards
                .Where(s => s.ModemId == _modemId)
                .Select(s => s.Id)
                .ToListAsync();

            var smsItems = await db.SmsRecords
                .Where(s => simIds.Contains(s.SimCardId))
                .OrderByDescending(s => s.ReceivedAt)
                .Select(s => new SmsListItem
                {
                    Id = s.Id,
                    SenderNumber = s.SenderNumber,
                    Content = s.Content,
                    ReceivedAt = s.ReceivedAt
                })
                .ToListAsync();

            if (_previousSmsCount > 0 && smsItems.Count > _previousSmsCount)
            {
                _mainWindow.PlayNotificationSound();
            }
            _previousSmsCount = smsItems.Count;

            foreach (var item in smsItems)
                item.SenderNumber = DecodeDecimalAscii(item.SenderNumber);

            SmsList.ItemsSource = smsItems;
            NoSmsText.Visibility = smsItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SmsList.Visibility = smsItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            try
            {
                var logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}\n");
            }
            catch { }
        }
    }

    private static string DecodeDecimalAscii(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length < 4 || !input.All(char.IsDigit))
            return input;

        if (input.StartsWith("0") || input.StartsWith("213") || input.StartsWith("00213"))
            return input;

        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < input.Length)
        {
            bool matched = false;
            if (i + 2 <= input.Length)
            {
                int val2 = int.Parse(input.Substring(i, 2));
                if (val2 >= 32 && val2 <= 99)
                {
                    result.Append((char)val2);
                    i += 2;
                    matched = true;
                }
            }
            if (!matched && i + 3 <= input.Length)
            {
                int val3 = int.Parse(input.Substring(i, 3));
                if (val3 >= 100 && val3 <= 126)
                {
                    result.Append((char)val3);
                    i += 3;
                    matched = true;
                }
            }
            if (!matched)
                return input;
        }
        return result.ToString().ToUpper();
    }

    private void SmsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SmsList.SelectedItem is SmsListItem sms)
        {
            var dialog = new SmsDetailDialog(sms);
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.NavigateToOverview();
    }
}
