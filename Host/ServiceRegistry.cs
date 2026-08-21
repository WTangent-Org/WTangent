using WTangent.Core;

namespace WTangent.Host;

/// <summary>服务注册表实现：类型 → 单例。同类型重复注册抛异常（覆盖是 bug 源）。</summary>
public sealed class ServiceRegistry : IServiceRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, object> _map = new();

    public void Register<T>(T impl) where T : class
    {
        lock (_lock)
        {
            if (!_map.TryAdd(typeof(T), impl))
                throw new InvalidOperationException($"服务 {typeof(T).Name} 已注册");
        }
    }

    public bool TryRegister<T>(T impl) where T : class
    {
        lock (_lock) return _map.TryAdd(typeof(T), impl);
    }

    public T? Resolve<T>() where T : class
    {
        lock (_lock) return _map.TryGetValue(typeof(T), out var v) ? (T)v : null;
    }
}
