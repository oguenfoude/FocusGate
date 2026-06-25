namespace FocusGate.Core.Models;

public class SmsRecord
{
    public long     Id           { get; set; }
    public long     SimCardId    { get; set; }
    public string   SenderNumber { get; set; } = string.Empty;
    public string   Content      { get; set; } = string.Empty;
    public DateTime ReceivedAt   { get; set; }
    public DateTime ProcessedAt  { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
    public string    MachineId  { get; set; } = string.Empty;

    public SimCard SimCard { get; set; } = null!;
}
