using System.Text.Json;

namespace WTangent;

// 本文件 = ComponentManager 安装元数据 + 路径/Http/JSON 助手部分（职责地图见 ComponentManager.cs）
public static partial class ComponentManager
{
    /// <summary>组件安装目录（%APPDATA%\agent\components）</summary>
    public static string ComponentsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "components");

    /// <summary>索引缓存文件（%APPDATA%\agent\components.json）</summary>
    private static string IndexFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "components.json");

    private static string ComponentDir(string component) => Path.Combine(ComponentsDir, component);

    private static string ManifestFile(string name) => Path.Combine(ComponentDir(name), "agent-component.json");

    /// <summary>JSON 选项：索引字段 camelCase 与 record 参数 PascalCase 匹配</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>按需新实例（下载大文件等长超时场景；用后 Dispose，与 WtAgent.Core.Http 同构）</summary>
    private static HttpClient NewHttp(TimeSpan timeout) => new() { Timeout = timeout };

    /// <summary>删目录（被占用时退避重试）：别的 wtangent 进程加载着该组件 dll 时删除会失败——
    /// 这不是本进程自锁（管理/开发命令不加载组件），子进程也绕不开 Windows 文件锁，
    /// 所以正解是重试 + 提示关掉正在跑的实例。install --force/remove/dev install 的删除都走这里</summary>
    internal static void DeleteDirRetry(string dir, int attempts = 3)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                Directory.Delete(dir, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && i < attempts)
            {
                Console.Error.WriteLine($"[wtangent] {dir} 被占用（有 wtangent 正在运行？），{300 * i}ms 后重试…");
                Thread.Sleep(300 * i);
            }
        }
    }

    /// <summary>安装元数据（components\{component}\.installed，JSON：安装来源仓库 + 版本 tag；
    /// 升级/卸载凭它走本地，不依赖远程索引。旧安装只有 .version：读取时回退，Repo 留空走索引）</summary>
    private sealed record InstallMeta(string? Repo, string? Version);

    private static string MetaFile(string component) => Path.Combine(ComponentsDir, component, ".installed");

    private static InstallMeta? ReadMeta(string component)
    {
        try
        {
            var f = MetaFile(component);
            if (File.Exists(f))
                return JsonSerializer.Deserialize<InstallMeta>(File.ReadAllText(f), JsonOpts);
            var v = ReadVersion(component);   // 旧安装只有 .version
            return v is null ? null : new InstallMeta(null, v);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SaveMeta(string component, string repo, string tag) =>
        File.WriteAllText(MetaFile(component), JsonSerializer.Serialize(new InstallMeta(repo, tag)));

    /// <summary>旧版安装版本记录（components\{component}\.version，内容为 release tag；新安装已并入 .installed，仅为兼容保留读取）</summary>
    private static string VersionFile(string component) => Path.Combine(ComponentsDir, component, ".version");

    private static string? ReadVersion(string component)
    {
        try
        {
            return File.Exists(VersionFile(component)) ? File.ReadAllText(VersionFile(component)).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
