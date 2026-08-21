using System.CommandLine;

namespace WTangent.Commands;

/// <summary>安装组件：wtangent install serve|tui|gui|web [--force]</summary>
public sealed class InstallCommand : Command
{
    public InstallCommand() : base("install", "安装组件：wtangent install serve|tui|gui|web [--force]")
    {
        var component = new Argument<string>("component") { Description = "serve（服务端）/ tui（终端）/ gui（图形）/ web（Web UI）" };
        var force = new Option<bool>("--force") { Description = "强制重装（默认：已装跳过）" };
        Add(component);
        Add(force);
        SetAction(pr => ComponentManager.Install(pr.GetValue(component) ?? "", pr.GetValue(force)));
    }
}
