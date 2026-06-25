namespace FocusGate.Desktop.ViewModels;

public class SmsListItem
{
    public long Id { get; set; }
    public string SenderNumber { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}
