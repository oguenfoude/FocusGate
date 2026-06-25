using System.Windows;
using FocusGate.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace FocusGate.Desktop.Views;

public partial class SmsDetailDialog : FluentWindow
{
    public SmsDetailDialog(SmsListItem sms)
    {
        InitializeComponent();
        SenderText.Text = sms.SenderNumber;
        DateText.Text = sms.ReceivedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        ContentText.Text = sms.Content;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
