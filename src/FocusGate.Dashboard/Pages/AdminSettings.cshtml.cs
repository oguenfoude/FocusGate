using FocusGate.Core.Enums;
using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using FocusGate.Dashboard.Resources;

namespace FocusGate.Dashboard.Pages;

public class AdminSettingsModel : PageModel
{
    private readonly FocusGateDbContext _db;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public string CurrentUsername { get; set; } = "";
    public string CurrentDisplayName { get; set; } = "";

    [BindProperty] public string NewUsername { get; set; } = "";
    [BindProperty] public string NewDisplayName { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";

    public AdminSettingsModel(FocusGateDbContext db, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _localizer = localizer;
    }

    public async Task OnGetAsync()
    {
        var admin = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Role == UserRole.Admin);
        if (admin != null)
        {
            CurrentUsername = admin.Username;
            CurrentDisplayName = admin.DisplayName ?? "";
        }
    }

    public async Task<IActionResult> OnPostChangeUsernameAsync()
    {
        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Admin);
        if (admin == null) return RedirectToPage();

        if (string.IsNullOrWhiteSpace(NewUsername))
        {
            TempData["ToastMessage"] = _localizer["Settings.UsernameRequired"].Value;
            TempData["ToastType"] = "error";
            return RedirectToPage();
        }

        var exists = await _db.Users.AnyAsync(u => u.Username == NewUsername && u.Id != admin.Id);
        if (exists)
        {
            TempData["ToastMessage"] = _localizer["Settings.UsernameTaken"].Value;
            TempData["ToastType"] = "error";
            return RedirectToPage();
        }

        admin.Username = NewUsername;
        await _db.SaveChangesAsync();

        TempData["ToastMessage"] = _localizer["Settings.UsernameUpdated"].Value;
        TempData["ToastType"] = "success";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangePasswordAsync()
    {
        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Admin);
        if (admin == null) return RedirectToPage();

        if (string.IsNullOrWhiteSpace(NewPassword))
        {
            TempData["ToastMessage"] = _localizer["Settings.PasswordRequired"].Value;
            TempData["ToastType"] = "error";
            return RedirectToPage();
        }

        if (NewPassword != ConfirmPassword)
        {
            TempData["ToastMessage"] = _localizer["Settings.PasswordMismatch"].Value;
            TempData["ToastType"] = "error";
            return RedirectToPage();
        }

        admin.Password = NewPassword;
        await _db.SaveChangesAsync();

        TempData["ToastMessage"] = _localizer["Settings.PasswordUpdated"].Value;
        TempData["ToastType"] = "success";
        return RedirectToPage();
    }
}
