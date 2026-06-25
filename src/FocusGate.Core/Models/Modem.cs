using FocusGate.Core.Enums;

namespace FocusGate.Core.Models;

public class Modem
{
    public int      Id          { get; set; }
    public string   IMEI        { get; set; } = string.Empty;
    public string?  ComPort     { get; set; }
    public ModemStatus Status   { get; set; } = ModemStatus.Offline;
    public ModemBrand Brand     { get; set; } = ModemBrand.Unknown;
    public string?  Manufacturer { get; set; }
    public string?  Model       { get; set; }
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
    public string     MachineId  { get; set; } = string.Empty;

    public ICollection<SimCard>   SimCards   { get; set; } = new List<SimCard>();
}
