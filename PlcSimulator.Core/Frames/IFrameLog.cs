namespace PlcSimulator.Core.Frames;

public enum FrameDirection
{
    Received,
    Sent,
}

/// <summary>一次报文收发的记录。原始字节只在开启 rawFrames 时填充。</summary>
public readonly record struct FrameLogEntry(
    DateTime Timestamp,
    string ConnectionId,
    string DeviceId,
    FrameDirection Direction,
    string Summary,
    byte[]? Raw,
    double LatencyMs)
{
    public string DirectionText => Direction == FrameDirection.Received ? "→" : "←";
}

/// <summary>报文日志接收端。GUI 与文件落盘都通过它订阅，协议层不认识具体实现。</summary>
public interface IFrameLog
{
    void Add(in FrameLogEntry entry);

    event EventHandler<FrameLogEntry>? EntryAdded;
}

/// <summary>丢弃全部记录的实现，用于纯命令行与测试场景。</summary>
public sealed class NullFrameLog : IFrameLog
{
    public static readonly NullFrameLog Instance = new();

    public event EventHandler<FrameLogEntry>? EntryAdded
    {
        add { }
        remove { }
    }

    public void Add(in FrameLogEntry entry)
    {
    }
}
