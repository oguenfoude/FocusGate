using FocusGate.Core.DTOs;
using FocusGate.Core.Interfaces;
using FocusGate.Core.Models;
using FocusGate.Dashboard.Resources;
using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace FocusGate.Dashboard.Pages;

public class SmsModel : PageModel
{
    private readonly FocusGateDbContext _db;
    private readonly IStringLocalizer<SharedResource> _localizer;
    public IConfigProvider Config { get; }

    public List<SmsRow> Messages { get; set; } = new();
    public List<ModemOption> ModemOptions { get; set; } = new();
    public int TotalCount { get; set; }
    public int TodayCount { get; set; }
    public int RechargeCount { get; set; }
    public int BalanceCount { get; set; }

    public int CurrentPage { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? ModemId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Days { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public SmsModel(FocusGateDbContext db, IStringLocalizer<SharedResource> localizer, IConfigProvider config)
    {
        _db = db;
        _localizer = localizer;
        Config = config;
    }

    public async Task OnGetAsync(int pageNumber = 1)
    {
        CurrentPage = Math.Max(1, pageNumber);

        // Load modem options for dropdown
        ModemOptions = await _db.Modems
            .AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new ModemOption
            {
                Id = m.Id,
                Imei = m.IMEI,
                Phone = m.SimCards.Where(s => s.IsActive).Select(s => s.PhoneNumber > 0 ? s.PhoneNumber.ToString() : null).FirstOrDefault()
            })
            .ToListAsync();

        var query = _db.SmsRecords
            .Include(s => s.SimCard)
            .ThenInclude(sc => sc!.Modem)
            .AsNoTracking()
            .AsQueryable();

        // Calculate global stats
        var todayUtc = DateTime.UtcNow.Date;
        TotalCount = await _db.SmsRecords.CountAsync();
        TodayCount = await _db.SmsRecords.CountAsync(s => s.ReceivedAt >= todayUtc);
        RechargeCount = await _db.SmsRecords.CountAsync(s => 
            s.Content.Contains("montant de") || s.Content.Contains("rechargé"));
        BalanceCount = await _db.SmsRecords.CountAsync(s => s.Content.Contains("Solde"));

        // Apply filters
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var search = Q.Trim().ToLower();
            query = query.Where(s => s.Content.ToLower().Contains(search) 
                || s.SenderNumber.ToLower().Contains(search));
        }

        if (ModemId.HasValue && ModemId.Value > 0)
        {
            query = query.Where(s => s.SimCard != null && s.SimCard.ModemId == ModemId.Value);
        }

        if (Days.HasValue && Days.Value > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-Days.Value);
            query = query.Where(s => s.ReceivedAt >= cutoff);
        }

        if (!string.IsNullOrWhiteSpace(Type))
        {
            var t = Type.Trim().ToLower();
            if (t == "recharge")
                query = query.Where(s => s.Content.Contains("montant de") || s.Content.Contains("rechargé"));
            else if (t == "balance")
                query = query.Where(s => s.Content.Contains("Solde"));
            else if (t == "other")
                query = query.Where(s => !s.Content.Contains("Solde") && !s.Content.Contains("montant de") && !s.Content.Contains("rechargé"));
        }

        var filteredTotal = await query.CountAsync();
        TotalPages = Math.Max(1, (int)Math.Ceiling(filteredTotal / (double)PageSize));
        CurrentPage = Math.Min(CurrentPage, TotalPages);

        var pagedList = await query
            .OrderByDescending(s => s.ReceivedAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        Messages = pagedList.Select(s => new SmsRow
        {
            Id = s.Id,
            SenderNumber = s.SenderNumber ?? "",
            Content = s.Content ?? "",
            ReceivedAt = s.ReceivedAt,
            SimCardId = s.SimCardId,
            ModemId = s.SimCard?.ModemId,
            ModemImei = s.SimCard?.Modem?.IMEI,
            PhoneNumber = s.SimCard != null && s.SimCard.PhoneNumber > 0 ? s.SimCard.PhoneNumber.ToString() : null
        }).ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var sms = await _db.SmsRecords.FirstOrDefaultAsync(s => s.Id == id);
        if (sms != null)
        {
            _db.SmsRecords.Remove(sms);
            await _db.SaveChangesAsync();
            StatusMessage = "SMS deleted successfully.";
        }

        if (Request.Headers.ContainsKey("HX-Request"))
        {
            Response.Headers["HX-Redirect"] = Request.Headers["Referer"].ToString() ?? "/Sms";
            return new EmptyResult();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteOlderAsync(int olderThanDays)
    {
        if (olderThanDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
            var toDelete = await _db.SmsRecords
                .Where(s => s.ReceivedAt < cutoff)
                .ToListAsync();

            if (toDelete.Count > 0)
            {
                _db.SmsRecords.RemoveRange(toDelete);
                await _db.SaveChangesAsync();
                StatusMessage = $"Deleted {toDelete.Count} messages older than {olderThanDays} days.";
            }
        }

        if (Request.Headers.ContainsKey("HX-Request"))
        {
            Response.Headers["HX-Redirect"] = "/Sms";
            return new EmptyResult();
        }

        return RedirectToPage();
    }

    public class SmsRow
    {
        public long Id { get; set; }
        public string SenderNumber { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime ReceivedAt { get; set; }
        public long SimCardId { get; set; }
        public int? ModemId { get; set; }
        public string? ModemImei { get; set; }
        public string? PhoneNumber { get; set; }
    }

    public class ModemOption
    {
        public int Id { get; set; }
        public string? Imei { get; set; }
        public string? Phone { get; set; }
    }
}
