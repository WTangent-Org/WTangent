namespace WTangent;

/// <summary>组件管理：索引（apt 模式）+ 安装/卸载/升级 + dll 加载 + Entry 反射推导 + 依赖解析。
/// partial 拆分职责地图：
/// ComponentManager.Index.cs = 索引（components.json 拉取/缓存/兜底、别名查找、展示优先级）；
/// ComponentManager.Install.cs = 安装/卸载/升级（依赖解析、zip 下载解压、latest 查询）；
/// ComponentManager.Load.cs = 加载（dll 加载、IEntry 推导、启动、manifest 读取、加载顺序）；
/// ComponentManager.Meta.cs = 安装元数据（.installed / 旧 .version 兼容）与路径/Http/JSON 助手</summary>
public static partial class ComponentManager
{
}
