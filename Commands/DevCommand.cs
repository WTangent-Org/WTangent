using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WTangent.Commands;

/// <summary>组件开发工具：wtangent dev install [组件...] [--configuration] [--root]。
/// 以 WTangentLocal=true（源码引用 Core + 生成器，绕过 nuget 索引传播）构建组件并直接部署到
/// %APPDATA%\agent\components——本地开发 inner loop，不等发版。
/// 工作区根自动探测：从 exe 位置和当前目录向上找同时含 WTangent\WTangent.csproj 与
/// WTangent.Components 的目录；找不到用 --root 显式指定。</summary>
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

    public DevCommand() : base("dev", "组件开发工具（install：本地源码构建并部署组件，绕过发版）")
    {
        Add(BuildInstall());
    }

    private static Command BuildInstall()
    {
        var names = new Argument<string[]>("components")
        {
            Description = "serve / tui / client / git（缺省 = 全部四个）",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var config = new Option<string>("--configuration", "-c") { Description = "构建配置", DefaultValueFactory = _ => "Debug" };
        var rootOpt = new Option<string?>("--root") { Description = "工作区根（缺省自动探测）" };
        var install = new Command("install", "本地源码构建组件（WTangentLocal=true）并部署到 components 目录") { names, config, rootOpt };
        install.SetAction(pr =>
        {
            var root = pr.GetValue(rootOpt) ?? FindWorkspaceRoot();
            if (root is null)
            {
                Console.Error.WriteLine("[dev] 找不到工作区根（含 WTangent 与 WTangent.Components 的目录），用 --root 指定");
                return 1;
            }
            var targets = pr.GetValue(names) is { Length: > 0 } list ? list : [.. ComponentMap.Keys];
            var rc = 0;
            foreach (var name in targets)
                rc |= InstallOne(name, root, pr.GetValue(config) ?? "Debug");
            return rc;
        });
        return install;
    }

    /// <summary>构建并部署单个组件：publish（WTangentLocal=true）→ 清空目标目录拷入（镜像 install 解压语义）
    /// → 缓存入口文件 + 写 .installed（Version=local-dev；upgrade 会把它升级回远程 release，本地调试别跑 upgrade）</summary>
    private static int InstallOne(string name, string root, string configuration)
    {
        if (!ComponentMap.TryGetValue(name, out var c))
        {
            Console.Error.WriteLine($"[dev] 未知组件 {name}（可选：{string.Join(", ", ComponentMap.Keys)}）");
            return 1;
        }
        var repoDir = Path.Combine(root, c.Dir);
        var outDir = Path.Combine(repoDir, "out", "dev-local");
        Console.WriteLine($"== {name} ← {c.Dir}（WTangentLocal=true, {configuration}）");
        var psi = new ProcessStartInfo("dotnet",
            $"publish \"{Path.Combine(repoDir, c.Proj)}\" -c {configuration} -r {Rid} --self-contained false -p:WTangentLocal=true -o \"{outDir}\"")
        { UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            Console.Error.WriteLine($"[dev] {name} 构建失败");
            return 1;
        }

        var dest = Path.Combine(ComponentManager.ComponentsDir, name);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        CopyDir(outDir, dest);
        File.Copy(Path.Combine(repoDir, "agent-component.json"), Path.Combine(dest, "agent-component.json"), overwrite: true);
        File.WriteAllText(Path.Combine(dest, ".installed"),
            JsonSerializer.Serialize(new { Repo = c.Dir, Version = "local-dev" }));

        // serve 的 web 资源：web/dist 已构建过才带（前端构建归 web/ 自己的 npm 流程）
        if (name == "serve")
        {
            var dist = Path.Combine(repoDir, "web", "dist");
            if (Directory.Exists(dist))
            {
                var webDest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "web");
                if (Directory.Exists(webDest)) Directory.Delete(webDest, true);
                CopyDir(dist, webDest);
                Console.WriteLine($"  web/dist → {webDest}");
            }
        }
        Console.WriteLine($"{name} → {dest}");
        return 0;
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
}
