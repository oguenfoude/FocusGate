using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusGate.Hardware.Services;

public class ConsoleCommandHandler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseWriteChannel _writeChannel;
    private readonly ILogger<ConsoleCommandHandler> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private CancellationToken _ct;

    public ConsoleCommandHandler(IServiceScopeFactory scopeFactory, DatabaseWriteChannel writeChannel,
        ILogger<ConsoleCommandHandler> log, IHostApplicationLifetime lifetime)
    {
        _scopeFactory = scopeFactory;
        _writeChannel = writeChannel;
        _log = log;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ct = stoppingToken;
        await SeedAdminAsync(stoppingToken);

        bool hasConsole = true;
        try { _ = Console.WindowWidth; }
        catch { hasConsole = false; }

        if (!hasConsole)
        {
            _log.LogInformation("Running in background mode (no console).");
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return;
        }

        _log.LogInformation("Console ready. Type 'help'.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var line = await Task.Run(() => Console.ReadLine(), stoppingToken);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0].ToLower();
                var args = parts.Skip(1).ToArray();

                switch (cmd)
                {
                    case "help":        PrintHelp(); break;
                    case "status":      await ShowStatusAsync(); break;
                    case "modems":      await ListModemsAsync(); break;
                    case "modem":       await ShowModemAsync(args); break;
                    case "sms":         await ListSmsAsync(args); break;
                    case "sim":         await ShowSimAsync(args); break;
                    case "config":      await ShowConfigAsync(); break;
                    case "set-config":  await SetConfigAsync(args); break;
                    case "users":       await ListUsersAsync(); break;
                    case "adduser":     await AddUserAsync(args); break;
                    case "assign":      await AssignModemAsync(args); break;
                    case "unassign":    await UnassignModemAsync(args); break;
                    case "settle":       await SettleAsync(args); break;
                    case "report":       await ReportAsync(args); break;
                    case "exit":        _lifetime.StopApplication(); break;
                    default:            Console.WriteLine($"Unknown: {cmd}. Type 'help'."); break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Command error"); Console.WriteLine($"Error: {ex.Message}"); }
        }
    }

    private async Task SeedAdminAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin, ct))
            return;

        var admin = new User
        {
            Username = "admin",
            Password = "admin",
            DisplayName = "Administrator",
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Default admin user seeded: admin/admin");
    }

    private void PrintHelp()
    {
        Console.WriteLine("FocusGate Commands:");
        Console.WriteLine("  status                    - Live dashboard");
        Console.WriteLine("  modems                    - List modems");
        Console.WriteLine("  modem <id>                - Modem detail + SIM + SMS stats");
        Console.WriteLine("  sms [modemId] [days]      - Recent SMS (optional filters)");
        Console.WriteLine("  sim <modemId>             - SIM history for modem");
        Console.WriteLine("  settle <modId> <amt> [note]- Adjust balance (logged as settlement)");
        Console.WriteLine("  report balance [id] [days]- Balance history");
        Console.WriteLine("  report sms [id] [days]    - SMS report by date range");
        Console.WriteLine("  users                     - List users");
        Console.WriteLine("  adduser <u> <p> [d]       - Add user");
        Console.WriteLine("  assign <uid> <mid>        - Assign modem to user");
        Console.WriteLine("  unassign <uid> <mid>      - Unassign modem");
        Console.WriteLine("  config                    - Show config");
        Console.WriteLine("  set-config <k> <v>        - Set config");
        Console.WriteLine("  exit                      - Exit");

    }

    private decimal GetLowBalanceThreshold()
    {
        try
        {
            var path = GetConfigPath();
            if (!File.Exists(path)) return 0;
            var json = File.ReadAllText(path);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict != null && dict.TryGetValue("alert.low_balance_threshold", out var val))
            {
                if (decimal.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var threshold))
                    return threshold;
            }
        }
        catch { }
        return 0;
    }

    private async Task ShowStatusAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var modems = await db.Modems.ToListAsync();
        var totalSms = await db.SmsRecords.CountAsync();
        var totalBalance = modems.Count > 0 ? (await db.SimCards.Where(s => s.IsActive).SumAsync(s => (decimal?)s.Balance)) ?? 0 : 0;

        var online = modems.Count(m => m.Status == ModemStatus.Online);
        var offline = modems.Count(m => m.Status == ModemStatus.Offline);

        Console.WriteLine();
        Console.WriteLine("=== FocusGate Dashboard ===");
        Console.WriteLine($"  Modems: {modems.Count} total, {online} online, {offline} offline");
        Console.WriteLine($"  SMS: {totalSms} total");
        Console.WriteLine($"  Balance: {totalBalance:F2} DZD");
        Console.WriteLine();

        var lowThreshold = GetLowBalanceThreshold();
        if (lowThreshold > 0 && totalBalance < lowThreshold)
            Console.WriteLine($"  *** LOW BALANCE WARNING: {totalBalance:F2} DZD (threshold: {lowThreshold:F2} DZD) ***");
        Console.WriteLine();

        if (modems.Count > 0)
        {
            Console.WriteLine($"  {"ID",-4} {"IMEI",-16} {"Status",-10} {"Port",-8} {"Phone",-14} {"Balance",-12}");
            Console.WriteLine("  " + new string('-', 66));
            foreach (var m in modems)
            {
                var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == m.Id && s.IsActive);
                var phone = sim?.PhoneNumber > 0 ? sim.PhoneNumber.ToString() : "-";
                var bal = sim?.Balance ?? 0;
                var balStr = bal.ToString("F2");
                if (lowThreshold > 0 && bal > 0 && bal < lowThreshold)
                    balStr = $"{bal:F2} !";
                Console.WriteLine($"  {m.Id,-4} {m.IMEI,-16} {m.Status,-10} {(m.ComPort ?? "-"),-8} {phone,-14} {balStr,-12}");
            }
        }
        Console.WriteLine();
    }

    private async Task ListModemsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var modems = await db.Modems.ToListAsync();
        Console.WriteLine($"{"ID",-4} {"IMEI",-16} {"Status",-10} {"Port",-8} {"Balance",-12}");
        Console.WriteLine(new string('-', 52));
        foreach (var m in modems)
        {
            var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == m.Id && s.IsActive);
            Console.WriteLine($"{m.Id,-4} {m.IMEI,-16} {m.Status,-10} {(m.ComPort ?? "-"),-8} {(sim?.Balance ?? 0),-12:F2}");
        }
    }

    private async Task ShowModemAsync(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("Usage: modem <id>"); return; }
        if (!int.TryParse(args[0], out var id)) { Console.WriteLine("Invalid ID"); return; }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var modem = await db.Modems.FindAsync(id);
        if (modem == null) { Console.WriteLine($"Modem {id} not found"); return; }

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == id && s.IsActive);
        var simIds = await db.SimCards.Where(s => s.ModemId == id).Select(s => s.Id).ToListAsync();
        var smsCount = await db.SmsRecords.CountAsync(s => simIds.Contains(s.SimCardId));
        var allSims = await db.SimCards.Where(s => s.ModemId == id).OrderByDescending(s => s.FirstSeen).ToListAsync();

        Console.WriteLine();
        Console.WriteLine($"=== Modem {modem.Id} ===");
        Console.WriteLine($"  IMEI:          {modem.IMEI}");
        Console.WriteLine($"  Status:        {modem.Status}");
        Console.WriteLine($"  ComPort:       {modem.ComPort ?? "-"}");
        Console.WriteLine($"  SMS:           {smsCount} total");
        Console.WriteLine();

        if (sim != null)
        {
            Console.WriteLine($"  Active SIM:");
            Console.WriteLine($"    IMSI:        {sim.IMSI}");
            Console.WriteLine($"    Phone:       {(sim.PhoneNumber > 0 ? sim.PhoneNumber.ToString() : "-")}");
            Console.WriteLine($"    Balance:     {sim.Balance:F2} DZD");
            Console.WriteLine($"    Verified:    {sim.VerifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-"}");
            Console.WriteLine($"    First Seen:  {sim.FirstSeen:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"    Last Seen:   {sim.LastSeen:yyyy-MM-dd HH:mm}");
        }
        Console.WriteLine();

        if (allSims.Count > 1)
        {
            Console.WriteLine($"  SIM History ({allSims.Count} SIMs):");
            foreach (var s in allSims)
            {
                var removed = s.RemovedAt?.ToString("yyyy-MM-dd HH:mm") ?? "active";
                var phone = s.PhoneNumber > 0 ? s.PhoneNumber.ToString() : "-";
                Console.WriteLine($"    {s.IMSI} | {phone} | {s.Balance:F2} DZD | {s.FirstSeen:yyyy-MM-dd} -> {removed}");
            }
            Console.WriteLine();
        }
    }

    private async Task ListSmsAsync(string[] args)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var query = db.SmsRecords
            .Include(s => s.SimCard);

        var sms = await query.OrderByDescending(s => s.ReceivedAt).Take(20).ToListAsync();
        Console.WriteLine($"{"ID",-6} {"Time",-19} {"SIM",-6} {"Sender",-14} {"Content",-24}");
        Console.WriteLine(new string('-', 72));
        foreach (var s in sms)
        {
            var content = s.Content.Length > 24 ? s.Content[..24] + "..." : s.Content;
            Console.WriteLine($"{s.Id,-6} {s.ReceivedAt:yyyy-MM-dd HH:mm:ss,-19} {s.SimCardId,-6} {s.SenderNumber,-14} {content,-24}");
        }
    }

    private async Task ShowSimAsync(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("Usage: sim <modemId>"); return; }
        if (!int.TryParse(args[0], out var modemId)) { Console.WriteLine("Invalid modem ID"); return; }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var modem = await db.Modems.FindAsync(modemId);
        if (modem == null) { Console.WriteLine($"Modem {modemId} not found"); return; }

        var sims = await db.SimCards.Where(s => s.ModemId == modemId).OrderByDescending(s => s.FirstSeen).ToListAsync();
        Console.WriteLine($"SIM History for Modem {modemId} (IMEI: {modem.IMEI}):");
        Console.WriteLine($"{"IMSI",-16} {"Phone",-14} {"Balance",-10} {"First Seen",-12} {"Last Seen",-12} {"Status",-10}");
        Console.WriteLine(new string('-', 76));
        foreach (var s in sims)
        {
            var status = s.IsActive ? "active" : s.RemovedAt?.ToString("yyyy-MM-dd") ?? "removed";
            var phone = s.PhoneNumber > 0 ? s.PhoneNumber.ToString() : "-";
            Console.WriteLine($"{s.IMSI,-16} {phone,-14} {s.Balance,-10:F2} {s.FirstSeen:yyyy-MM-dd,-12} {s.LastSeen:yyyy-MM-dd,-12} {status,-10}");
        }
        if (sims.Count == 0) Console.WriteLine("No SIM history.");
    }

    private string GetConfigPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "data", "config.json");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir, "FocusGate.sln");
            if (File.Exists(candidate))
            {
                var dataDir = Path.Combine(dir, "data");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                return Path.Combine(dataDir, "config.json");
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "data", "config.json");
    }

    private async Task ListUsersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var users = await db.Users.Include(u => u.UserModems).ToListAsync();
        Console.WriteLine($"{"ID",-4} {"Username",-16} {"Display",-20} {"Role",-7} {"Active",-7} {"Modems"}");
        Console.WriteLine(new string('-', 62));
        foreach (var u in users)
        {
            var modemCount = u.UserModems.Count(um => um.RemovedAt == null);
            Console.WriteLine($"{u.Id,-4} {u.Username,-16} {u.DisplayName,-20} {u.Role,-7} {u.IsActive,-7} {modemCount}");
        }
        if (users.Count == 0) Console.WriteLine("No users.");
    }

    private async Task AddUserAsync(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Usage: adduser <username> <password> [displayName]"); return; }
        var username = args[0];
        var password = args[1];
        var display = args.Length > 2 ? string.Join(" ", args.Skip(2)) : username;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        if (await db.Users.AnyAsync(u => u.Username == username))
        {
            Console.WriteLine($"User '{username}' already exists.");
            return;
        }

        var user = new User
        {
            Username = username,
            Password = password,
            DisplayName = display,
            Role = UserRole.User,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(_ct);
        Console.WriteLine($"User '{username}' created (ID={user.Id}).");
    }

    private async Task AssignModemAsync(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Usage: assign <userId> <modemId>"); return; }
        if (!long.TryParse(args[0], out var userId)) { Console.WriteLine("Invalid user ID."); return; }
        if (!int.TryParse(args[1], out var modemId)) { Console.WriteLine("Invalid modem ID."); return; }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var user = await db.Users.FindAsync(userId);
        if (user == null) { Console.WriteLine("User not found."); return; }
        var modem = await db.Modems.FindAsync(modemId);
        if (modem == null) { Console.WriteLine("Modem not found."); return; }

        var existing = await db.UserModems.FirstOrDefaultAsync(um => um.UserId == userId && um.ModemId == modemId);
        if (existing != null)
        {
            if (existing.RemovedAt == null) { Console.WriteLine("Already assigned."); return; }
            existing.RemovedAt = null;
        }
        else
        {
            db.UserModems.Add(new UserModem { UserId = userId, ModemId = modemId });
        }
        await db.SaveChangesAsync(_ct);
        Console.WriteLine($"Modem {modemId} assigned to user {user.Username}.");
    }

    private async Task UnassignModemAsync(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Usage: unassign <userId> <modemId>"); return; }
        if (!long.TryParse(args[0], out var userId)) { Console.WriteLine("Invalid user ID."); return; }
        if (!int.TryParse(args[1], out var modemId)) { Console.WriteLine("Invalid modem ID."); return; }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var um = await db.UserModems.FirstOrDefaultAsync(x => x.UserId == userId && x.ModemId == modemId);
        if (um == null || um.RemovedAt != null) { Console.WriteLine("Not currently assigned."); return; }

        um.RemovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(_ct);
        Console.WriteLine($"Modem {modemId} unassigned from user {userId}.");
    }

    private async Task ShowConfigAsync()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) { Console.WriteLine("No config file."); return; }
        var json = await File.ReadAllTextAsync(path);
        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (dict == null) return;
        Console.WriteLine($"{"Key",-35} {"Value",-30}");
        Console.WriteLine(new string('-', 65));
        foreach (var kv in dict) Console.WriteLine($"{kv.Key,-35} {kv.Value,-30}");
    }

    private async Task SetConfigAsync(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Usage: set-config <key> <value>"); return; }
        var path = GetConfigPath();
        Dictionary<string, string> dict;
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path);
            dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        else
        {
            dict = new();
        }
        dict[args[0]] = args[1];
        var updated = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, updated);
        Console.WriteLine($"Set: {args[0]} = {args[1]}");
    }

    private async Task SettleAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: settle <modemId> <amount> [note]");
            Console.WriteLine("  amount: +50.00 to add, -30.00 to subtract");
            Console.WriteLine("  note:   optional reason (e.g. 'cash deposit')");
            return;
        }
        if (!int.TryParse(args[0], out var modemId)) { Console.WriteLine("Invalid modem ID."); return; }
        if (!decimal.TryParse(args[1], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
        { Console.WriteLine("Invalid amount. Use +50.00 or -30.00"); return; }

        var note = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "manual settlement";

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        var modem = await db.Modems.FindAsync(modemId);
        if (modem == null) { Console.WriteLine($"Modem {modemId} not found."); return; }

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive);
        if (sim == null) { Console.WriteLine($"No active SIM on modem {modemId}."); return; }

        var oldBalance = sim.Balance;
        var newBalance = oldBalance + amount;

        if (newBalance < 0)
        {
            Console.WriteLine($"Warning: balance would go negative ({newBalance:F2} DZD). Aborting.");
            return;
        }

        sim.Balance = newBalance;
        sim.VerifiedAt = DateTime.UtcNow;
        sim.LastSeen = DateTime.UtcNow;

        var userId = await ResolveUserIdForModemAsync(db, modemId);

        db.BalanceHistories.Add(new BalanceHistory
        {
            SimCardId = sim.Id,
            ModemId = modemId,
            UserId = userId,
            Balance = newBalance,
            PreviousBalance = oldBalance,
            Source = BalanceSource.Settlement,
            RecordedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(_ct);

        var sign = amount >= 0 ? "+" : "";
        Console.WriteLine($"Settled modem {modemId}: {oldBalance:F2} DZD → {newBalance:F2} DZD ({sign}{amount:F2} DZD) [{note}]");
    }

    private async Task ReportAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: report <type> [modemId] [days]");
            Console.WriteLine("  type: balance | sms");
            return;
        }

        var reportType = args[0].ToLower();
        int? modemId = args.Length > 1 && int.TryParse(args[1], out var mid) ? mid : null;
        int days = args.Length > 2 && int.TryParse(args[2], out var d) ? d : 7;

        var since = DateTime.UtcNow.AddDays(-days);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        switch (reportType)
        {
            case "balance":
                await ReportBalanceAsync(db, modemId, days, since);
                break;
            case "sms":
                await ReportSmsAsync(db, modemId, days, since);
                break;
            default:
                Console.WriteLine($"Unknown report type: {reportType}. Use 'balance' or 'sms'.");
                break;
        }
    }

    private async Task ReportBalanceAsync(FocusGateDbContext db, int? modemId, int days, DateTime since)
    {
        Console.WriteLine($"\n=== Balance History (last {days} days) ===\n");

        var query = db.BalanceHistories
            .Include(b => b.SimCard)
            .Where(b => b.RecordedAt >= since);

        if (modemId.HasValue)
            query = query.Where(b => b.ModemId == modemId.Value);

        var entries = await query.OrderByDescending(b => b.RecordedAt).ToListAsync();

        if (entries.Count == 0)
        {
            Console.WriteLine("No balance records in this period.");
            return;
        }

        Console.WriteLine($"{"Date",-20} {"Modem",-7} {"Phone",-14} {"Prev Bal",-12} {"New Bal",-12} {"Change",-12} {"Source",-10}");
        Console.WriteLine(new string('-', 87));

        foreach (var b in entries)
        {
            var phone = b.SimCard?.PhoneNumber > 0 ? b.SimCard.PhoneNumber.ToString() : "-";
            var change = b.PreviousBalance.HasValue ? b.Balance - b.PreviousBalance.Value : 0m;
            var sign = change >= 0 ? "+" : "";
            Console.WriteLine($"{b.RecordedAt:yyyy-MM-dd HH:mm,-20} {b.ModemId,-7} {phone,-14} {(b.PreviousBalance?.ToString("F2") ?? "-"),-12} {b.Balance:F2,-12} {sign}{change:F2,-12} {b.Source,-10}");
        }

        var first = entries.Last();
        var last = entries.First();
        var totalChange = last.Balance - first.Balance;
        Console.WriteLine($"\n  Starting: {first.Balance:F2} DZD | Current: {last.Balance:F2} DZD | Net change: {(totalChange >= 0 ? "+" : "")}{totalChange:F2} DZD");
    }

    private async Task ReportSmsAsync(FocusGateDbContext db, int? modemId, int days, DateTime since)
    {
        Console.WriteLine($"\n=== SMS Report (last {days} days) ===\n");

        var simQuery = db.SimCards.AsQueryable();
        if (modemId.HasValue)
            simQuery = simQuery.Where(s => s.ModemId == modemId.Value);

        var simIds = await simQuery.Select(s => s.Id).ToListAsync();

        var smsQuery = db.SmsRecords
            .Where(s => simIds.Contains(s.SimCardId) && s.ReceivedAt >= since);

        var sms = await smsQuery.ToListAsync();
        var total = sms.Count;

        if (total == 0)
        {
            Console.WriteLine("No SMS in this period.");
            return;
        }

        var bySender = sms.GroupBy(s => s.SenderNumber)
            .OrderByDescending(g => g.Count())
            .Take(10);

        var byDay = sms.GroupBy(s => s.ReceivedAt.Date)
            .OrderBy(g => g.Key);

        Console.WriteLine($"  Total SMS: {total}");
        Console.WriteLine($"  Date range: {since:yyyy-MM-dd} to {DateTime.UtcNow:yyyy-MM-dd}");
        Console.WriteLine();

        Console.WriteLine("  By Day:");
        Console.WriteLine($"  {"Date",-14} {"Count",-8}");
        Console.WriteLine("  " + new string('-', 22));
        foreach (var g in byDay)
            Console.WriteLine($"  {g.Key:yyyy-MM-dd,-14} {g.Count(),-8}");

        Console.WriteLine();
        Console.WriteLine("  Top Senders:");
        Console.WriteLine($"  {"Sender",-22} {"Count",-8} {"First",-12} {"Last",-12}");
        Console.WriteLine("  " + new string('-', 54));
        foreach (var g in bySender)
        {
            var first = g.MinBy(s => s.ReceivedAt);
            var last = g.MaxBy(s => s.ReceivedAt);
            Console.WriteLine($"  {g.Key,-22} {g.Count(),-8} {first?.ReceivedAt:yyyy-MM-dd,-12} {last?.ReceivedAt:yyyy-MM-dd,-12}");
        }
        Console.WriteLine();
    }

    private static async Task<long> ResolveUserIdForModemAsync(FocusGateDbContext db, int modemId)
    {
        var um = await db.UserModems.FirstOrDefaultAsync(x => x.ModemId == modemId && x.RemovedAt == null);
        return um?.UserId ?? 1;
    }
}
