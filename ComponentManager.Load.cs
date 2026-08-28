using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using WTangent.Core;

namespace WTangent;

/// <summary>组件入口文件（agent-component.json：组件仓库根自声明——资产名等元数据；
/// 类型已废弃：行为由 IEntry 能力决定，不再按 type 分流；
/// MinCore = 组件编译时引用的 Core 版本（生成器构建时自动写入），install/upgrade 时校验空壳内置 Core ≥ 它；
/// Depends = 组件间编译期互引的运行时声明（别名→最低版本；csproj ComponentDepends 属性 → 生成器写入）；
/// Core 是每个组件的隐式必备依赖（即 minCore），不在 Depends 里声明）</summary>
public sealed record ManifestEntry(string Name, string Asset, string? MinCore = null,
    Dictionary<string, string>? Depends = null);

// 本文件 = ComponentManager 加载部分（职责地图见 ComponentManager.cs）
public static partial class ComponentManager
{
    /// <summary>空壳内置 Core 版本（= 组件 manifest MinCore 的比较基准；
    /// 单 ALC 统一，组件运行时用到的 Core 就是这份，与空壳版本同升同降）</summary>
    public static readonly Version CoreVersion =
        typeof(ILogger).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>已加载组件的依赖解析器（各组件 deps.json 驱动；TryLoadComponent 时注册，键 = 组件别名）</summary>
    private static readonly ConcurrentDictionary<string, AssemblyDependencyResolver> Resolvers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>组件是否已装（纯本地：看入口 dll；manifest 无本地缓存时回退 .installed 标记；不联网不查索引）</summary>
    public static bool IsInstalled(string name)
    {
        var dir = ComponentDir(name);
        if (!Directory.Exists(dir)) return false;
        var manifest = ReadLocalManifest(name);
        return File.Exists(manifest is null ? Path.Combine(dir, ".installed") : Path.Combine(dir, manifest.Asset + ".dll"));
    }

    /// <summary>已装组件别名（纯本地：扫组件目录逐个 IsInstalled；与远程索引无关——索引不代表本地装了什么）</summary>
    public static List<string> InstalledComponents()
    {
        if (!Directory.Exists(ComponentsDir)) return [];
        var list = new List<string>();
        foreach (var dir in Directory.GetDirectories(ComponentsDir))
        {
            if (Path.GetFileName(dir) is { } name && IsInstalled(name)) list.Add(name);
        }
        return list;
    }

    /// <summary>加载组件 dll（默认上下文，使编译期引用绑定到下载的 dll）；失败提示并返回 false</summary>
    public static bool TryLoadComponent(string name,[NotNullWhen(true)] out Assembly? asm)
    {
        var manifest = GetManifest(name);
        var dll = manifest is null
            ? Path.Combine(ComponentDir(name), name + ".dll")
            : Path.Combine(ComponentDir(name), manifest.Asset + ".dll");
        if (!File.Exists(dll))
        {
            asm = null;
            return false;
        }
        try
        {
            asm = Assembly.LoadFrom(dll);
            Resolvers[name] = new AssemblyDependencyResolver(dll);   // 依赖解析走该组件自己的 deps.json
            return true;
        }
        catch (Exception e)
        {
            asm = null;
            Console.Error.WriteLine($"[wtangent] 加载 {name} 组件失败：{e.Message}");
            return false;
        }
    }

    /// <summary>加载组件入口（找 IEntry 实现 → 构造注入 App 实例化；启动见 <see cref="StartEntry"/>）；失败提示并返回 null</summary>
    public static IEntry? LoadEntry(string name, Application app)
    {
        if (!TryLoadComponent(name, out var asm)) return null;
        var entryType = FindEntryType(asm);
        if (entryType is null)
        {
            Console.Error.WriteLine($"[wtangent] 组件缺少入口（实现 IEntry 的类型）：{asm.GetName().Name}");
            return null;
        }
        try
        {
            return (IEntry)Activator.CreateInstance(entryType, app)!;   // 构造注入
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[wtangent] 组件 {name} 入口构造失败：{e.Message}");
            return null;
        }
    }

    /// <summary>启动组件入口（SupportAsyncStart 分流：异步的 Task.Run 并行、同步的当场串行——启动分流，非 sync-over-async）</summary>
    public static Task StartEntry(IEntry entry) =>
        entry.SupportAsyncStart ? Task.Run(entry.StartAsync) : entry.StartAsync();

    /// <summary>反射推导组件入口类型（约定：实现 IEntry 的 public 非抽象类）；依赖缺失时容错返回 null</summary>
    public static Type? FindEntryType(Assembly asm)
    {
        try
        {
            return asm.GetTypes().FirstOrDefault(t => t is { IsPublic: true, IsAbstract: false }
                && typeof(IEntry).IsAssignableFrom(t));
        }
        catch (ReflectionTypeLoadException)
        {
            return null;   // 组件依赖缺失（版本不符等）：命令注册走占位，不崩
        }
    }

    /// <summary>组件依赖解析：优先各组件自己的 deps.json（AssemblyDependencyResolver，确定性、按组件隔离版本）；
    /// 兜底按名直扫组件目录（无 deps.json 的旧包）。
    /// Core / System.CommandLine 等空壳已加载的程序集由 ALC 按简单名统一，永远到不了这里。</summary>
    public static Assembly? ResolveComponentDependency(AssemblyLoadContext ctx, AssemblyName name)
    {
        foreach (var resolver in Resolvers.Values)
        {
            var p = resolver.ResolveAssemblyToPath(name);
            if (p is not null) return ctx.LoadFromAssemblyPath(p);
        }
        if (!Directory.Exists(ComponentsDir)) return null;
        foreach (var dir in Directory.GetDirectories(ComponentsDir))
        {
            var p = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
        }
        return null;
    }

    /// <summary>组件入口文件（本地缓存优先；缺失时从仓库拉取并缓存到 components\{name}\agent-component.json）</summary>
    public static ManifestEntry? GetManifest(string name)
    {
        if (ReadLocalManifest(name) is { } cached) return cached;
        if (!TryComponent(name, out var entry)) return null;
        var local = ManifestFile(name);
        var remote = $"https://raw.githubusercontent.com/WTangent-Org/{entry.Repo}/main/agent-component.json";
        try
        {
            using var http = NewHttp(TimeSpan.FromSeconds(30));
            var json = http.GetStringAsync(remote).GetAwaiter().GetResult();
            var manifest = JsonSerializer.Deserialize<ManifestEntry>(json, JsonOpts);
            if (manifest is not null)
            {
                Directory.CreateDirectory(ComponentDir(name));
                File.WriteAllText(local, json);
                return manifest;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[agent] 拉取 {name} 组件入口文件失败：{e.Message}");
        }
        return null;
    }

    /// <summary>本地缓存的组件入口文件（不联网；无缓存/解析失败返回 null）</summary>
    private static ManifestEntry? ReadLocalManifest(string name)
    {
        try
        {
            var f = ManifestFile(name);
            return File.Exists(f) ? JsonSerializer.Deserialize<ManifestEntry>(File.ReadAllText(f), JsonOpts) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>已装组件加载顺序：depends 拓扑序（依赖先于依赖方），同层按索引优先级；检出环时退化为纯优先级序</summary>
    public static List<string> LoadOrder(List<string> installed)
    {
        var deps = installed.ToDictionary(
            n => n,
            n => (ReadLocalManifest(n)?.Depends?.Keys.Where(installed.Contains).ToList() ?? []),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(installed.Count);
        var remaining = installed.OrderBy(PriorityOf).ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(n => deps[n].All(result.Contains)).ToList();
            if (ready.Count == 0) { result.AddRange(remaining); break; }   // 环：拓扑让位优先级
            result.AddRange(ready);
            foreach (var r in ready) remaining.Remove(r);
        }
        return result;
    }
}
