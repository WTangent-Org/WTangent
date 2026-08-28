using System.CommandLine;

namespace WTangent.Commands;

/// <summary>检查并更新已装组件：wtangent upgrade [serve|tui|gui|web]（缺省：全部）</summary>
public sealed class UpgradeCommand : Command
{
    public UpgradeCommand() : base("upgrade", "检查并更新已装组件：wtangent upgrade [serve|tui|gui|web]（缺省：全部）")
    {
        var component = new Argument<string>("component")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "serve / tui / gui / web（缺省：检查全部已装组件）",
        };
        Add(component);
        SetAction(async pr => await ComponentManager.UpgradeAsync(pr.GetValue(component)));
    }
}
