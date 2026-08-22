using System.CommandLine;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace WTangent;

/// <summary>组件索引条目（components.json：只存 别名→仓库 映射，winget 式；GitHub 维护，空壳拉取缓存）</summary>
public sealed record IndexEntry(string Alias, string Repo);

/// <summary>组件入口文件（agent-component.json：组件仓库根自声明——资产名等元数据；
/// 类型已废弃：行为由 IEntry 能力决定，不再按 type 分流）</summary>
public sealed record ManifestEntry(string Name, string Asset);

/// <summary>组件管理：索引（apt 模式）+ 安装/卸载/升级 + dll 加载 + Entry 反射推导 + 依赖解析</summary>
public static class ComponentManager
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

    /// <summary>组件安装目录（%APPDATA%\agent\components）</summary>
    public static string ComponentsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "components");

    /// <summary>索引缓存文件（%APPDATA%\agent\components.json）</summary>
    private static string IndexFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "components.json");

    /// <summary>JSON 选项：索引字段 camelCase 与 record 参数 PascalCase 匹配</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>按需新实例（下载大文件等长超时场景；用后 Dispose，与 WtAgent.Core.Http 同构）</summary>
    private static HttpClient NewHttp(TimeSpan timeout) => new() { Timeout = timeout };

    /// <summary>当前索引（缓存 > 兜底）</summary>
    public static List<IndexEntry> Index => LoadIndex();

    /// <summary>刷新索引：拉 GitHub components.json 写缓存；quiet 时失败静默</summary>
    public static bool UpdateIndex(bool quiet = false)
    {
        try
        {
            var json = Http.GetStringAsync(IndexUrl).GetAwaiter().GetResult();
            var list = JsonSerializer.Deserialize<List<IndexEntry>>(json, JsonOpts);
            if (list is not { Count: > 0 }) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(IndexFile)!);
            File.WriteAllText(IndexFile, json);
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
    public static void RefreshIndexSilently() => UpdateIndex(quiet: true);

    /// <summary>组件是否已装（看入口 dll；manifest 缺失时回退 .installed 标记）</summary>
    public static bool IsInstalled(string name)
    {
        var dir = ComponentDir(name);
        if (!Directory.Exists(dir)) return false;
        var manifest = GetManifest(name);
        if (manifest is null) return File.Exists(Path.Combine(dir, ".installed"));
        return File.Exists(Path.Combine(dir, manifest.Asset + ".dll"));
    }

    /// <summary>组件表查找（别名）；未知名字打印提示并返回 false</summary>
    public static bool TryComponent(string alias, out IndexEntry entry)
    {
        var hit = Index.FirstOrDefault(e => e.Alias == alias);
        if (hit is not null)
        {
            entry = hit;
            return true;
        }
        entry = null!;
        Console.Error.WriteLine($"[wtangent] 未知组件 {alias}（wtangent update 刷新索引后重试）");
        return false;
    }

    /// <summary>加载组件 dll（默认上下文，使编译期引用绑定到下载的 dll）；失败提示并返回 false</summary>
    public static bool TryLoadComponent(string name, out Assembly asm)
    {
        var manifest = GetManifest(name);
        var dll = manifest is null
            ? Path.Combine(ComponentDir(name), name + ".dll")
            : Path.Combine(ComponentDir(name), manifest.Asset + ".dll");
        if (!File.Exists(dll))
        {
            asm = null!;
            return false;
        }
        try
        {
            asm = Assembly.LoadFrom(dll);
            return true;
        }
        catch (Exception e)
        {
            asm = null!;
            Console.Error.WriteLine($"[wtangent] 加载 {name} 组件失败：{e.Message}");
            return false;
        }
    }

    /// <summary>加载组件入口（找 IEntry 实现 → 实例化 → StartAsync 注入 App）；失败提示并返回 null</summary>
    public static WTangent.Core.IEntry? LoadEntry(string name, WTangent.Core.Application app)
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
            var entry = (WTangent.Core.IEntry)Activator.CreateInstance(entryType, app)!;   // 构造注入
            entry.StartAsync(app).GetAwaiter().GetResult();
            return entry;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[wtangent] 组件 {name} 入口启动失败：{e.Message}");
            return null;
        }
    }

    /// <summary>组件入口文件（本地缓存优先；缺失时从仓库拉取并缓存到 components\{name}\agent-component.json）</summary>
    public static ManifestEntry? GetManifest(string name)
    {
        var local = ManifestFile(name);
        try
        {
            if (File.Exists(local))
                return JsonSerializer.Deserialize<ManifestEntry>(File.ReadAllText(local), JsonOpts);
        }
        catch { }
        if (!TryComponent(name, out var entry)) return null;
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

    private static string ManifestFile(string name) => Path.Combine(ComponentDir(name), "agent-component.json");

    /// <summary>反射推导组件入口类型（约定：实现 IEntry 的 public 非抽象类）；依赖缺失时容错返回 null</summary>
    public static Type? FindEntryType(Assembly asm)
    {
        try
        {
            return asm.GetTypes().FirstOrDefault(t => t is { IsPublic: true, IsAbstract: false }
                && typeof(WTangent.Core.IEntry).IsAssignableFrom(t));
        }
        catch (ReflectionTypeLoadException)
        {
            return null;   // 组件依赖缺失（版本不符等）：命令注册走占位，不崩
        }
    }

    /// <summary>执行组件顶级行为（IEntry.Default）</summary>
    public static int RunDefault(string component, WTangent.Core.Application app, string[] passthrough)
    {
        if (!IsInstalled(component))
        {
            Console.WriteLine($"[wtangent] {component} 组件未安装。");
            Console.WriteLine($"[wtangent] 请先运行：wtangent install {component}");
            return 1;
        }
        var entry = LoadEntry(component, app);
        if (entry is null) return 1;
        if (entry.Default is null)
        {
            Console.Error.WriteLine($"[wtangent] 组件 {component} 无顶级行为（Default）");
            return 1;
        }
        try
        {
            return entry.Default(passthrough);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[wtangent] 执行 {component} 组件失败：{e.Message}");
            return 1;
        }
    }

    /// <summary>读取组件命令（IEntry.Commands：(命令, 父路径) 元组）</summary>
    public static (Command Command, string? ParentPath)[] ReadCommands(WTangent.Core.IEntry entry) => entry.Commands;

    /// <summary>安装组件：拉入口文件（agent-component.json）→ 下载 zip → 解压（web 类进 %APPDATA%\agent\web，
    /// 其余进 components\{name}，含 web/ 处理）；装后记录版本</summary>
    public static int Install(string component, bool force)
    {
        var entry = Index.FirstOrDefault(e => e.Alias == component);
        if (entry is null)
        {
            // 本地索引没有 → 刷新索引再查一次（第三方组件刚注册进 components.json 的场景）
            UpdateIndex(quiet: false);
            entry = Index.FirstOrDefault(e => e.Alias == component);
            if (entry is null)
            {
                TryComponent(component, out _);   // 打印"未知组件"提示
                return 1;
            }
        }
        var name = entry.Alias;
        var repo = entry.Repo;
        var manifest = GetManifest(name);
        if (manifest is null) return 1;
        var asset = manifest.Asset;
        var dir = ComponentDir(name);
        var marker = IsInstalled(name);
        if (!force && marker)
        {
            var v = ReadVersion(name);
            Console.WriteLine($"[wtangent] {name} 已安装：{dir}" + (v is null ? "" : $"（{v}）") + "；--force 重装，wtangent upgrade 更新");
            return 0;
        }
        var tag = LatestTag(repo, name);
        if (tag is null) return 1;
        var url = $"https://github.com/WTangent-Org/{repo}/releases/latest/download/{AssetName(asset)}";
        Console.WriteLine($"[agent] 下载 {name} {tag} ← {url}");
        var zip = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.zip");
        if (!Download(url, zip)) return 1;
        var tmp = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmp);
            ZipFile.ExtractToDirectory(zip, tmp);
            File.Delete(zip);
            // 代码组件 → components\{name}（ui/cmd/tool 统一；serve 包内 web/ 资源 → %APPDATA%\agent\web）
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
            var webSrc = Path.Combine(dir, "web");
            if (Directory.Exists(webSrc))
            {
                var webDest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "web");
                if (Directory.Exists(webDest)) Directory.Delete(webDest, true);
                Directory.Move(webSrc, webDest);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[agent] 解压失败：{e.Message}");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            return 1;
        }
        SaveVersion(name, tag);
        Console.WriteLine($"[agent] {name} {tag} 已安装：{dir}");
        return 0;
    }

    /// <summary>卸载组件：删组件目录 + 版本记录</summary>
    public static int Remove(string component)
    {
        if (!TryComponent(component, out _)) return 1;
        var dir = ComponentDir(component);
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"[agent] {component} 未安装");
            return 0;
        }
        try
        {
            Directory.Delete(dir, true);
            Console.WriteLine($"[agent] {component} 已卸载");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[agent] 卸载失败：{e.Message}");
            return 1;
        }
    }

    /// <summary>检查并更新已装组件：agent upgrade [serve|tui|gui|web]（缺省全部已装组件）</summary>
    public static int Upgrade(string? component)
    {
        var targets = component is null
            ? Index.Select(e => e.Alias).Where(IsInstalled).ToList()
            : TryComponent(component, out _) ? [component] : [];
        if (component is not null && !targets.Any())
        {
            Console.WriteLine($"[wtangent] {component} 未安装（wtangent install {component}）");
            return 0;
        }
        if (!targets.Any())
        {
            Console.WriteLine("[wtangent] 未安装任何组件（wtangent install serve|tui|gui|web）");
            return 0;
        }
        var rc = 0;
        foreach (var name in targets)
        {
            var (_, repo) = Index.First(x => x.Alias == name);
            var tag = LatestTag(repo, name);
            if (tag is null) { rc = 1; continue; }
            var local = ReadVersion(name);
            if (local == tag)
            {
                Console.WriteLine($"[agent] {name} 已是最新（{tag}）");
                continue;
            }
            Console.WriteLine($"[agent] {name} {local ?? "未知版本"} → {tag}，更新中…");
            if (Install(name, force: true) != 0) { rc = 1; continue; }
            Console.WriteLine($"[agent] {name} 已更新至 {tag}");
        }
        return rc;
    }

    /// <summary>组件依赖解析：直接遍历已装组件目录（不走索引——Resolving 关键路径上禁 JSON/IO 递归）</summary>
    public static Assembly? ResolveComponentDependency(AssemblyLoadContext ctx, AssemblyName name)
    {
        if (!Directory.Exists(ComponentsDir)) return null;
        foreach (var dir in Directory.GetDirectories(ComponentsDir))
        {
            var p = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
        }
        return null;
    }

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
        catch { }
        return [.. FallbackIndex];
    }

    private static string ComponentDir(string component) => Path.Combine(ComponentsDir, component);

    /// <summary>查询仓库最新 release tag（GitHub API，需 User-Agent）；失败提示并返回 null</summary>
    private static string? LatestTag(string repo, string component)
    {
        try
        {
            using var http = NewHttp(TimeSpan.FromSeconds(20));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("agent-upgrade");
            var json = http.GetStringAsync($"https://api.github.com/repos/WTangent-Org/{repo}/releases/latest").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[agent] 查询 {component} 最新版本失败：{e.Message}");
            return null;
        }
    }

    /// <summary>下载 URL 到目标文件；失败提示并返回 false</summary>
    private static bool Download(string url, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[agent] 下载失败：HTTP {(int)resp.StatusCode}（URL：{url}）");
                return false;
            }
            using var fs = File.Create(dest);
            resp.Content.ReadAsStream().CopyTo(fs);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[agent] 下载失败：{e.Message}");
            return false;
        }
        return true;
    }

    /// <summary>已装版本记录文件（%APPDATA%\agent\components\{component}\.version，内容为 release tag）</summary>
    private static string VersionFile(string component) => Path.Combine(ComponentsDir, component, ".version");

    private static string? ReadVersion(string component)
    {
        try
        {
            return File.Exists(VersionFile(component)) ? File.ReadAllText(VersionFile(component)).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveVersion(string component, string tag) =>
        File.WriteAllText(VersionFile(component), tag);

    /// <summary>组件 zip 资产名（framework-dependent，按平台 native 库分 zip）</summary>
    private static string AssetName(string baseName)
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows()) return $"{baseName}-win-{(arm ? "arm64" : "x64")}.zip";
        if (OperatingSystem.IsMacOS()) return $"{baseName}-osx-{(arm ? "arm64" : "x64")}.zip";
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => $"{baseName}-linux-aarch64.zip",
            Architecture.Arm => $"{baseName}-linux-arm.zip",
            _ => $"{baseName}-linux-x86_64.zip",
        };
    }
}
