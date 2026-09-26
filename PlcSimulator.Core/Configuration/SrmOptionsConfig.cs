using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Core.Configuration;

/// <summary>
/// Socket 传输的三端口与帧长。<c>protocol = "Socket"</c> 时必填。
/// <para>
/// 三个端口的分工见 WCS 的 <c>ScSocket.cs</c>：2000 收指令、4000 被轮询状态、6000 被轮询报警。
/// 两个帧长做成配置项，是因为真机的实际线长未确证——WCS 的解析器只读到固定偏移，
/// 发得比它长无害，所以现场一旦发现对端在等更长的帧，改配置即可。
/// </para>
/// </summary>
public sealed class SocketPortsConfig
{
    /// <summary>指令口：WCS 发 26 字节指令帧，模拟器只读不应答。</summary>
    public int Command { get; set; } = 2000;

    /// <summary>状态口：WCS 每周期发一个 0xFF，模拟器回一帧状态。</summary>
    public int Status { get; set; } = 4000;

    /// <summary>报警口：WCS 每周期发一个 0xFF，模拟器回一帧报警位图。</summary>
    public int Alarm { get; set; } = 6000;

    public int StatusFrameLength { get; set; } = SrmLayout.DefaultStatusFrameLength;

    public int AlarmFrameLength { get; set; } = SrmLayout.DefaultAlarmFrameLength;
}

/// <summary>堆垛机专项参数，对应数据库 wcs_equipmentinfo 的若干字段。</summary>
public sealed class SrmOptionsConfig
{
    /// <summary>数据库 forktype 原值，形如 "3;3"（第一段是实际货叉类型、第二段是当前配置）。</summary>
    public string ForkType { get; set; } = "3;3";

    /// <summary>货叉数：1 或 2。</summary>
    public int ForkCount { get; set; } = 2;

    /// <summary>行走/定位耗时。</summary>
    public int TravelDelayMs { get; set; } = 1000;

    /// <summary>取货/放货/盘点动作耗时。</summary>
    public int ActionDelayMs { get; set; } = 2000;

    public int JitterMs { get; set; } = 200;

    /// <summary>站台号 → 动作点。对应数据库 stationpoint 的 "1271,2;1273,3;" 形式。</summary>
    public Dictionary<string, int> StationPoints { get; set; } = new(StringComparer.Ordinal);

    /// <summary>取货站台号（数据库 pickstation，逗号分隔已拆开）。</summary>
    public List<string> PickStations { get; set; } = [];

    /// <summary>放货站台号（数据库 putstation）。</summary>
    public List<string> PutStations { get; set; } = [];

    /// <summary>火警避让点。对应数据库 field5 的最后两段（近端/远端避让点）。</summary>
    public SrmPointConfig AvoidancePoint { get; set; } = new();

    /// <summary>数据库 field4 原值（"近端1,近端2;远端1,远端2"）。暂存原值，不参与判定。</summary>
    public string NearFarStationsRaw { get; set; } = string.Empty;

    /// <summary>数据库 field5 原值（"宽1;宽2;近端宽;远端宽;近端避让点;远端避让点"）。暂存原值。</summary>
    public string AvoidanceRaw { get; set; } = string.Empty;

    /// <summary>堆垛机开机时的位置。</summary>
    public SrmPointConfig InitialPoint { get; set; } = new();

    public bool Auto { get; set; } = true;

    public bool Fault { get; set; }

    /// <summary>初值是否带火警。用于在没有 WCS 时造避让场景。</summary>
    public bool FireAlarm { get; set; }
}

/// <summary>堆垛机的一个位置（动作点 + 巷道排层深）。</summary>
public sealed class SrmPointConfig
{
    public byte ActionPoint { get; set; }

    public byte Aisle { get; set; } = 1;

    public byte Row { get; set; }

    public byte Column { get; set; }

    public byte Cell { get; set; }

    public byte Level { get; set; }

    public byte Depth { get; set; } = 1;
}
