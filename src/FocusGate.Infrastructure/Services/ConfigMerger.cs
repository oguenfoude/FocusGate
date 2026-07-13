using System.Text.Json;

namespace FocusGate.Infrastructure.Services;

public static class ConfigMerger
{
    private static readonly Dictionary<string, string> RequiredKeys = new()
    {
        ["gateway.name"] = "FocusGate",
        ["gateway.admin.password"] = "ChangeMeImmediately",
        ["machine.id"] = "",
        ["sms.verification.enabled"] = "true",
        ["sms.verification.threshold"] = "50000",
        ["sms.verification.interval"] = "60",
        ["sms.parser.strict"] = "false",
        ["balance.limit.default"] = "50000",
        ["alert.low_balance_threshold"] = "10",
        ["modem.watchdog.interval"] = "30",
        ["modem.sms.poll.interval"] = "30",

        ["serial.read.timeout"] = "5000",
        ["modem.ussd.phone_code"] = "*101#",
        ["modem.ussd.balance_code"] = "*222#",
        ["modem.ussd.dcs"] = "15",
        ["mongodb.uri"] = "mongodb+srv://admin:admin@cluster0.ycqvmop.mongodb.net/?appName=Cluster0",
        ["mongodb.database"] = "focusgate",
        ["sync.interval_seconds"] = "30",
        ["hilink.enabled"] = "true",
        ["hilink.scan_ips"] = "",
        ["hilink.probe_timeout_ms"] = "2000",
        ["at.enabled"] = "true",
        ["at.probe_timeout_ms"] = "8000",
        ["modem.timezone_offset_hours"] = "1",
        ["modem.max_count"] = "15",
        ["display.timezone_offset_hours"] = "",
        ["app.version"] = "1.0.0"
    };

    public static void EnsureConfig(string configPath)
    {
        var existing = new Dictionary<string, string>();

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    existing[prop.Name] = prop.Value.GetString() ?? "";
            }
            catch (JsonException ex)
            {
                var backupPath = configPath + ".corrupted";
                try
                {
                    File.Copy(configPath, backupPath, overwrite: true);
                    Console.WriteLine($"[ConfigMerger] Corrupted config backed up to {backupPath}: {ex.Message}");
                }
                catch { }
                existing.Clear();
            }
        }

        bool changed = false;
        foreach (var kvp in RequiredKeys)
        {
            if (!existing.ContainsKey(kvp.Key))
            {
                existing[kvp.Key] = kvp.Value;
                changed = true;
            }
        }

        var deadKeys = new[] { "huawei.hilink.auto_switch", "huawei.hilink.gateway_ips" };
        foreach (var dead in deadKeys)
        {
            if (existing.Remove(dead))
                changed = true;
        }

        if (changed || !File.Exists(configPath))
        {
            var sorted = existing.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(sorted, options);
            var tempPath = configPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, configPath, overwrite: true);
        }
    }
}
