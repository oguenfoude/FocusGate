namespace FocusGate.Core.Models;

public class UserModem
{
    public long     Id          { get; set; }
    public long     UserId      { get; set; }
    public int      ModemId     { get; set; }
    public DateTime AssignedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? RemovedAt  { get; set; }
    public DateTime  UpdatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
    public string    MachineId  { get; set; } = string.Empty;

    public User   User   { get; set; } = null!;
    public Modem    Modem       { get; set; } = null!;
}
