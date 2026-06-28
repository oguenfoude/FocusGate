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
    public List<HighBalanceModemRow> HighBalanceModems { get; set; } = new();

    public IndexModel(FocusGateDbContext db)
    {
        _db = db;
    }

    public async Task OnGetAsync()
    {
        ModemsTotal = await _db.Modems.CountAsync();
        ModemsOnline = await _db.Modems.CountAsync(m => m.Status == Core.Enums.ModemStatus.Online);

        SimCount = await _db.SimCards.CountAsync(s => s.IsActive);
        TotalSimBalance = (await _db.SimCards.Where(s => s.IsActive).Select(s => s.Balance).ToListAsync()).Sum();

        UserCount = await _db.Users.CountAsync(u => u.ArchivedAt == null && u.Role != FocusGate.Core.Enums.UserRole.Admin);
        TotalUserBalance = (await _db.Users.Where(u => u.ArchivedAt == null && u.Role != FocusGate.Core.Enums.UserRole.Admin).Select(u => u.Balance).ToListAsync()).Sum();

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

        HighBalanceModems = await _db.Modems
            .Include(m => m.SimCards.Where(s => s.IsActive))
            .Where(m => m.SimCards.Any(s => s.IsActive && s.Balance >= 1000))
            .Select(m => new HighBalanceModemRow
            {
                Id = m.Id,
                PhoneNumber = m.SimCards.FirstOrDefault(s => s.IsActive)!.PhoneNumber.ToString(),
                Balance = m.SimCards.FirstOrDefault(s => s.IsActive)!.Balance
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

    public class HighBalanceModemRow
    {
        public int Id { get; set; }
        public string PhoneNumber { get; set; } = "";
        public decimal Balance { get; set; }
    }
}
