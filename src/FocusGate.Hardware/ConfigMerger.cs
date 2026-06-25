using System.Text.Json;

namespace FocusGate.Hardware;

public static class ConfigMerger
{
    private static readonly Dictionary<string, string> RequiredKeys = new()
    {
        ["gateway.name"] = "FocusGate",
        ["gateway.admin.password"] = "Admin@FocusGate2024",
        ["machine.id"] = "",
        ["sms.verification.enabled"] = "true",
        ["sms.verification.threshold"] = "50000",
        ["sms.verification.interval"] = "60",
        ["sms.parser.strict"] = "false",
        ["balance.limit.default"] = "50000",
        ["alert.low_balance_threshold"] = "10",
        ["modem.watchdog.interval"] = "30",
        ["modem.sms.poll.interval"] = "30",
        ["modem.balance.poll.interval"] = "30",
        ["serial.read.timeout"] = "5000",
        ["modem.ussd.phone_code"] = "*101#",
        ["modem.ussd.balance_code"] = "*222#",
        ["modem.ussd.dcs"] = "15",
        ["mongodb.uri"] = "mongodb+srv://admin:admin@cluster0.ldndrwe.mongodb.net/?appName=Cluster0",
        ["mongodb.database"] = "focusgate",
        ["sync.interval_seconds"] = "30",
        ["app.version"] = "1.0.0"
    };

    public static void EnsureConfig(string configPath)
    {
        var existing = new Dictionary<string, string>();

        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
                existing[prop.Name] = prop.Value.GetString() ?? "";
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

        if (existing.TryGetValue("mongodb.uri", out var currentUri))
        {
            if (currentUri.Contains("ac-8knjxta-shard") || currentUri.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase))
            {
                existing["mongodb.uri"] = RequiredKeys["mongodb.uri"];
                changed = true;
                Console.WriteLine("[ConfigMerger] Migrated MongoDB URI to SRV connection");
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
            File.WriteAllText(configPath, json);
        }
    }
}
