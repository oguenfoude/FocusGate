using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FocusGate.Dashboard.Pages;

public class IndexModel : PageModel
{
    private readonly FocusGateDbContext _db;

    public int ModemsTotal { get; set; }
    public int ModemsOnline { get; set; }
    public int SimCount { get; set; }
    public decimal TotalSimBalance { get; set; }
    public int UserCount { get; set; }
    public decimal TotalUserBalance { get; set; }
    public int PendingWithdrawals { get; set; }
    public List<SmsRow> RecentSms { get; set; } = new();

    public IndexModel(FocusGateDbContext db)
    {
        _db = db;
    }

    public async Task OnGetAsync()
    {
        ModemsTotal = await _db.Modems.CountAsync();
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var onlineModemIds = await _db.Modems
            .Where(m => m.Status == Core.Enums.ModemStatus.Online && m.UpdatedAt >= cutoff)
            .Select(m => m.Id)
            .ToListAsync();
        ModemsOnline = onlineModemIds.Count;

        var activeOnlineSims = _db.SimCards.Where(s => s.IsActive && onlineModemIds.Contains(s.ModemId));
        SimCount = await activeOnlineSims.CountAsync();
        TotalSimBalance = (await activeOnlineSims.Select(s => s.Balance).ToListAsync()).Sum();

        var activeUsers = _db.Users.Where(u => u.ArchivedAt == null && u.Role != FocusGate.Core.Enums.UserRole.Admin);
        UserCount = await activeUsers.CountAsync();
        TotalUserBalance = (await activeUsers.Select(u => u.Balance).ToListAsync()).Sum();

        PendingWithdrawals = await _db.WithdrawalRequests.CountAsync(w => w.Status == Core.Enums.WithdrawalStatus.Pending);

        RecentSms = await _db.SmsRecords
            .OrderByDescending(s => s.ReceivedAt)
            .Take(20)
            .Select(s => new SmsRow
            {
                SenderNumber = s.SenderNumber,
                Content = s.Content,
                ReceivedAt = s.ReceivedAt,
                ModemImei = s.SimCard != null && s.SimCard.Modem != null ? s.SimCard.Modem.IMEI : null,
                PhoneNumber = s.SimCard != null ? s.SimCard.PhoneNumber.ToString() : null
            })
            .ToListAsync();
    }

    public class SmsRow
    {
        public string SenderNumber { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime ReceivedAt { get; set; }
        public string? ModemImei { get; set; }
        public string? PhoneNumber { get; set; }
    }
}
