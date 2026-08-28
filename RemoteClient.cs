using System.Runtime.CompilerServices;
using WTangent.Core;

namespace WTangent;

/// <summary>远程客户端最小实现：remotes.json 读取 + 会话 API 调用。
/// serve 会话协议（WS/SSE 消息格式）由 serve 组件定义，完整客户端后续随协议下沉补齐；
/// 当前 Ask/Stream 返回明确错误提示，不静默失败。
/// 原 Host/RemoteClient.cs，空壳削薄后 Host/ 目录撤销，挪至根命名空间。</summary>
public sealed class RemoteClient(IAppStore store) : IRemoteClient
{
    public IReadOnlyList<RemoteEntry> ListRemotes()
    {
        var list = store.ReadJson<List<RemoteEntry>>("remotes.json");
        return list ?? [];
    }

    public Task<string?> AskAsync(string remote, string prompt, CancellationToken ct = default)
    {
        var hit = ListRemotes().FirstOrDefault(r => r.Name == remote);
        return Task.FromResult<string?>(hit is null
            ? $"远程 {remote} 未配置（wtangent remote add）"
            : "远程客户端尚未实现（serve 会话协议后续随 Core 下沉提供）");
    }

    public async IAsyncEnumerable<string> StreamAsync(string remote, string prompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return await AskAsync(remote, prompt, ct) ?? "";
    }
}
