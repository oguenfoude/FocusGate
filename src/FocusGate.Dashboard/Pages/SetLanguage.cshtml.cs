using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Localization;

namespace FocusGate.Dashboard.Pages;

public class SetLanguageModel : PageModel
{
    public IActionResult OnGet(string culture, string returnUrl = "/")
    {
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, Path = "/" }
        );
        return LocalRedirect(returnUrl);
    }
}
