namespace PlcSimulator.Core.Configuration;

public sealed class DeviceConfig
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Ip { get; set; } = string.Empty;

    public int Port { get; set; } = 502;

    public byte SlaveId { get; set; } = 1;

    /// <summary>Modbus 或 S7（后者为预留）。</summary>
    public string Protocol { get; set; } = "Modbus";

    /// <summary>数据库 protocolType 原值，仅用于导入核对，例如 "NTI_Modbus"。</summary>
    public string ProtocolTypeRaw { get; set; } = string.Empty;

    public string DeviceType { get; set; } = "Conveyor";

    /// <summary>对接协议：NTI / CATL / Robot / SRM，决定各字段的字节偏移。对应数据库 wcs_opcitem.protocolType。</summary>
    public string ProtocolType { get; set; } = "NTI";

    public string? ReadByteOrderPolicy { get; set; }

    public string? SingleFieldWriteByteOrderPolicy { get; set; }

    public int PollHintMs { get; set; }

    /// <summary>产线号，对应数据库 belong。</summary>
    public string Belong { get; set; } = string.Empty;

    /// <summary>Socket 传输的三端口与线长；<c>Protocol = "Socket"</c> 时必填。</summary>
    public SocketPortsConfig? SocketPorts { get; set; }

    /// <summary>堆垛机专项参数；<c>Protocol = "Socket"</c> 时必填。</summary>
    public SrmOptionsConfig? Srm { get; set; }

    public List<RegisterBlockConfig> Blocks { get; set; } = [];

    public List<StationConfig> Stations { get; set; } = [];
}

public sealed class RegisterBlockConfig
{
    public string Group { get; set; } = string.Empty;

    public int BaseByteOffset { get; set; }

    public int LengthBytes { get; set; }
}

public sealed class StationConfig
{
    public string StationNo { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    /// <summary>站台在块内的起始字节偏移，对应数据库 wcs_opcitem.value。</summary>
    public int ByteOffset { get; set; }

    /// <summary>站台数据块长度（字节），对应数据库 wcs_opcitem.signaltype。</summary>
    public int LengthBytes { get; set; }

    public int StationType { get; set; }

    public string Remark { get; set; } = string.Empty;

    public int LocationX { get; set; }

    public int LocationY { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>
    /// 输送方向，可多值（如 "1,4"）。1 = 上、2 = 下、3 = 左、4 = 右。
    /// "0" 表示该站台不参与输送拓扑。用于推导「下一站是谁」。
    /// </summary>
    public string ArrowDirection { get; set; } = string.Empty;

    /// <summary>区域码。只有同区域的站台才可能相邻。</summary>
    public string ZoneCode { get; set; } = string.Empty;

    /// <summary>数据库 field5。值为 "1" 时该站台不参与输送拓扑。</summary>
    public string Field5 { get; set; } = string.Empty;

    public SimulationConfig Simulation { get; set; } = new();

    /// <summary>
    /// 浅拷贝。用于「放开箭头约束」这类只读诊断：克隆一份改掉某个字段，
    /// 交给同一套建图算法，避免诊断与运行各写一份判定。
    /// </summary>
    public StationConfig Clone() => (StationConfig)MemberwiseClone();
}

public sealed class SimulationConfig
{
    public InitialValuesConfig Initial { get; set; } = new();

    /// <summary>模拟设备动作耗时（货物在本站移动）。</summary>
    public int ActionDelayMs { get; set; } = 3000;

    /// <summary>
    /// 货物在途时长：离开本站到抵达目标站台的耗时。这段时间两站都是无货，
    /// 便于在界面上观察到中间态。
    /// </summary>
    public int TransferDelayMs { get; set; } = 1000;

    /// <summary>随机抖动，避免所有站台同时动作。</summary>
    public int JitterMs { get; set; } = 200;

    /// <summary>置心跳后等待 WCS 清零的超时，超时记告警。</summary>
    public int ClearTimeoutMs { get; set; } = 10_000;

    public HeartbeatConfig Heartbeat { get; set; } = new();

    public AutoTaskConfig? AutoTask { get; set; }
}

public sealed class InitialValuesConfig
{
    public int TaskNum { get; set; }

    public int GoodsType { get; set; }

    public int FromStation { get; set; }

    public int ToStation { get; set; }

    public string Barcode { get; set; } = string.Empty;

    public int Heartbeat { get; set; }

    /// <summary>
    /// 站台是否有货。对应状态字 X8（0 有货 / 1 无货）。默认无货。
    /// </summary>
    public bool Loaded { get; set; }

    /// <summary>手自动。对应状态字 X9（1 自动 / 0 手动）。默认自动。</summary>
    public bool Auto { get; set; } = true;

    public bool Fault { get; set; }
}

public sealed class HeartbeatConfig
{
    /// <summary>
    /// 模拟器置位值，表示「有事要 WCS 处理」。心跳所在的字节偏移由对接协议决定
    /// （NTI 为 8，机械手为 10），不在这里配置。
    /// </summary>
    public int SetValue { get; set; } = 1;

    /// <summary>WCS 清零时写入的值。</summary>
    public int ClearOnValue { get; set; } = 2;

    /// <summary>Once = 置位后等 WCS 清零；Toggle = 周期性翻转（仅作连通性探针，会导致心跳风暴）。</summary>
    public string Mode { get; set; } = "Once";
}

public sealed class AutoTaskConfig
{
    public bool Enabled { get; set; }

    public int TaskNum { get; set; } = 1001;

    public int GoodsType { get; set; } = 1;

    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public string Barcode { get; set; } = string.Empty;
}
