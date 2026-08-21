using System.CommandLine;
using System.Reflection;
using System.Runtime.Loader;
using WTangent.Commands;
using WTangent.Core;
using WTangent.Host;

namespace WTangent;

/// <summary>wtangent 启动器（空壳）：self-contained 带 .NET 运行时；组件为 framework-dependent dll，
/// 下载解压后由本进程加载（共享运行时）。组件元数据 = GitHub components.json 索引（apt 模式），
/// 组件暴露 Command 列表注册到空壳命令树；入口约定：public static class Entry（Commands + Default + App）。
/// 宿主实现 Application（Logger/Events/Config/Store/Remote/GuiHost/Services）并注入每个组件（Entry.App）。</summary>
public static class Program
{
    /// <summary>组件运行时上下文（宿主实现；注入已加载组件的 Entry.App）</summary>
    public static Application App { get; private set; } = null!;

    public static int Main(string[] args)
    {
        // 组件依赖解析：从已装组件目录补依赖（组件 dll 的 NuGet 依赖如 Terminal.Gui 等）
        AssemblyLoadContext.Default.Resolving += ComponentManager.ResolveComponentDependency;

        // 静默刷新组件索引（快速超时，离线静默走缓存；更新提示由 wtangent upgrade 承担）
        ComponentManager.RefreshIndexSilently();

        App = BuildApp();
        App.Events.Publish("app.startup", null);

        var root = new RootCommand("wtangent - 启动器（install/remove/upgrade/update；组件命令已注册）")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        root.Add(new InstallCommand());
        root.Add(new RemoveCommand());
        root.Add(new UpgradeCommand());
        root.Add(new UpdateCommand());
        RegisterComponentCommands(root);

        // 顶级（无子命令）→ 已装且带 Default 的组件（headless 顺序）
        root.SetAction(_ => RunTopLevel());
        var rc = root.Parse(args).Invoke();
        App.Events.Publish("app.shutdown", null);
        return rc;
    }

    /// <summary>组装宿主 Application：所有契约由主仓实现，组件经 Entry.App 使用</summary>
    private static Application BuildApp()
    {
        var logger = new HostLogger();
        var events = new HostEventBus(logger);
        var store = new HostStore();
        return new Application
        {
            Logger = logger,
            Events = events,
            Config = new HostConfig(events),
            Store = store,
            Remote = new RemoteClient(store),
            GuiHost = new GuiHost(),
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
            Services = new ServiceRegistry(),
        };
    }

    /// <summary>注册组件命令：已装 → 加载 dll 注册真实命令（--help 直接显示）；未装 → 简单占位（提示安装）。
    /// 类型收敛：ui/cmd 组件注册命令；tool（LLM 工具，serve 加载）不注册。
    /// 重写规则：仅官方组件（serve/tui/gui）可覆盖重名命令（后注册覆盖先注册）；其余组件重名时跳过并提示。</summary>
    private static void RegisterComponentCommands(RootCommand root)
    {
        foreach (var e in ComponentManager.Index)
        {
            var name = e.Alias;
            if (ComponentManager.IsInstalled(name))
            {
                // tool（LLM 工具，serve 加载）类组件不注册命令
                if (ComponentManager.GetManifest(name) is { Type: "tool" }) continue;
                if (ComponentManager.TryLoadComponent(name, out var asm))
                {
                    ComponentManager.InjectApp(asm, App);
                    var canOverride = name is "serve" or "tui" or "gui";
                    foreach (var c in ComponentManager.ReadCommands(asm)) AddComponentCommand(root, c, canOverride);
                    continue;
                }
            }
            // 未装 / 加载失败：占位（重名时跳过，避免命令树冲突）
            if (root.Subcommands.Any(c => c.Name == name)) continue;
            root.Add(NotInstalledCommand(name));
        }
    }

    /// <summary>注册组件命令：重名时仅白名单组件（serve/tui/gui）可覆盖（先移除旧命令再注册）；
    /// 其余组件重名则跳过并提示。System.CommandLine 2.0 无公开移除 API，反射内部 _subcommands 列表。</summary>
    private static void AddComponentCommand(RootCommand root, Command c, bool canOverride)
    {
        var old = root.Subcommands.FirstOrDefault(x => x.Name == c.Name);
        if (old is null) { root.Add(c); return; }
        if (!canOverride)
        {
            Console.Error.WriteLine($"[agent] 跳过命令 {c.Name}：与现有命令重名（仅 serve/tui/gui 组件可重写）");
            return;
        }
        var field = typeof(Command).GetField("_subcommands", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.GetValue(root)?.GetType().GetMethod("Remove")?.Invoke(field.GetValue(root), [old]);
        root.Add(c);
    }

    /// <summary>未安装占位命令：执行时提示安装</summary>
    private static Command NotInstalledCommand(string component)
    {
        var c = new Command(component, $"未安装（wtangent install {component}）");
        c.SetAction(_ =>
        {
            Console.WriteLine($"[wtangent] {component} 组件未安装。");
            Console.WriteLine($"[wtangent] 请先运行：wtangent install {component}");
            return 1;
        });
        return c;
    }

    /// <summary>顶级 wtangent：索引顺序 = 桌面优先级（headless 反转），取第一个已装且有 Default 的组件执行</summary>
    private static int RunTopLevel()
    {
        var names = ComponentManager.Index.Select(e => e.Alias).ToArray();
        var ordered = HeadlessComponent() == "gui" ? names : names.Reverse();
        foreach (var name in ordered)
        {
            if (!ComponentManager.IsInstalled(name) || !ComponentManager.TryLoadComponent(name, out var asm)) continue;
            if (!ComponentManager.HasDefault(asm)) continue;
            return ComponentManager.RunDefault(name, []);
        }
        Console.WriteLine("[wtangent] 未安装客户端组件。");
        Console.WriteLine($"[wtangent] 请先运行：wtangent install {ComponentManager.Index.FirstOrDefault(e => e.Alias is "tui" or "gui")?.Alias ?? "tui"}");
        return 1;
    }

    /// <summary>headless 检测：有桌面（Windows 交互会话 / Linux 有 DISPLAY 且非 SSH）→ gui 优先；否则 tui 优先</summary>
    private static string HeadlessComponent()
    {
        if (OperatingSystem.IsWindows())
            return Environment.UserInteractive ? "gui" : "tui";
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var ssh = Environment.GetEnvironmentVariable("SSH_CONNECTION");
        return display is { Length: > 0 } && ssh is null ? "gui" : "tui";
    }
}
