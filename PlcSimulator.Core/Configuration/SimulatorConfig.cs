namespace PlcSimulator.Core.Configuration;

public sealed class SimulatorConfig
{
    public int Version { get; set; } = 1;

    public ServerConfig Server { get; set; } = new();

    public ByteOrderConfig ByteOrder { get; set; } = new();

    public FaultInjectionConfig FaultInjection { get; set; } = new();

    public List<DeviceConfig> Devices { get; set; } = [];
}

public sealed class ServerConfig
{
    public List<ListenEndpointConfig> Listen { get; set; } = [];

    public byte DefaultSlaveId { get; set; } = 1;

    /// <summary>
    /// 是否容忍站号不匹配。WCS 的闭源 DLL 可能把站台号当作单元标识发出来，
    /// 单设备端点通常需要打开。
    /// </summary>
    public bool AcceptAnySlaveId { get; set; } = true;

    public int MaxConnectionsPerIp { get; set; } = 8;

    /// <summary>是否在报文日志里保留原始十六进制字节。高频轮询下建议关闭。</summary>
    public bool RecordRawFrames { get; set; } = true;
}

public sealed class ListenEndpointConfig
{
    public string Ip { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 502;

    public bool Enabled { get; set; } = true;
}

public sealed class ByteOrderConfig
{
    public Dictionary<string, ByteOrderPolicyConfig> Policies { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ByteOrderPolicyConfig
{
    public string WordByteOrder { get; set; } = "BigEndian";

    /// <summary>从该字节偏移起每 2 字节反序；null 表示不整体反序。</summary>
    public int? SwapFromByteOffset { get; set; }

    /// <summary>额外需要反序的区间，每项形如 [start, end)，用于条码这类单独规则的字段。</summary>
    public List<int[]> ExtraPairUnswapRanges { get; set; } = [];
}
