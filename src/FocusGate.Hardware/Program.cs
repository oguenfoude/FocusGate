using FocusGate.Core.Services;
using FocusGate.Hardware;
using FocusGate.Hardware.Services;
using FocusGate.Infrastructure;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

using var appCts = new CancellationTokenSource();
CancellationTokenSource? linkedCts = null;

var mutex = new System.Threading.Mutex(true, @"Global\FocusGate_Hardware", out bool createdNew);
if (!createdNew)
{
    try
    {
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", "FocusGate_Restart", System.IO.Pipes.PipeDirection.Out);
        await client.ConnectAsync(2000);
        await using var writer = new StreamWriter(client);
        await writer.WriteLineAsync("restart");
        await writer.FlushAsync();
    }
    catch { }
    await Task.Delay(2000);
}
try
{

var dataDir = PathService.DataDirectory;
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(PathService.LogsDirectory);

var licenseDst = Path.Combine(dataDir, "license.json");
if (!File.Exists(licenseDst))
{
    var licenseDir = AppContext.BaseDirectory;
    while (licenseDir != null)
    {
        var candidate = Path.Combine(licenseDir, "license.json");
        if (File.Exists(candidate))
        {
            File.Copy(candidate, licenseDst);
            break;
        }
        licenseDir = Directory.GetParent(licenseDir)?.FullName;
    }
}

var configPath = PathService.ConfigPath;
ConfigMerger.EnsureConfig(configPath);

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        if (File.Exists(configPath))
        {
            cfg.AddJsonFile(configPath, optional: true, reloadOnChange: false);
        }
    })
    .UseSerilog((ctx, lc) => lc
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("MongoDB", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(PathService.LogsDirectory, "focusgate-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30))
    .ConfigureServices((ctx, services) =>
    {
        services.AddInfrastructure(ctx.Configuration, dataDir);
        services.AddHardware();
        services.AddHostedService<ModemOrchestrator>();
        services.AddHostedService<ConsoleCommandHandler>();
        services.AddHostedService<RestartService>();
    })
    .Build();

using var scope = host.Services.CreateScope();
var context = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
var machineInfo = scope.ServiceProvider.GetRequiredService<MachineInfoService>();

try
{
    DatabaseInitializer.Initialize(context, logger);

    var machineIdConfig = host.Services.GetRequiredService<IConfiguration>()["machine.id"] ?? "";
    context.MachineId = string.IsNullOrEmpty(machineIdConfig) ? machineInfo.MachineId : machineIdConfig;

    if (string.IsNullOrEmpty(machineIdConfig))
    {
        PersistMachineId(configPath, context.MachineId);
        logger.LogInformation("MachineId persisted to config: {Machine}", context.MachineId);
    }

    if (!LicenseService.VerifyLicense(dataDir, context.MachineId, out var licenseError))
    {
        logger.LogWarning("License invalid, generating new license for machine {Machine}...", context.MachineId);

        var generated = LicenseService.GenerateForMachine(context.MachineId);
        var licensePath = Path.Combine(dataDir, "license.json");
        File.WriteAllText(licensePath, System.Text.Json.JsonSerializer.Serialize(generated, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        if (!LicenseService.VerifyLicense(dataDir, context.MachineId, out var retryError))
        {
            logger.LogCritical("LICENSE ERROR: {Error}", retryError);
            ShowErrorDialog("FocusGate - License Error",
                $"License verification failed.\n\n" +
                $"Machine ID: {context.MachineId}\n\n" +
                $"Please contact the developer:\n" +
                $"Email: ouguenfoude@gmail.com");
        }
        else
        {
            logger.LogInformation("New license generated and verified for machine {Machine}", context.MachineId);
        }
    }
    else
    {
        logger.LogInformation("License verified for machine {Machine}", context.MachineId);
    }

    var writeChannel = scope.ServiceProvider.GetRequiredService<DatabaseWriteChannel>();
    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token);
    writeChannel.Start(linkedCts.Token);

    logger.LogInformation("FocusGate started | DB: {DbPath} | Machine: {Machine}",
        PathService.DatabasePath, context.MachineId);

    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        _ = writeChannel.CompleteAsync();
        linkedCts.Cancel();
    });

    var desktopPath = "";
    var searchDir = AppContext.BaseDirectory;
    while (searchDir != null)
    {
        var candidate = Path.Combine(searchDir, "FocusGate.Desktop.exe");
        if (File.Exists(candidate)) { desktopPath = candidate; break; }
        candidate = Path.Combine(searchDir, "Desktop", "FocusGate.Desktop.exe");
        if (File.Exists(candidate)) { desktopPath = candidate; break; }
        searchDir = Directory.GetParent(searchDir)?.FullName;
    }

    if (string.IsNullOrEmpty(desktopPath))
    {
        var srcDir = AppContext.BaseDirectory;
        while (srcDir != null)
        {
            var candidate = Path.Combine(srcDir, "src", "FocusGate.Desktop", "bin", "Debug", "net10.0-windows", "FocusGate.Desktop.exe");
            if (File.Exists(candidate)) { desktopPath = candidate; break; }
            candidate = Path.Combine(srcDir, "src", "FocusGate.Desktop", "bin", "Release", "net10.0-windows", "FocusGate.Desktop.exe");
            if (File.Exists(candidate)) { desktopPath = candidate; break; }
            srcDir = Directory.GetParent(srcDir)?.FullName;
        }
    }

    if (File.Exists(desktopPath))
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = desktopPath,
                UseShellExecute = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            });
            logger.LogInformation("Desktop monitor launched");
        }
        catch (Exception dex)
        {
            logger.LogWarning(dex, "Could not launch Desktop monitor");
        }
    }
}
catch (Exception ex)
{
    ShowErrorDialog("FocusGate - Startup Error", $"Failed to start FocusGate:\n\n{ex.Message}\n\n{ex.InnerException?.Message}");
}

await host.RunAsync();

linkedCts?.Cancel();
linkedCts?.Dispose();
}
finally
{
    mutex.ReleaseMutex();
    mutex.Dispose();
}

static void PersistMachineId(string configPath, string machineId)
{
    try
    {
        var json = File.Exists(configPath) ? File.ReadAllText(configPath) : "{}";
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var dict = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.GetString() ?? "";
        dict["machine.id"] = machineId;
        var sorted = dict.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value);
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(sorted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    catch { }
}

static void ShowErrorDialog(string title, string message)
{
    try
    {
        MessageBoxW(IntPtr.Zero, message, title, 0x10);
    }
    catch { }
}

[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
