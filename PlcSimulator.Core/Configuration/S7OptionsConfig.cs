using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Core.Configuration;

/// <summary>S7 堆垛机的监听端口与 DB 字节地址，业务字段布局沿用 SRM。</summary>
public sealed class S7OptionsConfig
{
    public int Port { get; set; } = 102;

    public int MaxPduLength { get; set; } = 960;

    public S7DbRegionConfig Command { get; set; } = new() { DbNumber = 60 };

    public S7DbRegionConfig Status { get; set; } = new() { DbNumber = 61 };

    public S7DbRegionConfig Alarm { get; set; } = new() { DbNumber = 70 };

    public int StatusLength { get; set; } = 74;

    /// <summary>2 保留 Socket 指令块的两个前导字节与末尾对齐字节；0 对接 WCS 的 23 字节裸负载。</summary>
    public int CommandPayloadOffset { get; set; } = 2;

    public int CommandLength => CommandPayloadOffset == 2 ? SrmLayout.CommandFrameLength : SrmLayout.CommandAreaLength;

    public int AlarmLength { get; set; } = SrmLayout.AlarmAreaLength;
}

public sealed class S7DbRegionConfig
{
    public int DbNumber { get; set; }

    public int ByteOffset { get; set; }
}
