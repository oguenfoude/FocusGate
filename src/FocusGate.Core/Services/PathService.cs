namespace FocusGate.Core.Services;

public static class PathService
{
    private static readonly Lazy<string> _dataDir = new(ResolveDataDir);

    public static string DataDirectory => _dataDir.Value;
    public static string DatabasePath => Path.Combine(DataDirectory, "focusgate.db");
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    private static string ResolveDataDir()
    {
        var envOverride = Environment.GetEnvironmentVariable("FOCUSGATE_DATA");
        if (!string.IsNullOrEmpty(envOverride) && Directory.Exists(envOverride))
            return envOverride;

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var data = Path.Combine(roaming, "FocusGate");
        Directory.CreateDirectory(data);
        return data;
    }
}
