using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FocusGate.Dashboard.Pages;

public class ModemsModel : PageModel
{
    private readonly FocusGateDbContext _db;

    public List<ModemRow> Modems { get; set; } = new();
    public List<ModemRow> AllModems { get; set; } = new();

    public ModemsModel(FocusGateDbContext db)
    {
        _db = db;
    }

    public async Task OnGetAsync(string? status = null)
    {
        // Always load ALL modems + their user assignments for tab counts
        var allModemsRaw = await _db.Modems
            .Include(m => m.SimCards.Where(s => s.IsActive))
            .AsNoTracking()
            .OrderBy(m => m.Id)
            .ToListAsync();

        var allModemIds = allModemsRaw.Select(m => m.Id).ToList();

        var allUserModems = await _db.UserModems
            .Where(um => allModemIds.Contains(um.ModemId) && um.RemovedAt == null)
            .Include(um => um.User)
            .ToDictionaryAsync(um => um.ModemId);

        var cutoff = DateTime.UtcNow.AddMinutes(-5);

        AllModems = allModemsRaw.Select(m =>
        {
            var sim = m.SimCards.FirstOrDefault(s => s.IsActive);
            allUserModems.TryGetValue(m.Id, out var um);
            return new ModemRow
            {
                Id = m.Id, IMEI = m.IMEI,
                ComPort = m.ComPort,
                Brand = m.Brand.ToString(), Model = m.Model,
                IsOnline = m.Status == ModemStatus.Online && m.UpdatedAt >= cutoff,
                PhoneNumber = sim?.PhoneNumber.ToString() ?? "N/A",
                Balance = sim?.Balance ?? 0,
                LastSeen = sim?.LastSeen ?? m.CreatedAt,
                UserId = um?.UserId, UserName = um?.User?.Username
            };
        }).ToList();

        // Apply filter
        Modems = status switch
        {
            "online"     => AllModems.Where(m => m.IsOnline).ToList(),
            "offline"    => AllModems.Where(m => !m.IsOnline).ToList(),
            "assigned"   => AllModems.Where(m => m.UserId.HasValue).ToList(),
            "unassigned" => AllModems.Where(m => !m.UserId.HasValue).ToList(),
            _            => AllModems
        };
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

        Response.Headers["HX-Redirect"] = "/Modems";
        return new EmptyResult();
    }

    public class ModemRow
    {
        public int Id { get; set; }
        public string IMEI { get; set; } = "";
        public string? ComPort { get; set; }
        public string Brand { get; set; } = "";
        public string? Model { get; set; }
        public bool IsOnline { get; set; }
        public string PhoneNumber { get; set; } = "";
        public decimal Balance { get; set; }
        public DateTime LastSeen { get; set; }
        public long? UserId { get; set; }
        public string? UserName { get; set; }
    }
}
