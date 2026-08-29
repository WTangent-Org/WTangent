using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WTangent.Commands;

/// <summary>组件开发工具（第三方开发者 clone 单仓即可起步，不等发版、不依赖工作区）：
/// dev restore = 按 agent-component.json（本地 json 自声明）拉齐开发依赖——depends 组件走安装链从
///   GitHub release 补装，Core/生成器 dll 直接拉 Components 仓的 GitHub release 资产（与组件 zip
///   同一条下载线），缓存到 %APPDATA%\agent\dev\refs 并生成 wtangent.dev.props；
/// dev build   = 用 restore 的 props 编译（-p:WTangentDev=true + CustomBeforeMicrosoftCommonProps，不改 csproj）；
/// dev install = 本地构建并部署组件到 components 目录（官方组件走工作区 WTangentLocal 源码引用；
///   任意仓库用 --proj 走 restore 的引用缓存）。
/// 分发渠道只有 GitHub release（Core 也是组件，与组件 zip 同一条线，不经任何包平台）。System.CommandLine 由
/// 组件 csproj 自己包引用提供，restore 不重复注入（避免同程序集双 Reference）。</summary>
public sealed class DevCommand : Command
{
    /// <summary>官方组件：别名 → (仓目录名, csproj 文件名)（仓 = 工作区根的平级目录）</summary>
    private static readonly Dictionary<string, (string Dir, string Proj)> ComponentMap = new()
    {
        ["serve"] = ("WTangent.Server", "WTangent.Server.csproj"),
        ["tui"] = ("WTangent.Tui", "WTangent.Tui.csproj"),
        ["client"] = ("WTangent.Client", "WTangent.Client.csproj"),
        ["git"] = ("WTangent.GitCmd", "WTangent.GitCmd.csproj"),
    };

    private const string CoreRepo = "WTangent.Components";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public DevCommand() : base("dev", "组件开发工具（restore 拉依赖 / build 编译 / install 构建部署）")
    {
        Add(BuildInstall());
        Add(BuildRestore());
        Add(BuildBuild());
    }

    // ---------- dev install ----------

    private static Command BuildInstall()
    {
        var names = new Argument<string[]>("components")
        {
            Description = "serve / tui / client / git（缺省 = 全部四个；--proj 时忽略）",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var config = new Option<string>("--configuration", "-c") { Description = "构建配置", DefaultValueFactory = _ => "Debug" };
        var rootOpt = new Option<string?>("--root") { Description = "工作区根（官方组件缺省自动探测；第三方仓传仓根）" };
        var proj = new Option<string?>("--proj") { Description = "任意组件 csproj 路径（第三方仓；引用走 dev restore 缓存）" };
        var install = new Command("install", "本地构建组件并部署到 components 目录（官方组件 WTangentLocal；--proj 任意仓）")
            { names, config, rootOpt, proj };
        install.SetAction(async pr =>
        {
            var projFile = pr.GetValue(proj);
            if (projFile is not null)
            {
                if (!File.Exists(projFile))
                {
                    Console.Error.WriteLine($"[dev] 找不到 {projFile}");
                    return 1;
                }
                return await DeployAsync(projFile, pr.GetValue(config) ?? "Debug", useWorkspaceRefs: false);
            }
            var root = pr.GetValue(rootOpt) ?? FindWorkspaceRoot();
            if (root is null)
            {
                Console.Error.WriteLine("[dev] 找不到工作区根（含 WTangent 与 WTangent.Components 的目录），用 --root 指定或用 --proj 指向组件 csproj");
                return 1;
            }
            var targets = pr.GetValue(names) is { Length: > 0 } list ? list : [.. ComponentMap.Keys];
            var rc = 0;
            foreach (var name in targets)
            {
                if (!ComponentMap.TryGetValue(name, out var c))
                {
                    Console.Error.WriteLine($"[dev] 未知组件 {name}（可选：{string.Join(", ", ComponentMap.Keys)}；第三方仓用 --proj）");
                    rc |= 1;
                    continue;
                }
                rc |= await DeployAsync(Path.Combine(root, c.Dir, c.Proj), pr.GetValue(config) ?? "Debug", useWorkspaceRefs: true);
            }
            return rc;
        });
        return install;
    }

    /// <summary>构建并部署单个组件：publish → 清空目标目录拷入（镜像 install 解压语义）
    /// → 缓存入口文件 + 写 .installed（Version=local-dev；upgrade 会把它升级回远程 release，本地调试别跑 upgrade）。
    /// useWorkspaceRefs：true = 工作区源码引用（WTangentLocal，官方组件）；false = dev restore 的引用缓存（--proj 第三方仓）</summary>
    private static async Task<int> DeployAsync(string projFile, string configuration, bool useWorkspaceRefs)
    {
        projFile = Path.GetFullPath(projFile);
        var repoDir = Path.GetDirectoryName(projFile)!;
        var manifestFile = Path.Combine(repoDir, "agent-component.json");
        if (!File.Exists(manifestFile))
        {
            Console.Error.WriteLine($"[dev] {repoDir} 缺 agent-component.json（组件自声明），无法部署");
            return 1;
        }
        var manifest = JsonSerializer.Deserialize<ManifestEntry>(File.ReadAllText(manifestFile), JsonOpts);
        if (manifest is null)
        {
            Console.Error.WriteLine($"[dev] agent-component.json 解析失败：{manifestFile}");
            return 1;
        }
        var name = manifest.Name;
        var outDir = Path.Combine(repoDir, "out", "dev-local");
        Console.WriteLine($"== {name} ← {projFile}（{configuration}）");
        var extraProps = useWorkspaceRefs
            ? "-p:WTangentLocal=true"
            : await BuildRefPropsArgsAsync(manifest);
        if (extraProps is null) return 1;
        var rc = await RunDotnetAsync(
            $"publish \"{projFile}\" -c {configuration} -r {Rid} --self-contained false {extraProps} -o \"{outDir}\"");
        if (rc != 0)
        {
            Console.Error.WriteLine($"[dev] {name} 构建失败");
            return 1;
        }

        var dest = Path.Combine(ComponentManager.ComponentsDir, name);
        if (Directory.Exists(dest)) ComponentManager.DeleteDirRetry(dest);
        CopyDir(outDir, dest);
        // Core/System.CommandLine 由空壳运行时统一提供（单 ALC 简单名绑定）。组件目录若带了旧版本，
        // Resolving 兜底扫描会把它捡回来 → 同名双版本 → manifest mismatch 炸掉组件加载（与
        // ExcludeAssets=runtime 同理），发布产物带进来的一律清除
        foreach (var victim in new[] { "WTangent.Core.dll", "System.CommandLine.dll" })
        {
            var stale = Path.Combine(dest, victim);
            if (File.Exists(stale)) File.Delete(stale);
        }
        File.Copy(manifestFile, Path.Combine(dest, "agent-component.json"), overwrite: true);
        File.WriteAllText(Path.Combine(dest, ".installed"),
            JsonSerializer.Serialize(new InstallMetaRecord(null, "local-dev")));

        // serve 的 web 资源：web/dist 已构建过才带（前端构建归 web/ 自己的 npm 流程）
        if (name == "serve")
        {
            var dist = Path.Combine(repoDir, "web", "dist");
            if (Directory.Exists(dist))
            {
                var webDest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "web");
                if (Directory.Exists(webDest)) ComponentManager.DeleteDirRetry(webDest);
                CopyDir(dist, webDest);
                Console.WriteLine($"  web/dist → {webDest}");
            }
        }
        Console.WriteLine($"{name} → {dest}");
        return 0;
    }

    /// <summary>dev build / dev install 的 restore 引用参数；props 未生成时提示先 restore 并返回 null</summary>
    private static async Task<string?> BuildRefPropsArgsAsync(ManifestEntry manifest)
    {
        var props = RefsPropsFile();
        if (!File.Exists(props))
        {
            Console.Error.WriteLine("[dev] 引用缓存不存在，先运行 wtangent dev restore");
            return null;
        }
        var rc = await RestoreDependsAsync(manifest);
        return rc == 0 ? $"-p:WTangentDev=true -p:CustomBeforeMicrosoftCommonProps=\"{props}\"" : null;
    }

    // ---------- dev restore ----------

    private static Command BuildRestore()
    {
        var rootOpt = new Option<string?>("--root") { Description = "组件仓库根（含 agent-component.json，缺省当前目录）" };
        var coreVer = new Option<string?>("--core-version")
        {
            Description = "Core release tag（如 v0.0.12；缺省取最新 release 资产）",
        };
        var force = new Option<bool>("--force") { Description = "忽略缓存重新下载" };
        var restore = new Command("restore",
            "按 agent-component.json 拉齐开发依赖：depends 组件补装 + Core/生成器 GitHub release 直拉 + 生成 wtangent.dev.props")
            { rootOpt, coreVer, force };
        restore.SetAction(async pr =>
        {
            var root = pr.GetValue(rootOpt) ?? Directory.GetCurrentDirectory();
            var rc = await RestoreAsync(root, pr.GetValue(coreVer), pr.GetValue(force));
            if (rc == 0)
                Console.WriteLine($"""
                    [dev] 完成。编译：wtangent dev build --root "{root}"
                    """);
            return rc;
        });
        return restore;
    }

    private static async Task<int> RestoreAsync(string root, string? coreVersionOpt, bool force)
    {
        var manifestFile = Path.Combine(root, "agent-component.json");
        if (!File.Exists(manifestFile))
        {
            Console.Error.WriteLine($"[dev] {root} 缺 agent-component.json（组件自声明 = dev 工具的输入 json）");
            return 1;
        }
        var manifest = JsonSerializer.Deserialize<ManifestEntry>(File.ReadAllText(manifestFile), JsonOpts);
        if (manifest is null)
        {
            Console.Error.WriteLine($"[dev] agent-component.json 解析失败：{manifestFile}");
            return 1;
        }
        Dictionary<string, string> deps = manifest.Depends ?? new Dictionary<string, string>();
        Console.WriteLine($"== dev restore：{manifest.Name}（depends: {(deps.Count == 0 ? "无" : string.Join(", ", deps.Keys))}）");

        // 1) depends 组件：走完整安装链（自动拉装/版本门禁/传递依赖），装进 components 目录供运行时加载与编译引用
        if (await RestoreDependsAsync(manifest) != 0) return 1;

        // 2) Core + 生成器：GitHub release 资产直拉（Core 也是组件，分发与组件 zip 同一条线）
        var tag = coreVersionOpt is null ? "latest" : coreVersionOpt.StartsWith('v') ? coreVersionOpt : "v" + coreVersionOpt;
        var base_ = tag == "latest"
            ? $"https://github.com/WTangent-Org/{CoreRepo}/releases/latest/download"
            : $"https://github.com/WTangent-Org/{CoreRepo}/releases/download/{tag}";
        var dir = Path.Combine(RefsRoot(), "wtangent.components", tag);
        var coreDll = Path.Combine(dir, "WTangent.Core.dll");
        var genDll = Path.Combine(dir, "WTangent.Components.dll");
        if (force || !File.Exists(coreDll) || !File.Exists(genDll))
        {
            Directory.CreateDirectory(dir);
            if (!await DownloadFileAsync($"{base_}/WTangent.Core.dll", coreDll)) return 1;
            if (!await DownloadFileAsync($"{base_}/WTangent.Components.dll", genDll)) return 1;
        }
        if (!File.Exists(coreDll) || !File.Exists(genDll))
        {
            Console.Error.WriteLine($"[dev] Core/生成器资产异常（缺文件）：{coreDll} / {genDll}");
            return 1;
        }
        // 内容健全性检查：tag 新 ≠ 内容对（防 release 资产挂错/缺内容的老快照流进引用缓存）
        if (!await ContainsBytesAsync(coreDll, new byte[] { 0x49, 0x45, 0x6E, 0x74, 0x72, 0x79 }))   // "IEntry"
        {
            Console.Error.WriteLine($"[dev] {tag} 的 Core.dll 缺 IEntry（资产异常），换 --core-version 指定其他 tag");
            return 1;
        }

        // 3) 生成 props（每次 restore 重新生成；deps 引用指向 components 目录里已装组件的入口 dll）
        var propsLines = new List<string>
        {
            "<Project>",
            "  <!-- 由 wtangent dev restore 生成（勿手改）；接入: wtangent dev build 或 dev install(proj 模式)，",
            "    或 dotnet build 加属性 WTangentDev=true 与 CustomBeforeMicrosoftCommonProps=本文件 -->",
            "  <ItemGroup>",
            $"    <Reference Include=\"WTangent.Core\"><HintPath>{coreDll}</HintPath></Reference>",
        };
        foreach (var dep in deps.Keys)
        {
            var depManifest = await ComponentManager.GetManifestAsync(dep);
            var dll = Path.Combine(ComponentManager.ComponentsDir, dep, (depManifest?.Asset ?? dep) + ".dll");
            if (!File.Exists(dll))
            {
                Console.Error.WriteLine($"[dev] 依赖 {dep} 入口 dll 缺失（{dll}），编译引用不完整");
                return 1;
            }
            propsLines.Add($"    <Reference Include=\"{dep}\"><HintPath>{dll}</HintPath></Reference>");
        }
        propsLines.AddRange(
        [
            "  </ItemGroup>",
            "  <ItemGroup>",
            $"    <Analyzer Include=\"{genDll}\" />",
            "    <CompilerVisibleProperty Include=\"ProjectDir\" />",
            "    <CompilerVisibleProperty Include=\"AssemblyName\" />",
            "    <CompilerVisibleProperty Include=\"ComponentDepends\" />",
            "  </ItemGroup>",
            "</Project>",
        ]);
        var propsFile = RefsPropsFile();
        Directory.CreateDirectory(Path.GetDirectoryName(propsFile)!);
        await File.WriteAllTextAsync(propsFile, string.Join(Environment.NewLine, propsLines));
        Console.WriteLine($"[dev] Core {tag} + 生成器已缓存；props → {propsFile}");
        return 0;
    }

    /// <summary>补装 depends 组件（已装跳过；失败返回非 0）——运行时加载与编译引用共用这套安装结果</summary>
    private static async Task<int> RestoreDependsAsync(ManifestEntry manifest)
    {
        foreach (var dep in manifest.Depends?.Keys ?? (IEnumerable<string>)[])
        {
            var rc = await ComponentManager.EnsureInstalledAsync(dep);
            if (rc != 0)
            {
                Console.Error.WriteLine($"[dev] 依赖 {dep} 安装失败，{manifest.Name} 中止");
                return 1;
            }
        }
        return 0;
    }

    /// <summary>dll 内容包含探测字节（健全性检查用；IL 元数据里类型名以明文存在）</summary>
    private static async Task<bool> ContainsBytesAsync(string file, byte[] probe)
    {
        var data = await File.ReadAllBytesAsync(file);
        return data.AsSpan().IndexOf(probe) >= 0;
    }

    /// <summary>GitHub release 资产下载（dev restore 的 Core/生成器直拉通道）；失败提示并返回 false</summary>
    private static async Task<bool> DownloadFileAsync(string url, string dest)
    {
        try
        {
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[dev] 下载失败：HTTP {(int)resp.StatusCode}（{url}）");
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    Console.Error.WriteLine("[dev] release 上还没有该资产——Components 发版（挂 Core/生成器 dll）后自动可用，或 --core-version 指定已挂资产的 tag");
                return false;
            }
            using var fs = File.Create(dest);
            await resp.Content.ReadAsStream().CopyToAsync(fs);
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[dev] 下载失败：{e.Message}");
            return false;
        }
    }

    private static string RefsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "dev", "refs");

    private static string RefsPropsFile() => Path.Combine(RefsRoot(), "wtangent.dev.props");

    // ---------- dev build ----------

    private static Command BuildBuild()
    {
        var rootOpt = new Option<string?>("--root") { Description = "组件仓库根（缺省当前目录）" };
        var proj = new Option<string?>("--proj") { Description = "组件 csproj 路径（缺省取 root 下唯一 csproj）" };
        var config = new Option<string>("--configuration", "-c") { Description = "构建配置", DefaultValueFactory = _ => "Debug" };
        var build = new Command("build", "用 restore 拉齐的引用编译组件（不部署；等价 dev install 的构建步骤）") { rootOpt, proj, config };
        build.SetAction(async pr =>
        {
            var projFile = pr.GetValue(proj) ?? FindSingleCsproj(pr.GetValue(rootOpt) ?? Directory.GetCurrentDirectory());
            if (projFile is null) return 1;
            var manifestFile = Path.Combine(Path.GetDirectoryName(projFile)!, "agent-component.json");
            if (!File.Exists(manifestFile))
            {
                Console.Error.WriteLine($"[dev] {manifestFile} 不存在，先写组件自声明（restore 的输入）");
                return 1;
            }
            var manifest = JsonSerializer.Deserialize<ManifestEntry>(File.ReadAllText(manifestFile), JsonOpts);
            if (manifest is null) return 1;
            var extraProps = await BuildRefPropsArgsAsync(manifest);
            if (extraProps is null) return 1;
            Console.WriteLine($"== dev build：{manifest.Name} ← {projFile}");
            return await RunDotnetAsync($"build \"{projFile}\" -c {pr.GetValue(config) ?? "Debug"} {extraProps}");
        });
        return build;
    }

    /// <summary>目录下唯一 csproj；0 个或多个时报错返回 null（多个用 --proj 指定）</summary>
    private static string? FindSingleCsproj(string root)
    {
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[dev] 目录不存在：{root}");
            return null;
        }
        var projs = Directory.GetFiles(root, "*.csproj");
        switch (projs.Length)
        {
            case 1: return projs[0];
            case 0:
                Console.Error.WriteLine($"[dev] {root} 下没有 csproj");
                return null;
            default:
                Console.Error.WriteLine($"[dev] {root} 下有 {projs.Length} 个 csproj，用 --proj 指定");
                return null;
        }
    }

    /// <summary>跑 dotnet 子进程（输出直通控制台；构建/发布本来就是子进程，天然规避本进程组件自锁）</summary>
    private static async Task<int> RunDotnetAsync(string args)
    {
        var psi = new ProcessStartInfo("dotnet", args) { UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    /// <summary>当前机器 RID（dev-local 只服务本机）</summary>
    private static string Rid =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + "-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    /// <summary>工作区根探测：从 exe 位置和当前目录分别向上找（最多 6 层），
    /// 判据 = 同时含 WTangent\WTangent.csproj 与 WTangent.Components\src</summary>
    private static string? FindWorkspaceRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "WTangent", "WTangent.csproj"))
                    && Directory.Exists(Path.Combine(dir.FullName, "WTangent.Components", "src")))
                    return dir.FullName;
            }
        }
        return null;
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// <summary>.installed 反序列化形状（与 ComponentManager 内部 InstallMeta 同构；此处匿名序列化用）</summary>
    private sealed record InstallMetaRecord(string? Repo, string? Version);
}
