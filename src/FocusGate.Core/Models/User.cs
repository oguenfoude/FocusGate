using FocusGate.Core.Enums;

namespace FocusGate.Core.Models;

public class User
{
    public long     Id          { get; set; }
    public string   Username    { get; set; } = string.Empty;
    public string   Password    { get; set; } = string.Empty;
    public string   DisplayName { get; set; } = string.Empty;
    public UserRole Role        { get; set; }
    public bool     IsActive    { get; set; } = true;
    public decimal  Balance     { get; set; } = 0m;
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
    public string   MachineId   { get; set; } = string.Empty;

    public ICollection<UserModem>          UserModems          { get; set; } = new List<UserModem>();
    public ICollection<BalanceHistory>     BalanceHistories    { get; set; } = new List<BalanceHistory>();
    public ICollection<WithdrawalRequest>  WithdrawalRequests  { get; set; } = new List<WithdrawalRequest>();
    public ICollection<UserBalanceHistory> UserBalanceHistories { get; set; } = new List<UserBalanceHistory>();
}
