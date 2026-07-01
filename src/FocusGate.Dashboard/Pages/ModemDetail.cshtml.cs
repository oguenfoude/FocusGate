using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FocusGate.Dashboard.Pages;

public class ModemDetailModel : PageModel
{
    private readonly FocusGateDbContext _db;

    public Modem? Modem { get; set; }
    public SimCard? Sim { get; set; }
    public User? AssignedUser { get; set; }
    public List<BalanceHistory> BalanceHistory { get; set; } = new();
    public List<SmsRecord> SmsRecords { get; set; } = new();
    public int SmsCount { get; set; }
    public bool IsOnline { get; set; }
    public string StatusLabel { get; set; } = "";

    public ModemDetailModel(FocusGateDbContext db)
    {
        _db = db;
    }

    public async Task OnGetAsync(int id)
    {
        Modem = await _db.Modems
            .Include(m => m.SimCards.Where(s => s.IsActive))
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id);

        if (Modem == null) return;

        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        IsOnline = Modem.Status == ModemStatus.Online && Modem.UpdatedAt >= cutoff;

        StatusLabel = Modem.Status switch
        {
            ModemStatus.Online => "Online",
            ModemStatus.Offline => "Offline",
            ModemStatus.Error => "Error",
            ModemStatus.PendingNetwork => "Pending Network",
            ModemStatus.Connecting => "Connecting",
            ModemStatus.Initializing => "Initializing",
            ModemStatus.Detected => "Detected",
            _ => "Unknown"
        };

        Sim = Modem.SimCards.FirstOrDefault(s => s.IsActive);

        if (Sim != null)
        {
            var userModem = await _db.UserModems
                .Include(um => um.User)
                .FirstOrDefaultAsync(um => um.ModemId == id && um.RemovedAt == null);

            AssignedUser = userModem?.User;

            BalanceHistory = await _db.BalanceHistories
                .Where(b => b.SimCardId == Sim.Id)
                .OrderByDescending(b => b.RecordedAt)
                .Take(50)
                .AsNoTracking()
                .ToListAsync();

            SmsCount = await _db.SmsRecords.CountAsync(s => s.SimCardId == Sim.Id);

            SmsRecords = await _db.SmsRecords
                .Where(s => s.SimCardId == Sim.Id)
                .OrderByDescending(s => s.ReceivedAt)
                .Take(20)
                .AsNoTracking()
                .ToListAsync();
        }
    }

    public async Task<IActionResult> OnPostUnassignAsync(int modemId)
    {
        var userModem = await _db.UserModems
            .FirstOrDefaultAsync(um => um.ModemId == modemId && um.RemovedAt == null);

        if (userModem != null)
        {
            userModem.RemovedAt = DateTime.UtcNow;
            userModem.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        Response.Headers["HX-Redirect"] = $"/ModemDetail?id={modemId}";
        return new EmptyResult();
    }
}
