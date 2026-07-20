using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using FocusGate.Dashboard.Resources;

namespace FocusGate.Dashboard.Pages;

public class WithdrawalsModel : PageModel
{
    private readonly FocusGateDbContext _db;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public List<WithdrawalRow> Requests { get; set; } = new();
    public List<WithdrawalRow> AllRequests { get; set; } = new();

    public WithdrawalsModel(FocusGateDbContext db, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _localizer = localizer;
    }

    public async Task OnGetAsync(string? status = null)
    {
        var all = await _db.WithdrawalRequests
            .Include(w => w.User)
            .AsNoTracking()
            .OrderByDescending(w => w.RequestedAt)
            .ToListAsync();

        AllRequests = all.Select(w => new WithdrawalRow
        {
            Id = w.Id, UserId = w.UserId,
            Username = w.User?.Username ?? "Unknown",
            Amount = w.Amount, Status = w.Status.ToString(),
            RequestedAt = w.RequestedAt, ProcessedAt = w.ProcessedAt, Note = w.Note
        }).ToList();

        if (string.IsNullOrEmpty(status) || !Enum.TryParse<WithdrawalStatus>(status, out var s))
        {
            Requests = AllRequests;
        }
        else
        {
            Requests = AllRequests.Where(r => r.Status == s.ToString()).ToList();
        }
    }

    public async Task<IActionResult> OnPostApproveAsync(long id)
    {
        var request = await _db.WithdrawalRequests
            .Include(w => w.User)
            .FirstOrDefaultAsync(w => w.Id == id && w.Status == WithdrawalStatus.Pending);

        if (request?.User == null)
        {
            Response.Headers["HX-Redirect"] = "/Withdrawals";
            return new EmptyResult();
        }

        if (request.User.Balance < request.Amount)
        {
            TempData["ToastMessage"] = _localizer["Toast.InsufficientBalance"].Value;
            TempData["ToastType"] = "error";
            Response.Headers["HX-Redirect"] = "/Withdrawals";
            return new EmptyResult();
        }

        var admin = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Role == UserRole.Admin);

        var adminId = admin?.Id;
        var now = DateTime.UtcNow;

        request.Status = WithdrawalStatus.Approved;
        request.ProcessedAt = now;
        request.ProcessedByAdminId = adminId;
        request.UpdatedAt = now;

        var user = request.User;
        var oldBalance = user.Balance;
        user.Balance -= request.Amount;
        user.UpdatedAt = now;

        _db.UserBalanceHistories.Add(new UserBalanceHistory
        {
            UserId = request.UserId,
            Amount = -request.Amount,
            BalanceAfter = user.Balance,
            Type = 1,
            Note = $"Withdrawal approved: {request.Amount:N2} DA",
            RecordedAt = now,
            UpdatedAt = now
        });

        _db.BalanceHistories.Add(new BalanceHistory
        {
            UserId = request.UserId,
            Balance = user.Balance,
            PreviousBalance = oldBalance,
            Source = BalanceSource.Withdrawal,
            RecordedAt = now,
            UpdatedAt = now
        });

        await _db.SaveChangesAsync();
        TempData["ToastMessage"] = _localizer["Toast.WithdrawalApproved"].Value;
        TempData["ToastType"] = "success";
        Response.Headers["HX-Redirect"] = "/Withdrawals";
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostRejectAsync(long id)
    {
        var request = await _db.WithdrawalRequests
            .FirstOrDefaultAsync(w => w.Id == id && w.Status == WithdrawalStatus.Pending);

        if (request == null)
        {
            Response.Headers["HX-Redirect"] = "/Withdrawals";
            return new EmptyResult();
        }

        var admin = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Role == UserRole.Admin);

        request.Status = WithdrawalStatus.Rejected;
        request.ProcessedAt = DateTime.UtcNow;
        request.ProcessedByAdminId = admin?.Id;
        request.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        TempData["ToastMessage"] = _localizer["Toast.WithdrawalRejected"].Value;
        TempData["ToastType"] = "success";
        Response.Headers["HX-Redirect"] = "/Withdrawals";
        return new EmptyResult();
    }

    public class WithdrawalRow
    {
        public long Id { get; set; }
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public decimal Amount { get; set; }
        public string Status { get; set; } = "";
        public DateTime RequestedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
        public string? Note { get; set; }
    }
}
