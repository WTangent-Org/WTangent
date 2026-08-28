using System.CommandLine;

namespace WTangent.Commands;

/// <summary>刷新组件索引：wtangent update（拉 GitHub components.json → 本地缓存）</summary>
public sealed class UpdateCommand : Command
{
    public UpdateCommand() : base("update", "刷新组件索引（components.json 缓存）")
    {
        SetAction(async _ =>
        {
            var ok = await ComponentManager.UpdateIndexAsync();
            Console.WriteLine(ok
                ? $"[wtangent] 索引已刷新：{ComponentManager.Index.Count} 个组件（{ComponentManager.IndexUrl}）"
                : "[wtangent] 索引刷新失败（离线？用本地缓存）");
            return ok ? 0 : 1;
        });
    }
}
