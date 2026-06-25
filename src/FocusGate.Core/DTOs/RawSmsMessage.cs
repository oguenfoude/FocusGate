namespace FocusGate.Core.DTOs;

public class RawSmsMessage
{
    public int      Index        { get; set; }
    public string   Status       { get; set; } = string.Empty;
    public string   Sender       { get; set; } = string.Empty;
    public DateTime ReceivedAt   { get; set; }
    public string   Content      { get; set; } = string.Empty;
}

