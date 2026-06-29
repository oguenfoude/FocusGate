using System.Security.Cryptography;
using System.Text;
using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using FocusGate.Dashboard.Resources;

namespace FocusGate.Dashboard.Pages;

public class UsersModel : PageModel
{
    private readonly FocusGateDbContext _db;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public List<UserRow> Users { get; set; } = new();

    public UsersModel(FocusGateDbContext db, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _localizer = localizer;
    }

    public async Task OnGetAsync(bool showArchived = false)
    {
        IQueryable<User> query;

        if (showArchived)
        {
            // Must bypass the global query filter to see archived users
            query = _db.Users
                .IgnoreQueryFilters()
                .Include(u => u.UserModems.Where(um => um.RemovedAt == null))
                .Where(u => u.Role != UserRole.Admin)
                .AsNoTracking();
        }
        else
        {
            query = _db.Users
                .Include(u => u.UserModems.Where(um => um.RemovedAt == null))
                .AsNoTracking()
                .Where(u => u.ArchivedAt == null && u.Role != UserRole.Admin);
        }

        var users = await query.OrderBy(u => u.Id).ToListAsync();

        Users = users.Select(u => new UserRow
        {
            Id = u.Id,
            Username = u.Username,
            DisplayName = u.DisplayName,
            Role = u.Role.ToString(),
            Balance = u.Balance,
            ModemCount = u.UserModems.Count(um => um.RemovedAt == null),
            IsArchived = u.ArchivedAt != null
        }).ToList();
    }

    public async Task<IActionResult> OnPostArchiveAsync(long userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user != null && user.Role != UserRole.Admin)
        {
            user.ArchivedAt = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            Response.Headers["HX-Trigger"] = $"{{\"showToast\":{{\"message\":\"{_localizer["Toast.UserArchived"]}\",\"type\":\"success\"}}}}";
        }
        Response.Headers["HX-Redirect"] = "/Users";
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostRestoreAsync(long userId)
    {
        // Must bypass the global query filter to find archived users
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.ArchivedAt = null;
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            Response.Headers["HX-Trigger"] = $"{{\"showToast\":{{\"message\":\"{_localizer["Toast.UserRestored"]}\",\"type\":\"success\"}}}}";
        }
        Response.Headers["HX-Redirect"] = "/Users";
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostAddUserAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            Response.Headers["HX-Redirect"] = "/Users";
            return new EmptyResult();
        }

        var exists = await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Username == username);
        if (exists)
        {
            Response.Headers["HX-Redirect"] = "/Users";
            return new EmptyResult();
        }

        var user = new User
        {
            Username = username,
            Password = HashPassword(password),
            DisplayName = username,
            Role = UserRole.User,
            IsActive = true,
            Balance = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        Response.Headers["HX-Trigger"] = $"{{\"showToast\":{{\"message\":\"{_localizer["Toast.UserCreated"]}\",\"type\":\"success\"}}}}";
        Response.Headers["HX-Redirect"] = "/Users";
        return new EmptyResult();
    }

    private static string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLower();
    }

    public class UserRow
    {
        public long Id { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = "";
        public decimal Balance { get; set; }
        public int ModemCount { get; set; }
        public bool IsArchived { get; set; }
    }
}
