using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WTangent;

// 本文件 = ComponentManager 安装/卸载/升级部分（职责地图见 ComponentManager.cs）
public static partial class ComponentManager
{
    /// <summary>安装组件：拉入口文件（agent-component.json）→ 下载 zip → 解压（web 类进 %APPDATA%\agent\web，
    /// 其余进 components\{name}，含 web/ 处理）；装后写安装元数据（.installed：来源仓库 + 版本）。
    /// 组件间依赖（manifest.depends）先解析：未装自动拉装、版本不足拒装、循环依赖报错</summary>
    public static Task<int> InstallAsync(string component, bool force) => InstallCoreAsync(component, force, []);

    /// <summary>确保组件已装（dev restore 拉依赖用）：已装原样成功，未装走完整安装链</summary>
    internal static Task<int> EnsureInstalledAsync(string component) =>
        IsInstalled(component) ? Task.FromResult(0) : InstallCoreAsync(component, force: false, []);

    private static async Task<int> InstallCoreAsync(string component, bool force, HashSet<string> chain)
    {
        var entry = Index.FirstOrDefault(e => e.Alias == component);
        if (entry is null)
        {
            // 本地索引没有 → 刷新索引再查一次（第三方组件刚注册进 components.json 的场景）
            await UpdateIndexAsync(quiet: false);
            entry = Index.FirstOrDefault(e => e.Alias == component);
            if (entry is null)
            {
                TryComponent(component, out _);   // 打印"未知组件"提示
                return 1;
            }
        }
        var name = entry.Alias;
        var repo = entry.Repo;
        var manifest = await GetManifestAsync(name);
        if (manifest is null) return 1;
        // Core 版本门禁：组件编译引用的 Core 高于空壳内置 Core 时拒装
        // （单 ALC 静默绑旧 Core，调用新成员会运行时炸）
        if (manifest.MinCore is { } minCore && Version.TryParse(minCore, out var need) && need > CoreVersion)
        {
            Console.Error.WriteLine($"[wtangent] {name} 需要 Core ≥ {minCore}（当前空壳内置 {CoreVersion}）");
            Console.Error.WriteLine("[wtangent] 请重新运行安装脚本升级空壳（install.ps1 / install.sh）");
            return 1;
        }
        // 组件间依赖解析：未装自动拉装、版本不足拒装、循环依赖报错
        if (manifest.Depends is { Count: > 0 } && !await ResolveDependsAsync(name, manifest.Depends, chain))
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
        var tag = await LatestTagAsync(repo, name);
        if (tag is null) return 1;
        var url = $"https://github.com/WTangent-Org/{repo}/releases/latest/download/{AssetName(asset)}";
        Console.WriteLine($"[agent] 下载 {name} {tag} ← {url}");
        var zip = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.zip");
        if (!await DownloadAsync(url, zip)) return 1;
        var tmp = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmp);
            ZipFile.ExtractToDirectory(zip, tmp);
            File.Delete(zip);
            // 代码组件 → components\{name}（ui/cmd/tool 统一；serve 包内 web/ 资源 → %APPDATA%\agent\web）
            if (Directory.Exists(dir)) DeleteDirRetry(dir);
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
    private static async Task<bool> ResolveDependsAsync(string name, Dictionary<string, string> depends, HashSet<string> chain)
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
                    if (await InstallCoreAsync(dep, force: false, chain) != 0)
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
            DeleteDirRetry(dir);
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
    public static async Task<int> UpgradeAsync(string? component)
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
            var tag = await LatestTagAsync(repo, name);
            if (tag is null) { rc = 1; continue; }
            var local = meta?.Version;
            if (local == tag)
            {
                Console.WriteLine($"[agent] {name} 已是最新（{tag}）");
                continue;
            }
            Console.WriteLine($"[agent] {name} {local ?? "未知版本"} → {tag}，更新中…");
            if (await InstallAsync(name, force: true) != 0) { rc = 1; continue; }
            Console.WriteLine($"[agent] {name} 已更新至 {tag}");
        }
        return rc;
    }

    /// <summary>查询仓库最新 release tag（GitHub API，需 User-Agent）；失败提示并返回 null</summary>
    private static async Task<string?> LatestTagAsync(string repo, string component)
    {
        try
        {
            using var http = NewHttp(TimeSpan.FromSeconds(20));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("agent-upgrade");
            var json = await http.GetStringAsync($"https://api.github.com/repos/WTangent-Org/{repo}/releases/latest");
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
    private static async Task<bool> DownloadAsync(string url, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[agent] 下载失败：HTTP {(int)resp.StatusCode}（URL：{url}）");
                return false;
            }
            using var fs = File.Create(dest);
            await resp.Content.ReadAsStream().CopyToAsync(fs);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[agent] 下载失败：{e.Message}");
            return false;
        }
        return true;
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
