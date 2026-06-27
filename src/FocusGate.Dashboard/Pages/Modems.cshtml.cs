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

    public ModemsModel(FocusGateDbContext db)
    {
        _db = db;
    }

    public async Task OnGetAsync(string? status = null)
    {
        var query = _db.Modems
            .Include(m => m.SimCards.Where(s => s.IsActive))
            .AsNoTracking()
            .AsQueryable();

        if (status == "online")
            query = query.Where(m => m.Status == ModemStatus.Online);
        else if (status == "offline")
            query = query.Where(m => m.Status != ModemStatus.Online);

        var modems = await query.OrderBy(m => m.Id).ToListAsync();

        var modemIds = modems.Select(m => m.Id).ToList();

        var userModems = await _db.UserModems
            .Where(um => modemIds.Contains(um.ModemId) && um.RemovedAt == null)
            .Include(um => um.User)
            .ToDictionaryAsync(um => um.ModemId);

        Modems = modems.Select(m =>
        {
            var sim = m.SimCards.FirstOrDefault(s => s.IsActive);
            userModems.TryGetValue(m.Id, out var userModem);

            return new ModemRow
            {
                Id = m.Id,
                IMEI = m.IMEI,
                ComPort = m.ComPort ?? sim?.Modem?.ComPort,
                Brand = m.Brand.ToString(),
                Model = m.Model,
                IsOnline = m.Status == ModemStatus.Online,
                PhoneNumber = sim?.PhoneNumber.ToString() ?? "N/A",
                Balance = sim?.Balance ?? 0,
                LastSeen = sim?.LastSeen ?? m.CreatedAt,
                UserId = userModem?.UserId,
                UserName = userModem?.User?.Username
            };
        }).ToList();
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
