using FocusGate.Core.Enums;

namespace FocusGate.Core.Models;

public class BalanceHistory
{
    public long     Id              { get; set; }
    public long?    SimCardId       { get; set; }
    public int?     ModemId         { get; set; }
    public long?    UserId          { get; set; }
    public decimal  Balance         { get; set; }
    public decimal? PreviousBalance { get; set; }
    public BalanceSource Source     { get; set; } = BalanceSource.USSD;
    public DateTime RecordedAt      { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
    public string    MachineId  { get; set; } = string.Empty;

    public SimCard? SimCard { get; set; }
    public Modem?   Modem   { get; set; }
    public User?    User    { get; set; }
}
