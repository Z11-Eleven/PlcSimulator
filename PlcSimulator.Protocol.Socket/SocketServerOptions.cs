namespace PlcSimulator.Protocol.Socket;

public sealed class SocketServerOptions
{
    public int MaxConnectionsPerIp { get; init; } = 8;

    public bool NoDelay { get; init; } = true;

    public bool RecordRawFrames { get; init; } = true;

    /// <summary>连接空闲多久后关闭。WCS 每周期都会轮询，正常不会触发。</summary>
    public int IdleTimeoutMs { get; init; } = 300_000;
}
