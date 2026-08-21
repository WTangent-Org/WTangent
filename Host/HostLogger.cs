using WTangent.Core;

namespace WTangent.Host;

/// <summary>日志实现：控制台（带级别前缀）+ 追加写 %APPDATA%\agent\logs\wtangent.log</summary>
public sealed class HostLogger : ILogger
{
    private readonly object _lock = new();
    private readonly string _logFile;

    public HostLogger()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "logs");
        Directory.CreateDirectory(dir);
        _logFile = Path.Combine(dir, "wtangent.log");
    }

    public void Debug(string msg) => Write("DEBUG", msg);
    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}\n{ex}");

    private void Write(string level, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
        Console.WriteLine(line);
        try
        {
            lock (_lock) File.AppendAllText(_logFile, line + Environment.NewLine);
        }
        catch { /* 日志文件写失败不阻断 */ }
    }
}
