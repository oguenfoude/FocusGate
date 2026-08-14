using System.Text.Json;
using FocusGate.Core.Services;

namespace FocusGate.Infrastructure.Services;

public class MeetMobTokenStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, MeetMobToken> _tokens = new();
    private bool _loaded;

    public MeetMobTokenStore()
    {
        _filePath = Path.Combine(PathService.DataDirectory, "meetmob-tokens.json");
    }

    public async Task<MeetMobToken?> GetAsync(string key)
    {
        await LoadAsync();
        if (_tokens.TryGetValue(key, out var token) && token.ExpiresAt > DateTime.UtcNow)
            return token;
        return null;
    }

    public async Task SaveAsync(string key, MeetMobToken token)
    {
        await LoadAsync();
        _tokens[key] = token;
        await PersistAsync();
    }

    public async Task RemoveAsync(string key)
    {
        await LoadAsync();
        if (_tokens.Remove(key))
            await PersistAsync();
    }

    public async Task<Dictionary<string, MeetMobToken>> GetAllAsync()
    {
        await LoadAsync();
        return new Dictionary<string, MeetMobToken>(_tokens);
    }

    public async Task<int> PurgeExpiredAsync()
    {
        await LoadAsync();
        await _lock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var expired = _tokens.Where(kvp => kvp.Value.ExpiresAt <= now).Select(kvp => kvp.Key).ToList();
            foreach (var key in expired)
                _tokens.Remove(key);
            if (expired.Count > 0)
                await PersistAsync();
            return expired.Count;
        }
        finally { _lock.Release(); }
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_loaded) return;
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_filePath);
                    _tokens = JsonSerializer.Deserialize<Dictionary<string, MeetMobToken>>(json) ?? new();
                }
                catch
                {
                    _tokens = new();
                }
            }
            _loaded = true;
        }
        finally { _lock.Release(); }
    }

    private async Task PersistAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var sorted = _tokens.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(sorted, options);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally { _lock.Release(); }
    }
}

public class MeetMobToken
{
    public string Phone { get; set; } = string.Empty;
    public string CsrfToken { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string SubscriberKey { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime LastLoginAttempt { get; set; }
    public int FailedAttempts { get; set; }
}
