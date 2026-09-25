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
}
