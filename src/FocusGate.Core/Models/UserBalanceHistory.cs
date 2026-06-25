namespace FocusGate.Core.Models;

public class UserBalanceHistory
{
    public long     Id          { get; set; }
    public long     UserId      { get; set; }
    public decimal  Amount      { get; set; }
    public decimal  BalanceAfter { get; set; }
    public int      Type        { get; set; }
    public long?    SimCardId   { get; set; }
    public string?  Note        { get; set; }
    public DateTime RecordedAt  { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
    public string   MachineId   { get; set; } = string.Empty;

    public User?     User     { get; set; }
    public SimCard?  SimCard  { get; set; }
}
