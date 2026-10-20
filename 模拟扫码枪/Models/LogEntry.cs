using System;

namespace 模拟扫码枪;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// 单条日志
/// </summary>
public sealed class LogEntry
{
    public LogEntry(DateTime time, LogLevel level, string text)
    {
        Time = time;
        Level = level;
        Text = text;
    }

    public DateTime Time { get; }
    public LogLevel Level { get; }
    public string Text { get; }
    public string TimeText => Time.ToString("HH:mm:ss");
    public string TimeFullText => Time.ToString("HH:mm:ss.fff");
}
