using System.Windows;

namespace FocusGate.Desktop.Views;

public partial class LoadingWindow : Window
{
    public LoadingWindow()
    {
        InitializeComponent();
    }

    public void UpdateStatus(string status)
    {
        StatusText.Text = status;
    }
}
