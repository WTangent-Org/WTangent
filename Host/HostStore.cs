using System.Text.Json;
using WTangent.Core;

namespace WTangent.Host;

/// <summary>数据存储实现（%APPDATA%\agent 下数据文件）：remotes.json / credentials 等。
/// 写 = temp + rename 原子替换，防半写文件。</summary>
public sealed class HostStore : IAppStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent");

    private string PathOf(string name) => Path.Combine(_dir, name);

    public string? ReadText(string name)
    {
        try
        {
            var p = PathOf(name);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch { return null; }
    }

    public void WriteText(string name, string content)
    {
        Directory.CreateDirectory(_dir);
        var p = PathOf(name);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, p, overwrite: true);
    }

    public T? ReadJson<T>(string name)
    {
        var text = ReadText(name);
        if (text is null) return default;
        try { return JsonSerializer.Deserialize<T>(text); }
        catch { return default; }
    }

    public void WriteJson<T>(string name, T value) =>
        WriteText(name, JsonSerializer.Serialize(value, JsonOpts));
}
