using FocusGate.Core.Enums;

namespace FocusGate.Core.Models;

public class SimCard
{
    public long      Id            { get; set; }
    public int       ModemId       { get; set; }
    public string    IMSI          { get; set; } = string.Empty;
    public long      PhoneNumber   { get; set; }
    public decimal   Balance       { get; set; }
    public DateTime?  VerifiedAt   { get; set; }
    public bool      IsActive      { get; set; } = true;
    public SimStatus Status        { get; set; } = SimStatus.Active;
    public DateTime  FirstSeen     { get; set; }
    public DateTime  LastSeen      { get; set; }
    public DateTime?  RemovedAt    { get; set; }
    public DateTime?  ReplacedAt   { get; set; }
    public DateTime  CreatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt    { get; set; }
    public string    MachineId     { get; set; } = string.Empty;

    public Modem     Modem       { get; set; } = null!;
    public ICollection<SmsRecord> SmsRecords { get; set; } = new List<SmsRecord>();
}
