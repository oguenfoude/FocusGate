using FocusGate.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FocusGate.Dashboard.Pages;

public class WarningsModel : PageModel
{
    private readonly FocusGateDbContext _db;

    public List<WarningRow> Warnings { get; set; } = new();

    public WarningsModel(FocusGateDbContext db)
    {
        _db = db;
    }

    public async Task OnGetAsync()
    {
        Warnings = await _db.Modems
            .Include(m => m.SimCards.Where(s => s.IsActive))
            .Where(m => m.SimCards.Any(s => s.IsActive && s.Balance >= 45000))
            .Select(m => new WarningRow
            {
                Id = m.Id,
                PhoneNumber = m.SimCards.FirstOrDefault(s => s.IsActive)!.PhoneNumber.ToString(),
                Balance = m.SimCards.FirstOrDefault(s => s.IsActive)!.Balance
            })
            .ToListAsync();
    }

    public class WarningRow
    {
        public int Id { get; set; }
        public string PhoneNumber { get; set; } = "";
        public decimal Balance { get; set; }
    }
}
