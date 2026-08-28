using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using WTangent.Core;

namespace WTangent;

/// <summary>组件索引条目（components.json：只存 别名→仓库 映射，winget 式；GitHub 维护，空壳拉取缓存）</summary>
public sealed record IndexEntry(string Alias, string Repo);

/// <summary>组件入口文件（agent-component.json：组件仓库根自声明——资产名等元数据；
/// 类型已废弃：行为由 IEntry 能力决定，不再按 type 分流；
/// MinCore = 组件编译时引用的 Core 版本（生成器构建时自动写入），install/upgrade 时校验空壳内置 Core ≥ 它；
/// Depends = 组件间编译期互引的运行时声明（别名→最低版本；csproj ComponentDepends 属性 → 生成器写入）；
/// Core 是每个组件的隐式必备依赖（即 minCore），不在 Depends 里声明）</summary>
public sealed record ManifestEntry(string Name, string Asset, string? MinCore = null,
    Dictionary<string, string>? Depends = null);

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

    /// <summary>空壳内置 Core 版本（= 组件 manifest MinCore 的比较基准；
    /// 单 ALC 统一，组件运行时用到的 Core 就是这份，与空壳版本同升同降）</summary>
    public static readonly Version CoreVersion =
        typeof(ILogger).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>已加载组件的依赖解析器（各组件 deps.json 驱动；TryLoadComponent 时注册，键 = 组件别名）</summary>
    private static readonly ConcurrentDictionary<string, AssemblyDependencyResolver> Resolvers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>远程组件清单的本地缓存（内存 > 磁盘 > 兜底；UpdateIndex 成功时同步刷新内存）。
    /// 只回答「registry 里有哪些组件可装 / 别名→仓库 / 展示优先级」，不代表本地装了什么——已装集合见 <see cref="InstalledComponents"/></summary>
    public static List<IndexEntry> Index
    {
        get => field ??= LoadIndex();
        private set;
    }

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
    public static void RefreshIndexSilently() => UpdateIndex(quiet: true);

    /// <summary>组件是否已装（纯本地：看入口 dll；manifest 无本地缓存时回退 .installed 标记；不联网不查索引）</summary>
    public static bool IsInstalled(string name)
    {
        var dir = ComponentDir(name);
        if (!Directory.Exists(dir)) return false;
        var manifest = ReadLocalManifest(name);
        return File.Exists(manifest is null ? Path.Combine(dir, ".installed") : Path.Combine(dir, manifest.Asset + ".dll"));
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

    /// <summary>启动组件入口</summary>
    public static Task StartEntry(IEntry entry) =>
        entry.SupportAsyncStart ? Task.Run(entry.StartAsync) : entry.StartAsync();

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

    private static string ManifestFile(string name) => Path.Combine(ComponentDir(name), "agent-component.json");

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

    /// <summary>安装组件：拉入口文件（agent-component.json）→ 下载 zip → 解压（web 类进 %APPDATA%\agent\web，
    /// 其余进 components\{name}，含 web/ 处理）；装后写安装元数据（.installed：来源仓库 + 版本）。
    /// 组件间依赖（manifest.depends）先解析：未装自动拉装、版本不足拒装、循环依赖报错</summary>
    public static int Install(string component, bool force) => InstallCore(component, force, []);

    private static int InstallCore(string component, bool force, HashSet<string> chain)
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
        // Core 版本门禁：组件编译引用的 Core 高于空壳内置 Core 时拒装
        // （单 ALC 静默绑旧 Core，调用新成员会运行时炸）
        if (manifest.MinCore is { } minCore && Version.TryParse(minCore, out var need) && need > CoreVersion)
        {
            Console.Error.WriteLine($"[wtangent] {name} 需要 Core ≥ {minCore}（当前空壳内置 {CoreVersion}）");
            Console.Error.WriteLine("[wtangent] 请重新运行安装脚本升级空壳（install.ps1 / install.sh）");
            return 1;
        }
        // 组件间依赖解析：未装自动拉装（递归）、已装校验最低版本、循环依赖报错
        if (manifest.Depends is { Count: > 0 } && !ResolveDepends(name, manifest.Depends, chain))
            return 1;
        var asset = manifest.Asset;
        var dir = ComponentDir(name);
        var marker = IsInstalled(name);
        if (!force && marker)
        {
            var v = ReadMeta(name)?.Version;
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
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return 1;
        }
        SaveMeta(name, repo, tag);
        Console.WriteLine($"[agent] {name} {tag} 已安装：{dir}");
        return 0;
    }

    /// <summary>组件间依赖解析（install 期）：未装 → 递归自动拉装；已装 → 校验最低版本（tag 去 v 前缀比较，
    /// local-dev/未知版本跳过校验——本地开发安装不挡）；chain 检出循环依赖。失败打印原因并返回 false</summary>
    private static bool ResolveDepends(string name, Dictionary<string, string> depends, HashSet<string> chain)
    {
        if (!chain.Add(name))
        {
            Console.Error.WriteLine($"[wtangent] 循环依赖：{string.Join(" → ", chain)} → {name}");
            return false;
        }
        try
        {
            foreach (var (dep, minVer) in depends)
            {
                if (!IsInstalled(dep))
                {
                    Console.WriteLine($"[wtangent] {name} 依赖 {dep}（≥ {minVer}），自动安装…");
                    if (InstallCore(dep, force: false, chain) != 0)
                    {
                        Console.Error.WriteLine($"[wtangent] 依赖 {dep} 安装失败，{name} 中止");
                        return false;
                    }
                    continue;
                }
                var local = ReadMeta(dep)?.Version;
                if (local is null or "local-dev") continue;
                if (Version.TryParse(minVer, out var need)
                    && Version.TryParse(local.TrimStart('v'), out var have) && have < need)
                {
                    Console.Error.WriteLine($"[wtangent] {name} 需要 {dep} ≥ {minVer}（当前 {local}），先 wtangent upgrade {dep}");
                    return false;
                }
            }
            return true;
        }
        finally { chain.Remove(name); }
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

    /// <summary>卸载组件：删组件目录（含安装元数据）；纯本地操作，不查远程索引。
    /// 有其他已装组件 depends 它时拒删（列出依赖方）</summary>
    public static int Remove(string component)
    {
        var dir = ComponentDir(component);
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"[agent] {component} 未安装");
            return 0;
        }
        // 卸载保护：有已装组件声明依赖它时拒删
        var users = InstalledComponents()
            .Where(n => !n.Equals(component, StringComparison.OrdinalIgnoreCase)
                && ReadLocalManifest(n)?.Depends?.ContainsKey(component) is true)
            .ToList();
        if (users.Count > 0)
        {
            Console.Error.WriteLine($"[wtangent] {component} 被依赖中，拒删：{string.Join(", ", users)}（先卸载它们）");
            return 1;
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

    /// <summary>检查并更新已装组件：agent upgrade [serve|tui|gui|web]（缺省 = 本地全部已装组件，纯本地扫描）。
    /// 来源仓库优先读安装元数据（.installed），旧安装缺失时回退索引；都查不到则跳过</summary>
    public static int Upgrade(string? component)
    {
        var targets = component is null
            ? InstalledComponents()
            : IsInstalled(component) ? [component] : [];
        if (component is not null && targets.Count == 0)
        {
            Console.WriteLine($"[wtangent] {component} 未安装（wtangent install {component}）");
            return 0;
        }
        if (targets.Count == 0)
        {
            Console.WriteLine("[wtangent] 未安装任何组件（wtangent install serve|tui|gui|web）");
            return 0;
        }
        var rc = 0;
        foreach (var name in targets)
        {
            var meta = ReadMeta(name);
            var repo = meta?.Repo ?? Index.FirstOrDefault(x => x.Alias == name)?.Repo;
            if (repo is null)
            {
                Console.Error.WriteLine($"[agent] {name} 来源仓库未知（无安装元数据且索引里没有），跳过");
                rc = 1;
                continue;
            }
            var tag = LatestTag(repo, name);
            if (tag is null) { rc = 1; continue; }
            var local = meta?.Version;
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

    /// <summary>安装元数据（components\{component}\.installed，JSON：安装来源仓库 + 版本 tag；
    /// 升级/卸载凭它走本地，不依赖远程索引。旧安装只有 .version：读取时回退，Repo 留空走索引）</summary>
    private sealed record InstallMeta(string? Repo, string? Version);

    private static string MetaFile(string component) => Path.Combine(ComponentsDir, component, ".installed");

    private static InstallMeta? ReadMeta(string component)
    {
        try
        {
            var f = MetaFile(component);
            if (File.Exists(f))
                return JsonSerializer.Deserialize<InstallMeta>(File.ReadAllText(f), JsonOpts);
            var v = ReadVersion(component);   // 旧安装只有 .version
            return v is null ? null : new InstallMeta(null, v);
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

    private static void SaveMeta(string component, string repo, string tag) =>
        File.WriteAllText(MetaFile(component), JsonSerializer.Serialize(new InstallMeta(repo, tag)));

    /// <summary>旧版安装版本记录（components\{component}\.version，内容为 release tag；新安装已并入 .installed，仅为兼容保留读取）</summary>
    private static string VersionFile(string component) => Path.Combine(ComponentsDir, component, ".version");

    private static string? ReadVersion(string component)
    {
        try
        {
            return File.Exists(VersionFile(component)) ? File.ReadAllText(VersionFile(component)).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

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
