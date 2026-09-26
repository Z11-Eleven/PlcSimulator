using PlcSimulator.Core.Configuration;

namespace PlcSimulator.Integration.Tests;

internal static class TestPorts
{
    private static int _next = 15500;

    public static int Next() => Interlocked.Increment(ref _next);
}

internal static class TestConfigFactory
{
    public const int StationLengthBytes = 30;

    /// <summary>
    /// 构造一份与 config/simulator.json 等价的内存配置（端口可注入，避免测试相互冲突）。
    /// 寄存器块按 WCS 写路径的最坏范围开：<c>站台数 × 30 × 2</c> 字节。
    /// </summary>
    public static SimulatorConfig BuildConveyor(int port, params string[] stationNos)
        => BuildConveyor(port, actionDelayMs: 3000, jitterMs: 200, stationNos);

    /// <summary>同上，但可指定动作延时，供需要快速跑闭环的测试使用。</summary>
    public static SimulatorConfig BuildConveyor(int port, int actionDelayMs, int jitterMs, params string[] stationNos)
    {
        int totalStationBytes = stationNos.Length * StationLengthBytes;

        var device = new DeviceConfig
        {
            Id = "cv-test",
            Name = "测试输送机",
            Ip = "127.0.0.1",
            Port = port,
            SlaveId = 1,
            Protocol = "Modbus",
            ProtocolTypeRaw = "NTI_Modbus",
            DeviceType = "Conveyor",
            ProtocolType = "NTI",
            ReadByteOrderPolicy = "NTI-ConveyorRead",
            SingleFieldWriteByteOrderPolicy = "SingleFieldWrite",
            Belong = "1",
            PollHintMs = 1000,
            Blocks =
            [
                new RegisterBlockConfig
                {
                    Group = string.Empty,
                    BaseByteOffset = 0,
                    LengthBytes = totalStationBytes * 2,
                },
            ],
            Stations =
            [
                .. stationNos.Select((stationNo, index) => new StationConfig
                {
                    StationNo = stationNo,
                    Name = stationNo,
                    ByteOffset = index * StationLengthBytes,
                    LengthBytes = StationLengthBytes,
                    Simulation = new SimulationConfig
                    {
                        ActionDelayMs = actionDelayMs,
                        JitterMs = jitterMs,
                    },
                }),
            ],
        };

        return new SimulatorConfig
        {
            Server = new ServerConfig
            {
                Listen = [new ListenEndpointConfig { Ip = "127.0.0.1", Port = port }],
                AcceptAnySlaveId = true,
            },
            ByteOrder = new ByteOrderConfig
            {
                Policies =
                {
                    ["NTI-ConveyorRead"] = new ByteOrderPolicyConfig
                    {
                        ExtraPairUnswapRanges = [[12, 28]],
                    },
                    ["NTI-ConveyorWrite"] = new ByteOrderPolicyConfig
                    {
                        SwapFromByteOffset = 6,
                    },
                    ["SingleFieldWrite"] = new ByteOrderPolicyConfig(),
                },
            },
            Devices = [device],
        };
    }

    /// <summary>
    /// 构造一份只含一台堆垛机的内存配置。三个端口由调用方各取一个空闲值，
    /// 避免测试之间以及与其他测试进程相互冲突。
    /// <para>
    /// 注意 <c>Server.Listen</c> 是空的：堆垛机的端口来自设备自己的 socketPorts，
    /// 这正是「只跑堆垛机的配置不该因为 server.listen 为空而起不来」的那条路径。
    /// </para>
    /// </summary>
    public static SimulatorConfig BuildSrm(
        int commandPort,
        int statusPort,
        int alarmPort,
        int travelDelayMs = 100,
        int actionDelayMs = 100,
        int jitterMs = 0)
    {
        var device = new DeviceConfig
        {
            Id = "SC01",
            Name = "高温SC01",
            Ip = "127.0.0.1",
            Protocol = "Socket",
            ProtocolType = "SRM",
            ProtocolTypeRaw = "Socket_NTI",
            DeviceType = "Srm",
            Belong = "1",
            SocketPorts = new SocketPortsConfig
            {
                Command = commandPort,
                Status = statusPort,
                Alarm = alarmPort,
            },
            Srm = new SrmOptionsConfig
            {
                ForkType = "3;3",
                ForkCount = 2,
                TravelDelayMs = travelDelayMs,
                ActionDelayMs = actionDelayMs,
                JitterMs = jitterMs,
                StationPoints = { ["1271"] = 2, ["1273"] = 3 },
                PickStations = ["1271"],
                PutStations = ["1273"],
            },
        };

        return new SimulatorConfig
        {
            Server = new ServerConfig { Listen = [] },
            Devices = [device],
        };
    }
}
