using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FocusGate.Core.Enums;
using FocusGate.Desktop.Data;
using FocusGate.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace FocusGate.Desktop.Views;

public partial class ModemsOverviewPage : Page
{
    private readonly IServiceProvider _services;
    private readonly MainWindow _mainWindow;
    private readonly DispatcherTimer _refreshTimer;
    private List<ModemListItem> _allItems = new();
    private int _previousSmsCount = 0;

    private enum FilterState { All, Online, Offline }
    private FilterState _currentFilter = FilterState.All;

    public ModemsOverviewPage(IServiceProvider services, MainWindow mainWindow)
    {
        InitializeComponent();
        _services = services;
        _mainWindow = mainWindow;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (s, e) => await LoadDataAsync();
        _refreshTimer.Start();

        Loaded += async (s, e) => await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            using var db = scope.ServiceProvider.GetRequiredService<ReadOnlyDbContext>();

            var modems = await db.Modems.ToListAsync();
            var allSims = await db.SimCards.ToListAsync();

            foreach (var m in modems)
                m.SimCards = allSims.Where(s => s.ModemId == m.Id).ToList();

            var totalSms = await db.SmsRecords.CountAsync();

            if (_previousSmsCount > 0 && totalSms > _previousSmsCount)
            {
                _mainWindow.PlayNotificationSound();
            }
            _previousSmsCount = totalSms;

            var items = new List<ModemListItem>();
            int row = 1;
            int online = 0;
            int offline = 0;

            foreach (var modem in modems)
            {
                var activeSim = modem.SimCards.FirstOrDefault(s => s.IsActive);

                bool isOnline = modem.Status == ModemStatus.Online;
                if (isOnline) online++; else offline++;

                items.Add(new ModemListItem
                {
                    Id = modem.Id,
                    RowNumber = row++,
                    Imei = modem.IMEI ?? "---",
                    PhoneNumber = activeSim?.PhoneNumber != 0
                        ? activeSim!.PhoneNumber.ToString()
                        : "---",
                    IsOnline = isOnline,
                    Balance = activeSim?.Balance ?? 0m
                });
            }

            _allItems = items;
            _ = Dispatcher.BeginInvoke(() =>
            {
                ApplyFilter();
                OnlineCount.Text = $"{online} Online";
                OfflineCount.Text = $"{offline} Offline";
            });
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

    private void ApplyFilter()
    {
        var filtered = _currentFilter switch
        {
            FilterState.Online => _allItems.Where(i => i.IsOnline).ToList(),
            FilterState.Offline => _allItems.Where(i => !i.IsOnline).ToList(),
            _ => _allItems
        };

        ModemsGrid.ItemsSource = filtered;
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModemsGrid.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateFilterButtons();
    }

    private void UpdateFilterButtons()
    {
        FilterAll.Appearance = _currentFilter == FilterState.All
            ? ControlAppearance.Primary : ControlAppearance.Secondary;
        FilterOnline.Appearance = _currentFilter == FilterState.Online
            ? ControlAppearance.Primary : ControlAppearance.Secondary;
        FilterOffline.Appearance = _currentFilter == FilterState.Offline
            ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e) { _currentFilter = FilterState.All; ApplyFilter(); }
    private void FilterOnline_Click(object sender, RoutedEventArgs e) { _currentFilter = FilterState.Online; ApplyFilter(); }
    private void FilterOffline_Click(object sender, RoutedEventArgs e) { _currentFilter = FilterState.Offline; ApplyFilter(); }

    private void ModemsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ModemsGrid.SelectedItem is ModemListItem item)
            _mainWindow.NavigateToDetail(item.Id);
    }

    private void ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.DataContext is ModemListItem item)
            _mainWindow.NavigateToDetail(item.Id);
    }
}
