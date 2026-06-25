using FocusGate.Core.Enums;

namespace FocusGate.Core.Models;

public class WithdrawalRequest
{
    public long                Id                  { get; set; }
    public long                UserId              { get; set; }
    public decimal             Amount              { get; set; }
    public WithdrawalStatus    Status              { get; set; } = WithdrawalStatus.Pending;
    public string?             Note                { get; set; }
    public string?             AdminNote           { get; set; }
    public long?               ProcessedByAdminId  { get; set; }
    public DateTime            RequestedAt         { get; set; } = DateTime.UtcNow;
    public DateTime?           ProcessedAt         { get; set; }
    public DateTime            CreatedAt           { get; set; } = DateTime.UtcNow;
    public DateTime            UpdatedAt           { get; set; } = DateTime.UtcNow;
    public DateTime?           ArchivedAt          { get; set; }
    public string              MachineId           { get; set; } = string.Empty;

    public User? User              { get; set; }
    public User? ProcessedByAdmin  { get; set; }
}
