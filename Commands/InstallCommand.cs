using System.CommandLine;

namespace WTangent.Commands;

/// <summary>安装组件：wtangent install [serve|tui|client|gui|git] [--force]。
/// 无参数 = 安装官方组件清单（serve/tui/client/git；gui 未实现跳过）——下载器/首次运行开箱即用。</summary>
public sealed class InstallCommand : Command
{
    /// <summary>官方组件清单（按序安装；gui 未实现，等实现后加入）</summary>
    private static readonly string[] OfficialComponents = ["serve", "tui", "client", "git"];

    public InstallCommand() : base("install", "安装组件：wtangent install [serve|tui|client|gui|git] [--force]（无参数=官方组件全装）")
    {
        var component = new Argument<string?>("component")
        {
            Description = "serve（服务端）/ tui（终端 UI）/ client（客户端命令）/ gui（图形 UI）/ git（git 命令）",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var force = new Option<bool>("--force") { Description = "强制重装（默认：已装跳过）" };
        Add(component);
        Add(force);
        SetAction(pr =>
        {
            var name = pr.GetValue(component);
            if (name is null)
            {
                // 官方组件全装：缺省清单，逐个安装（已装跳过）
                var rc = 0;
                foreach (var c in OfficialComponents)
                    rc |= ComponentManager.Install(c, pr.GetValue(force));
                return rc;
            }
            return ComponentManager.Install(name, pr.GetValue(force));
        });
    }
}
