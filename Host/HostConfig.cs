using System.Text.Json;
using WTangent.Core;

namespace WTangent.Host;

/// <summary>配置实现（%APPDATA%\agent\config.json）：内存字典 + 原子写持久化；变更发 config.changed。</summary>
public sealed class HostConfig : IConfig
{
    private readonly IEventBus _events;
    private readonly object _lock = new();
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "config.json");
    private readonly Dictionary<string, object?> _data = new(StringComparer.OrdinalIgnoreCase);

    public HostConfig(IEventBus events)
    {
        _events = events;
        try
        {
            if (File.Exists(_file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_file));
                foreach (var p in doc.RootElement.EnumerateObject())
                    _data[p.Name] = p.Value.Clone();
            }
        }
        catch { /* 配置损坏按空处理 */ }
    }

    public T? Get<T>(string key)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var v) || v is null) return default;
            try { return (T)Convert.ChangeType(v, typeof(T)); }
            catch { return JsonSerializer.Deserialize<T>(((JsonElement)v).GetRawText()); }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _data[key] = value;
            Save();
        }
        _events.Publish("config.changed", key);
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_data.Remove(key)) Save();
        }
        _events.Publish("config.changed", key);
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(dir);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _file, overwrite: true);
    }
}
