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

    public async Task<MeetMobToken?> GetAsync(string imsi)
    {
        await LoadAsync();
        if (_tokens.TryGetValue(imsi, out var token) && token.ExpiresAt > DateTime.UtcNow)
            return token;
        return null;
    }

    public async Task SaveAsync(string imsi, MeetMobToken token)
    {
        await LoadAsync();
        _tokens[imsi] = token;
        await PersistAsync();
    }

    public async Task RemoveAsync(string imsi)
    {
        await LoadAsync();
        if (_tokens.Remove(imsi))
            await PersistAsync();
    }

    public async Task<Dictionary<string, MeetMobToken>> GetAllAsync()
    {
        await LoadAsync();
        return new Dictionary<string, MeetMobToken>(_tokens);
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
