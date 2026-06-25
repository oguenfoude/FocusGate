namespace FocusGate.Desktop.ViewModels;

public class SmsListItem
{
    public long Id { get; set; }
    public string SenderNumber { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ContentPreview => Content.Length > 40 ? Content[..40] + "..." : Content;
    public DateTime ReceivedAt { get; set; }
    public string ReceivedAtFormatted => ReceivedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}
