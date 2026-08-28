using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace WTangent;

/// <summary>组件索引条目（components.json：只存 别名→仓库 映射，winget 式；GitHub 维护，空壳拉取缓存）</summary>
public sealed record IndexEntry(string Alias, string Repo);

// 本文件 = ComponentManager 索引部分（职责地图见 ComponentManager.cs）
public static partial class ComponentManager
{
    /// <summary>索引源（GitHub raw，主仓 components.json）</summary>
    public const string IndexUrl = "https://raw.githubusercontent.com/WTangent-Org/WTangent/main/components.json";

    /// <summary>离线兜底索引（仅首次无缓存时用；与 components.json 同序 = 桌面优先级）</summary>
    private static readonly IndexEntry[] FallbackIndex =
    [
        new("gui",    "WTangent.Gui"),
        new("tui",    "WTangent.Tui"),
        new("client", "WTangent.Client"),
        new("serve",  "WTangent.Server"),
        new("git",    "WTangent.GitCmd"),
    ];

    /// <summary>远程组件清单的本地缓存（内存 > 磁盘 > 兜底；UpdateIndex 成功时同步刷新内存）。
    /// 只回答「registry 里有哪些组件可装 / 别名→仓库 / 展示优先级」，不代表本地装了什么——已装集合见 <see cref="InstalledComponents"/></summary>
    public static List<IndexEntry> Index
    {
        get => field ??= LoadIndex();
        private set;
    }

    /// <summary>刷新索引：拉 GitHub components.json 写缓存；quiet 时失败静默</summary>
    public static async Task<bool> UpdateIndexAsync(bool quiet = false)
    {
        try
        {
            var json = await Http.GetStringAsync(IndexUrl);
            var list = JsonSerializer.Deserialize<List<IndexEntry>>(json, JsonOpts);
            if (list is not { Count: > 0 }) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(IndexFile)!);
            File.WriteAllText(IndexFile, json);
            Index = list;   // 同步内存缓存（否则本次进程还拿着旧索引）
            return true;
        }
        catch (Exception e)
        {
            if (!quiet)
                Console.Error.WriteLine($"[agent] 索引刷新失败：{e.Message}");
            return false;
        }
    }

    /// <summary>启动静默刷新索引（不查版本——更新由 agent upgrade 显式承担）</summary>
    public static Task RefreshIndexSilentlyAsync() => UpdateIndexAsync(quiet: true);

    private static List<IndexEntry> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexFile))
            {
                var list = JsonSerializer.Deserialize<List<IndexEntry>>(File.ReadAllText(IndexFile), JsonOpts);
                if (list is { Count: > 0 }) return list;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return [.. FallbackIndex];
    }

    /// <summary>组件展示优先级（= 索引顺序；不在索引的排最后）——索引只定序，不定已装成员</summary>
    public static int PriorityOf(string name)
    {
        var i = Index.FindIndex(e => e.Alias == name);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>组件表查找（别名）；未知名字打印提示并返回 false</summary>
    public static bool TryComponent(string alias,[NotNullWhen(true)] out IndexEntry? entry)
    {
        var hit = Index.FirstOrDefault(e => e.Alias == alias);
        if (hit is not null)
        {
            entry = hit;
            return true;
        }
        entry = null;
        Console.Error.WriteLine($"[wtangent] 未知组件 {alias}（wtangent update 刷新索引后重试）");
        return false;
    }
}
