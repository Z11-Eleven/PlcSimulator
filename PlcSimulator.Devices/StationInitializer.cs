using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Devices;

/// <summary>
/// 把配置里的初值写进寄存器数据区。数据区存的是**线路表示**（真实 PLC 寄存器里的字节），
/// 所以初值要先经字节序策略编码再落盘——这样 WCS 读上去才是配置里写的那个值。
/// </summary>
public static class StationInitializer
{
    public static void Apply(DeviceRuntime device)
    {
        foreach (StationRuntime station in device.Stations)
        {
            Apply(device, station);
        }
    }

    public static void Apply(DeviceRuntime device, StationRuntime station)
    {
        InitialValuesConfig initial = station.Simulation.Initial;

        station.WriteOutgoingU16("tasknum", (ushort)initial.TaskNum);
        station.WriteOutgoingU16("goodstype", (ushort)initial.GoodsType);
        station.WriteOutgoingU16("from", (ushort)initial.FromStation);
        station.WriteOutgoingU16("to", (ushort)initial.ToStation);
        station.WriteOutgoingAscii("barcode", initial.Barcode);

        ApplyHandshake(device, station, initial);
        ApplyStatus(device, station, initial);
    }

    /// <summary>
    /// 握手字段的位置随协议不同：NTI 用独立的心跳寄存器，
    /// CATL 用回执字节 ack（见 ConveryPLC.BindStationCATL 里的 byt[32]）。
    /// </summary>
    public static string? ResolveHandshakeFieldName(IProtocolTemplate protocol)
    {
        if (protocol.TryGetReadField("heartbeat", out _))
        {
            return "heartbeat";
        }

        return protocol.TryGetReadField("ack", out _) ? "ack" : null;
    }

    private static void ApplyHandshake(DeviceRuntime device, StationRuntime station, InitialValuesConfig initial)
    {
        string? fieldName = ResolveHandshakeFieldName(device.ProtocolTemplate);
        if (fieldName is null)
        {
            return;
        }

        if (fieldName == "ack")
        {
            station.WriteOutgoingU8("ack", (byte)initial.Heartbeat);
            return;
        }

        station.WriteOutgoingU16("heartbeat", (ushort)initial.Heartbeat);
    }

    /// <summary>
    /// 状态字按位编码。位定义来自现场协议（输送线 NTI）：
    /// <list type="bullet">
    /// <item>X8 StaLoad：<b>0 = 有货，1 = 无货</b></item>
    /// <item>X9 AUTO：1 = 自动，0 = 手动</item>
    /// <item>X10 Fault：<b>1 = 有故障</b></item>
    /// <item>X11-X14：CW / CCW / UP / DN 动作方向；X0-X7、X15 备用</item>
    /// </list>
    /// 默认初值为「无货 + 自动 + 无故障」，即状态字 <c>0x0300</c>。
    /// <para>
    /// 注意：WCS 侧对 Fault 位的判定与此相反（`ConveryPLC.cs:414` 是
    /// <c>bit10 == 0 → 有故障</c>），属于待反馈的 WCS 缺陷。
    /// </para>
    /// </summary>
    private static void ApplyStatus(DeviceRuntime device, StationRuntime station, InitialValuesConfig initial)
    {
        if (device.ProtocolTemplate.Name.Equals(NtiProtocol.ProtocolName, StringComparison.OrdinalIgnoreCase))
        {
            int status = 0;

            if (!initial.Loaded)
            {
                status |= 1 << 8;
            }

            if (initial.Auto)
            {
                status |= 1 << 9;
            }

            if (initial.Fault)
            {
                status |= 1 << 10;
            }

            station.WriteOutgoingU16("status", (ushort)status);
            return;
        }

        if (device.ProtocolTemplate.Name.Equals(CatlProtocol.ProtocolName, StringComparison.OrdinalIgnoreCase))
        {
            station.WriteOutgoingU8("autoManual", (byte)(initial.Auto ? 2 : 1));
            station.WriteOutgoingU8("status", (byte)(initial.Loaded ? 0 : 1));
            station.WriteOutgoingU8("fault", (byte)(initial.Fault ? 0 : 1));
        }
    }
}
