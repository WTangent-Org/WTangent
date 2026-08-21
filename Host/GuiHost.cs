using WTangent.Core;

namespace WTangent.Host;

/// <summary>GUI 宿主占位实现（未来 gui 组件挂载视图用）</summary>
public sealed class GuiHost : IGuiHost
{
    public void ShowView(object view) { }
    public void CloseView(object view) { }
}
