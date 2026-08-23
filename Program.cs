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
        // 管理命令不加载组件：组件 dll 一经加载即被本进程锁定，Windows 上 install --force/upgrade/remove 会删不动目录（自锁）
        if (args is not ["install" or "remove" or "upgrade" or "update", ..])
            RegisterComponentCommands(root);

        // 顶级（无子命令）→ 已装且带 Default 的组件（headless 顺序）
        root.SetAction(_ => RunTopLevel());
        var rc = root.Parse(args).Invoke();
        App.Events.Publish("app.shutdown", null);
        return rc;
    }

    /// <summary>组装宿主 Application：所有契约由主仓实现，组件经 Entry.App 使用；
    /// 同步注入 Core 全局门面 Log/Config（组件任意位置 Log.Info / Config.Get 直达，单 ALC 下静态唯一）</summary>
    private static Application BuildApp()
    {
        var logger = new HostLogger();
        Log.Init(logger);
        var events = new HostEventBus(logger);
        var store = new HostStore();
        var config = new HostConfig(events);
        Config.Init(config);
        return new Application
        {
            Events = events,
            Store = store,
            Remote = new RemoteClient(store),
            GuiHost = new GuiHost(),
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
            Services = new ServiceRegistry(),
        };
    }

    /// <summary>注册组件命令：已装（纯本地扫描，按索引优先级排序）→ 加载 dll → 找 IEntry → StartAsync(App) → 注册 Commands（--help 直接显示）；
    /// 索引里已知但未装/加载失败 → 简单占位（提示安装）。索引只是远程清单，不代表本地装了什么。
    /// 能力由组件自己声明：Commands 非空即注册；sub（只订阅事件）/tool（只给 Tools）自然无命令可注册。
    /// 重写规则：仅官方组件（serve/tui/gui）可覆盖重名命令（后注册覆盖先注册）；其余组件重名时跳过并提示。</summary>
    private static void RegisterComponentCommands(RootCommand root)
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ComponentManager.InstalledComponents().OrderBy(ComponentManager.PriorityOf))
        {
            var entry = ComponentManager.LoadEntry(name, App);
            if (entry is null) continue;   // 加载失败 → 落占位
            var canOverride = name is "serve" or "tui" or "gui";
            foreach (var (cmd, parentPath) in entry.Commands)
                AddComponentCommand(root, cmd, parentPath, canOverride);
            loaded.Add(name);
        }
        // 未装 / 加载失败：占位（重名时跳过，避免命令树冲突）
        foreach (var alias in ComponentManager.Index.Select(e => e.Alias))
        {
            if (loaded.Contains(alias)) continue;
            if (root.Subcommands.Any(c => c.Name == alias)) continue;
            root.Add(NotInstalledCommand(alias));
        }
    }

    /// <summary>注册组件命令：父路径非空 → 挂到该路径命令下（路径含根名 root，如 "root/remote"）；
    /// 顶级/挂接重名时仅白名单组件（serve/tui/gui）可覆盖（先移除旧命令再注册）；
    /// 其余组件重名则跳过并提示。System.CommandLine 2.0 无公开移除 API，反射内部 _subcommands 列表。</summary>
    private static void AddComponentCommand(RootCommand root, Command c, string? parentPath, bool canOverride)
    {
        if (parentPath is { Length: > 0 })
        {
            var parent = ResolvePath(root, parentPath);
            if (parent is null)
            {
                Console.Error.WriteLine($"[wtangent] 跳过命令 {c.Name}：父路径 {parentPath} 不存在");
                return;
            }
            var oldChild = parent.Subcommands.FirstOrDefault(x => x.Name == c.Name);
            if (oldChild is null) { parent.Add(c); return; }
            if (!canOverride)
            {
                Console.Error.WriteLine($"[wtangent] 跳过命令 {c.Name}：与 {parentPath} 下现有命令重名（仅 serve/tui/gui 组件可重写）");
                return;
            }
            RemoveCommand(parent, oldChild);
            parent.Add(c);
            return;
        }
        var old = root.Subcommands.FirstOrDefault(x => x.Name == c.Name);
        if (old is null) { root.Add(c); return; }
        if (!canOverride)
        {
            Console.Error.WriteLine($"[agent] 跳过命令 {c.Name}：与现有命令重名（仅 serve/tui/gui 组件可重写）");
            return;
        }
        RemoveCommand(root, old);
        root.Add(c);
    }

    /// <summary>按路径解析命令（路径含根名 root，如 "root/remote/user"；root 段跳过）</summary>
    private static Command? ResolvePath(RootCommand root, string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return null;
        var i = segs[0] == "root" ? 1 : 0;
        Command cur = root;
        for (; i < segs.Length; i++)
        {
            var next = cur.Subcommands.FirstOrDefault(x => x.Name == segs[i]);
            if (next is null) return null;
            cur = next;
        }
        return cur;
    }

    /// <summary>移除命令（System.CommandLine 2.0 无公开移除 API，反射内部 _subcommands 列表）</summary>
    private static void RemoveCommand(Command parent, Command old)
    {
        var field = typeof(Command).GetField("_subcommands", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.GetValue(parent)?.GetType().GetMethod("Remove")?.Invoke(field.GetValue(parent), [old]);
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

    /// <summary>顶级 wtangent：已装集合纯本地扫描；索引只定优先级（顺序 = 桌面优先，headless 反转），
    /// 取第一个有 Default 的组件执行</summary>
    private static int RunTopLevel()
    {
        var installed = ComponentManager.InstalledComponents();
        var ordered = HeadlessComponent() == "gui"
            ? installed.OrderBy(ComponentManager.PriorityOf)
            : installed.OrderByDescending(ComponentManager.PriorityOf);
        foreach (var name in ordered)
        {
            var entry = ComponentManager.LoadEntry(name, App);
            if (entry?.Default is null) continue;
            return ComponentManager.RunDefault(name, App, []);
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
