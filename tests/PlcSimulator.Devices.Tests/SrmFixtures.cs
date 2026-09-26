using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Srm;

namespace PlcSimulator.Devices.Tests;

internal static class SrmFixtures
{
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 造一台 SC01 那样的双货叉堆垛机：延时压到几十毫秒，测试才跑得快。
    /// </summary>
    public static SrmDeviceRuntime CreateRuntime(
        int travelDelayMs = 100,
        int actionDelayMs = 100,
        int jitterMs = 0,
        int forkCount = 2,
        bool auto = true,
        bool fault = false,
        bool fireAlarm = false,
        int statusFrameLength = SrmLayout.DefaultStatusFrameLength)
    {
        var config = new DeviceConfig
        {
            Id = "srm-sc01",
            Name = "高温SC01",
            Ip = "127.0.0.1",
            Protocol = "Socket",
            ProtocolType = "SRM",
            ProtocolTypeRaw = "Socket_NTI",
            DeviceType = "Srm",
            SocketPorts = new SocketPortsConfig
            {
                Command = 2000,
                Status = 4000,
                Alarm = 6000,
                StatusFrameLength = statusFrameLength,
            },
            Srm = new SrmOptionsConfig
            {
                ForkType = "3;3",
                ForkCount = forkCount,
                TravelDelayMs = travelDelayMs,
                ActionDelayMs = actionDelayMs,
                JitterMs = jitterMs,
                Auto = auto,
                Fault = fault,
                FireAlarm = fireAlarm,
                StationPoints = { ["1271"] = 2, ["1273"] = 3, ["1073"] = 4, ["1075"] = 5 },
                PickStations = ["1271", "1073"],
                PutStations = ["1273", "1075"],
            },
        };

        return SrmDeviceRuntime.Create(config);
    }

    public static (SrmDeviceRuntime Runtime, SrmMachine Machine) CreateMachine(
        int travelDelayMs = 100,
        int actionDelayMs = 100,
        int jitterMs = 0,
        int forkCount = 2,
        bool auto = true,
        bool fault = false,
        bool fireAlarm = false,
        int? randomSeed = 0)
    {
        SrmDeviceRuntime runtime = CreateRuntime(
            travelDelayMs, actionDelayMs, jitterMs, forkCount, auto, fault, fireAlarm);

        return (runtime, new SrmMachine(runtime, randomSeed));
    }

    public static void Advance(SrmMachine machine, int milliseconds)
    {
        int ticks = (int)Math.Ceiling(milliseconds / Tick.TotalMilliseconds);
        for (int i = 0; i < ticks; i++)
        {
            machine.Tick(Tick);
        }
    }

    /// <summary>推进到指定状态出现为止，最多等 <paramref name="maxMs"/> 毫秒。返回是否等到。</summary>
    public static bool AdvanceUntil(SrmMachine machine, byte expectedReport, int maxMs = 5_000)
    {
        int ticks = (int)Math.Ceiling(maxMs / Tick.TotalMilliseconds);
        for (int i = 0; i < ticks; i++)
        {
            if (ReadFunctionReport(machine.Runtime) == expectedReport)
            {
                return true;
            }

            machine.Tick(Tick);
        }

        return ReadFunctionReport(machine.Runtime) == expectedReport;
    }

    public static byte ReadStatusByte(SrmDeviceRuntime runtime, int offset)
    {
        byte[] buffer = new byte[1];
        Assert.True(runtime.Space.TryReadBytes(SrmLayout.StatusAreaBase + offset, buffer));
        return buffer[0];
    }

    public static ushort ReadStatusU16(SrmDeviceRuntime runtime, int offset)
    {
        byte[] buffer = new byte[2];
        Assert.True(runtime.Space.TryReadBytes(SrmLayout.StatusAreaBase + offset, buffer));
        return (ushort)((buffer[0] << 8) | buffer[1]);
    }

    public static byte ReadFunctionReport(SrmDeviceRuntime runtime) => ReadStatusByte(runtime, 6);

    public static byte ReadFunctionMode(SrmDeviceRuntime runtime) => ReadStatusByte(runtime, 7);

    public static SrmCommand Command(
        byte commandType,
        ushort taskNum = 1001,
        byte forkNo = 1,
        byte actionPoint = 2,
        byte row = 1,
        byte column = 46,
        byte level = 6,
        byte fireFlag = 0)
    {
        // 双货叉下 WCS 会把不动的工位置 0，这里按货叉号放到对应工位。
        ushort fork1Task = forkNo == 2 ? (ushort)0 : taskNum;
        ushort fork2Task = forkNo == 1 ? (ushort)0 : taskNum;

        return new SrmCommand(
            Fork1TaskNum: fork1Task,
            Fork2TaskNum: fork2Task,
            CommandType: commandType,
            ForkNo: forkNo,
            Fork1GoodsType: 1,
            Fork2GoodsType: 0,
            ActionPoint: actionPoint,
            Aisle: 1,
            Row: row,
            Column: column,
            Cell: 1,
            Level: level,
            Depth: 1,
            FireFlag: fireFlag,
            Face1: 0,
            Face2: 0);
    }

    /// <summary>一条清除指令（WCS 在取放完成后收尾用），全零帧。</summary>
    public static SrmCommand ClearCommand(byte forkNo = 0) => new(
        Fork1TaskNum: 0,
        Fork2TaskNum: 0,
        CommandType: SrmCommandType.NoFunc,
        ForkNo: forkNo,
        Fork1GoodsType: 0,
        Fork2GoodsType: 0,
        ActionPoint: 0,
        Aisle: 0,
        Row: 0,
        Column: 0,
        Cell: 0,
        Level: 0,
        Depth: 0,
        FireFlag: 0,
        Face1: 0,
        Face2: 0);
}
