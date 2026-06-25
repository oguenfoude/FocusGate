using System.Text.Json;
using FocusGate.Core.Interfaces;

namespace FocusGate.Infrastructure.Services;

public class JsonConfigProvider : IConfigProvider
{
    private readonly string _configPath;
    private Dictionary<string, string> _config = new();
    private DateTime _lastLoaded = DateTime.MinValue;

    public JsonConfigProvider(string configPath)
    {
        _configPath = configPath;
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_configPath))
        {
            _config = new Dictionary<string, string>();
            return;
        }

        var json = File.ReadAllText(_configPath);
        _config = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        _lastLoaded = DateTime.UtcNow;
    }

    private void ReloadIfNeeded()
    {
        if ((DateTime.UtcNow - _lastLoaded).TotalSeconds > 5 && File.Exists(_configPath))
        {
            var lastWrite = File.GetLastWriteTimeUtc(_configPath);
            if (lastWrite > _lastLoaded)
                Load();
        }
    }

    public string Get(string key, string? defaultValue = null)
    {
        ReloadIfNeeded();
        return _config.TryGetValue(key, out var value) ? value : defaultValue ?? string.Empty;
    }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        var str = Get(key, null);
        if (string.IsNullOrEmpty(str)) return defaultValue;
        try { return (T)Convert.ChangeType(str, typeof(T))!; }
        catch { return defaultValue; }
    }
}
