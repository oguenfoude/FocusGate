using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using FocusGate.Dashboard.Resources;

namespace FocusGate.Dashboard.Pages;

public class UserDetailModel : PageModel
{
    private readonly FocusGateDbContext _db;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public new User? User { get; set; }
    public List<AssignedModem> AssignedModems { get; set; } = new();
    public List<AvailableModem> AvailableModems { get; set; } = new();
    public List<UserBalanceHistoryRow> BalanceHistory { get; set; } = new();
    public List<SmsRow> SmsMessages { get; set; } = new();

    public UserDetailModel(FocusGateDbContext db, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _localizer = localizer;
    }

    public async Task OnGetAsync(long id)
    {
        User = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

        if (User == null) return;

        AssignedModems = await _db.UserModems
            .Where(um => um.UserId == id && um.RemovedAt == null)
            .Include(um => um.Modem)
                .ThenInclude(m => m.SimCards.Where(s => s.IsActive))
            .Select(um => new AssignedModem
            {
                ModemId = um.ModemId,
                Imei = um.Modem.IMEI,
                PhoneNumber = um.Modem.SimCards.FirstOrDefault(s => s.IsActive) != null
                    ? um.Modem.SimCards.First(s => s.IsActive).PhoneNumber.ToString()
                    : "N/A",
                Balance = um.Modem.SimCards.FirstOrDefault(s => s.IsActive) != null
                    ? um.Modem.SimCards.First(s => s.IsActive).Balance
                    : 0
            })
            .ToListAsync();

        // Get all modem IDs already assigned to any user
        var alreadyAssigned = await _db.UserModems
            .Where(um => um.RemovedAt == null)
            .Select(um => um.ModemId)
            .ToListAsync();

        AvailableModems = await _db.Modems
            .Where(m => !alreadyAssigned.Contains(m.Id))
            .Select(m => new AvailableModem
            {
                Id = m.Id,
                Imei = m.IMEI,
                PhoneNumber = m.SimCards.FirstOrDefault(s => s.IsActive) != null
                    ? m.SimCards.First(s => s.IsActive).PhoneNumber.ToString()
                    : "N/A"
            })
            .ToListAsync();

        BalanceHistory = await _db.UserBalanceHistories
            .Where(b => b.UserId == id)
            .OrderByDescending(b => b.RecordedAt)
            .Select(b => new UserBalanceHistoryRow
            {
                RecordedAt = b.RecordedAt,
                Type = b.Type,
                Amount = b.Amount,
                BalanceAfter = b.BalanceAfter,
                Note = b.Note
            })
            .ToListAsync();

        // SMS messages from user's assigned modems
        var userModemIds = await _db.UserModems
            .Where(um => um.UserId == id && um.RemovedAt == null)
            .Select(um => um.ModemId)
            .ToListAsync();

        SmsMessages = await _db.SmsRecords
            .Where(s => s.SimCard != null && s.SimCard.Modem != null && userModemIds.Contains(s.SimCard.Modem.Id))
            .OrderByDescending(s => s.ReceivedAt)
            .Take(50)
            .Select(s => new SmsRow
            {
                SenderNumber = s.SenderNumber,
                Content = s.Content,
                ReceivedAt = s.ReceivedAt,
                PhoneNumber = s.SimCard != null ? s.SimCard.PhoneNumber.ToString() : "N/A"
            })
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostUnassignModemAsync(int modemId, long userId)
    {
        var userModem = await _db.UserModems
            .FirstOrDefaultAsync(um => um.ModemId == modemId && um.UserId == userId && um.RemovedAt == null);

        if (userModem != null)
        {
            userModem.RemovedAt = DateTime.UtcNow;
            userModem.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["ToastMessage"] = _localizer["Toast.ModemUnassigned"].Value;
            TempData["ToastType"] = "success";
        }

        Response.Headers["HX-Redirect"] = $"/UserDetail?id={userId}";
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostAssignModemAsync(long userId, int modemId)
    {
        // Check including soft-deleted records (bypass global filter)
        var existing = await _db.UserModems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(um => um.UserId == userId && um.ModemId == modemId);

        if (existing == null)
        {
            _db.UserModems.Add(new UserModem
            {
                UserId = userId,
                ModemId = modemId,
                AssignedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else if (existing.RemovedAt != null)
        {
            // Reactivate the soft-deleted assignment
            existing.RemovedAt = null;
            existing.AssignedAt = DateTime.UtcNow;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        // else: already active, do nothing

        await _db.SaveChangesAsync();
        TempData["ToastMessage"] = _localizer["Toast.ModemAssigned"].Value;
        TempData["ToastType"] = "success";
        Response.Headers["HX-Redirect"] = $"/UserDetail?id={userId}";
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostCreateWithdrawalAsync(long userId, decimal amount, string? note)
    {
        if (amount <= 0)
        {
            Response.Headers["HX-Redirect"] = $"/UserDetail?id={userId}";
            return new EmptyResult();
        }

        var user = await _db.Users.FindAsync(userId);
        if (user == null || user.Balance < amount)
        {
            Response.Headers["HX-Redirect"] = $"/UserDetail?id={userId}";
            return new EmptyResult();
        }

        _db.WithdrawalRequests.Add(new WithdrawalRequest
        {
            UserId = userId,
            Amount = amount,
            Status = WithdrawalStatus.Pending,
            Note = note,
            RequestedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
        TempData["ToastMessage"] = _localizer["Toast.WithdrawalCreated"].Value;
        TempData["ToastType"] = "success";
        Response.Headers["HX-Redirect"] = $"/UserDetail?id={userId}";
        return new EmptyResult();
    }

    public class AssignedModem
    {
        public int ModemId { get; set; }
        public string Imei { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
        public decimal Balance { get; set; }
    }

    public class AvailableModem
    {
        public int Id { get; set; }
        public string Imei { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
    }

    public class UserBalanceHistoryRow
    {
        public DateTime RecordedAt { get; set; }
        public int Type { get; set; }
        public decimal Amount { get; set; }
        public decimal BalanceAfter { get; set; }
        public string? Note { get; set; }
    }

    public class SmsRow
    {
        public string SenderNumber { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime ReceivedAt { get; set; }
        public string PhoneNumber { get; set; } = "";
    }
}
