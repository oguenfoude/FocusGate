namespace FocusGate.Core.Interfaces;

public interface IConfigProvider
{
    string Get(string key, string? defaultValue = null);
    T? Get<T>(string key, T? defaultValue = default);
}
