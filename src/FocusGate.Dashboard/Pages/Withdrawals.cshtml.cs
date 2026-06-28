using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FocusGate.Dashboard.Pages;

public class WithdrawalsModel : PageModel
{
    private readonly FocusGateDbContext _db;

    public List<WithdrawalRow> Requests { get; set; } = new();
    public List<WithdrawalRow> AllRequests { get; set; } = new();

    public WithdrawalsModel(FocusGateDbContext db)
    {
        _db = db;
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

        var filtered = string.IsNullOrEmpty(status) || !Enum.TryParse<WithdrawalStatus>(status, out var s)
            ? all
            : all.Where(w => w.Status == s).ToList();

        Requests = filtered.Select(w => new WithdrawalRow
        {
            Id = w.Id, UserId = w.UserId,
            Username = w.User?.Username ?? "Unknown",
            Amount = w.Amount, Status = w.Status.ToString(),
            RequestedAt = w.RequestedAt, ProcessedAt = w.ProcessedAt, Note = w.Note
        }).ToList();
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

        var user = request.User;
        if (user.Balance < request.Amount)
        {
            Response.Headers["HX-Redirect"] = "/Withdrawals";
            return new EmptyResult();
        }

        user.Balance -= request.Amount;
        user.UpdatedAt = DateTime.UtcNow;

        request.Status = WithdrawalStatus.Approved;
        request.ProcessedAt = DateTime.UtcNow;
        request.UpdatedAt = DateTime.UtcNow;

        _db.UserBalanceHistories.Add(new UserBalanceHistory
        {
            UserId = request.UserId,
            Amount = -request.Amount,
            BalanceAfter = user.Balance,
            Type = 1,
            Note = $"Withdrawal approved: {request.Amount:N2} DA",
            RecordedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        _db.BalanceHistories.Add(new BalanceHistory
        {
            UserId = request.UserId,
            Balance = user.Balance,
            PreviousBalance = user.Balance + request.Amount,
            Source = BalanceSource.Withdrawal,
            RecordedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
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

        request.Status = WithdrawalStatus.Rejected;
        request.ProcessedAt = DateTime.UtcNow;
        request.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
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
