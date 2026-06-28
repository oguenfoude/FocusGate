using FocusGate.Core.Enums;
using FocusGate.Core.Services;
using FocusGate.Infrastructure;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var dataDir = PathService.DataDirectory;
Directory.CreateDirectory(dataDir);

ConfigMerger.EnsureConfig(Path.Combine(dataDir, "config.json"));

var configPath = Path.Combine(dataDir, "config.json");
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile(configPath, optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

var configuration = configBuilder.Build();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("MongoDB", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(dataDir, "logs", "dashboard-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddFocusGateDashboard(configuration, dataDir);
builder.Services.AddLocalization();
builder.Services.AddRazorPages();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5080);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    DatabaseInitializer.Initialize(db, logger);

    var machineIdService = scope.ServiceProvider.GetRequiredService<Action<FocusGateDbContext>>();
    machineIdService(db);

    // Seed test data for customer and withdrawal requests if none exist
    if (!db.Users.Any(u => u.Role == FocusGate.Core.Enums.UserRole.User))
    {
        var testUser = new FocusGate.Core.Models.User
        {
            Username = "test_customer",
            Password = "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b", // hashed "1"
            DisplayName = "Test Customer",
            Role = FocusGate.Core.Enums.UserRole.User,
            IsActive = true,
            Balance = 5000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MachineId = db.MachineId ?? ""
        };
        db.Users.Add(testUser);
        db.SaveChanges();
    }

    var customer = db.Users.FirstOrDefault(u => u.Role == FocusGate.Core.Enums.UserRole.User);
    if (customer != null && !db.WithdrawalRequests.Any())
    {
        db.WithdrawalRequests.Add(new FocusGate.Core.Models.WithdrawalRequest
        {
            UserId = customer.Id,
            Amount = 1500m,
            Status = FocusGate.Core.Enums.WithdrawalStatus.Pending,
            Note = "Requesting payout for testing",
            RequestedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MachineId = db.MachineId ?? ""
        });
        db.SaveChanges();
    }
}

var cts = new CancellationTokenSource();
var writeChannel = app.Services.GetRequiredService<DatabaseWriteChannel>();
writeChannel.Start(cts.Token);

app.Lifetime.ApplicationStopping.Register(async () =>
{
    cts.Cancel();
    await writeChannel.CompleteAsync();
});

app.UseStaticFiles();

var supportedCultures = new[] { "en", "fr", "ar" };
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en")
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures));

app.MapRazorPages();

Log.Information("FocusGate Dashboard starting on http://localhost:5080");

app.Run();
