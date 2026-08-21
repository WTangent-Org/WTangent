using System.CommandLine;

namespace WTangent.Commands;

/// <summary>卸载组件：wtangent remove serve|tui|gui|web（删组件目录 + 版本记录）</summary>
public sealed class RemoveCommand : Command
{
    public RemoveCommand() : base("remove", "卸载组件：wtangent remove serve|tui|gui|web")
    {
        var component = new Argument<string>("component") { Description = "serve / tui / gui / web" };
        Add(component);
        SetAction(pr => ComponentManager.Remove(pr.GetValue(component) ?? ""));
    }
}
