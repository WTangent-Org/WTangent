using System.Collections.Concurrent;
using WTangent.Core;

namespace WTangent.Host;

/// <summary>事件总线实现：string 键 + 任意值。线程安全（发布时快照遍历）；同步发布；
/// 单个 handler 异常只记日志，不中断其他 handler。</summary>
public sealed class HostEventBus(ILogger log) : IEventBus
{
    private readonly ConcurrentDictionary<string, List<Action<object?>>> _subs = new();

    public void Publish(string key, object? payload)
    {
        if (!_subs.TryGetValue(key, out var handlers)) return;
        foreach (var h in handlers.ToArray())   // 快照：订阅/退订不影响本次发布
        {
            try { h(payload); }
            catch (Exception e) { log.Error($"事件 {key} 的 handler 异常：{e.Message}", e); }
        }
    }

    public IDisposable Subscribe(string key, Action<object?> handler)
    {
        var list = _subs.GetOrAdd(key, _ => []);
        lock (list) list.Add(handler);
        return new Unsubscriber(this, key, list, handler);
    }

    private void Unsubscribe(string key, List<Action<object?>> list, Action<object?> handler)
    {
        lock (list) list.Remove(handler);
        if (list.Count == 0) _subs.TryRemove(key, out _);
    }

    private sealed class Unsubscriber(HostEventBus bus, string key, List<Action<object?>> list, Action<object?> handler) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) bus.Unsubscribe(key, list, handler);
        }
    }
}
