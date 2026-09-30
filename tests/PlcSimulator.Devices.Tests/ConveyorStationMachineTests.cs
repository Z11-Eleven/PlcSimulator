using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Stations;

namespace PlcSimulator.Devices.Tests;

internal static class StationFixtures
{
    public const int HeartbeatByteOffset = 8;
    public const int StatusByteOffset = 28;

    /// <summary>
    /// 构造一个 NTI + Modbus 的单站台设备，字节序策略与 config/simulator.json 一致。
    /// </summary>
    public static StationRuntime CreateStation(
        int actionDelayMs = 1000,
        int jitterMs = 0,
        int clearTimeoutMs = 0,
        bool loaded = false,
        bool auto = true)
    {
        var config = new DeviceConfig
        {
            Id = "cv-test",
            Name = "测试输送机",
            Ip = "127.0.0.1",
            ProtocolType = NtiProtocol.ProtocolName,
            Protocol = "Modbus",
            ReadByteOrderPolicy = "NTI-ConveyorRead",
            SingleFieldWriteByteOrderPolicy = "SingleFieldWrite",
            Blocks = [new RegisterBlockConfig { Group = string.Empty, BaseByteOffset = 0, LengthBytes = 256 }],
            Stations =
            [
                new StationConfig
                {
                    StationNo = "1004",
                    Name = "1004",
                    ByteOffset = 0,
                    LengthBytes = 30,
                    Simulation = new SimulationConfig
                    {
                        ActionDelayMs = actionDelayMs,
                        JitterMs = jitterMs,
                        ClearTimeoutMs = clearTimeoutMs,
                        Initial = new InitialValuesConfig { Loaded = loaded, Auto = auto },
                    },
                },
            ],
        };

        var device = new DeviceRuntime(
            config,
            NtiProtocol.Instance,
            new ByteOrderPolicy { Name = "NTI-ConveyorRead", ExtraPairUnswapRanges = [[12, 28]] },
            new ByteOrderPolicy { Name = "SingleFieldWrite" });

        StationInitializer.Apply(device);
        return device.Stations[0];
    }

    /// <summary>
    /// 造一台带下游站台的状态机：本站 1004、下游 1003，目标写 1003 就能走完
    /// 「动作 → 离站 → 送达 → 置心跳」的整条闭环。
    /// <para>
    /// 不配寻路拓扑，按任务里的目标一步投送；在途时长配成 0，送达是即时的。
    /// 下游只负责接收，自己不会被推进——本夹具测的是本站的流程，不是货物的实际走位。
    /// </para>
    /// </summary>
    public static (ConveyorStationMachine Machine, StationRuntime Station) CreateMachineWithDownstream(
        int actionDelayMs = 1000,
        int jitterMs = 0,
        int clearTimeoutMs = 0,
        bool loaded = false,
        bool auto = true)
    {
        var config = new DeviceConfig
        {
            Id = "cv-test",
            Name = "测试输送机",
            Ip = "127.0.0.1",
            ProtocolType = NtiProtocol.ProtocolName,
            Protocol = "Modbus",
            ReadByteOrderPolicy = "NTI-ConveyorRead",
            SingleFieldWriteByteOrderPolicy = "SingleFieldWrite",
            Blocks = [new RegisterBlockConfig { Group = string.Empty, BaseByteOffset = 0, LengthBytes = 256 }],
            Stations =
            [
                new StationConfig
                {
                    StationNo = "1004",
                    Name = "1004",
                    ByteOffset = 0,
                    LengthBytes = 30,
                    Simulation = new SimulationConfig
                    {
                        ActionDelayMs = actionDelayMs,
                        TransferDelayMs = 0,
                        JitterMs = jitterMs,
                        ClearTimeoutMs = clearTimeoutMs,
                        Initial = new InitialValuesConfig { Loaded = loaded, Auto = auto },
                    },
                },
                new StationConfig
                {
                    StationNo = "1003",
                    Name = "1003",
                    ByteOffset = 30,
                    LengthBytes = 30,
                },
            ],
        };

        var device = new DeviceRuntime(
            config,
            NtiProtocol.Instance,
            new ByteOrderPolicy { Name = "NTI-ConveyorRead", ExtraPairUnswapRanges = [[12, 28]] },
            new ByteOrderPolicy { Name = "SingleFieldWrite" });

        StationInitializer.Apply(device);

        StationRuntime upstream = device.Stations.First(static s => s.StationNo == "1004");
        StationRuntime downstream = device.Stations.First(static s => s.StationNo == "1003");

        ConveyorStationMachine? upstreamMachine = null;
        ConveyorStationMachine? downstreamMachine = null;

        ConveyorStationMachine? Lookup(string stationNo) => stationNo switch
        {
            "1004" => upstreamMachine,
            "1003" => downstreamMachine,
            _ => null,
        };

        upstreamMachine = new ConveyorStationMachine(upstream, randomSeed: 1, stationLookup: Lookup);
        downstreamMachine = new ConveyorStationMachine(downstream, randomSeed: 1, stationLookup: Lookup);

        return (upstreamMachine, upstream);
    }

    /// <summary>
    /// 模拟 WCS 写入一个 16 位值。按 2026-09-23 修正后的交换规则，
    /// WCS 写入不再需要高低位转换，这里就是大端直写。
    /// </summary>
    public static void WcsWriteU16(StationRuntime station, int blockByteOffset, ushort value)
    {
        byte[] buffer = [(byte)(value >> 8), (byte)(value & 0xFF)];
        Assert.True(station.Device.Space.TryWriteBytes(station.ByteOffset + blockByteOffset, buffer));
    }

    /// <summary>按 WCS 的读法（大端）读取数据区里的一个 16 位值。</summary>
    public static ushort ReadRawU16(StationRuntime station, int blockByteOffset)
    {
        byte[] buffer = new byte[2];
        Assert.True(station.Device.Space.TryReadBytes(station.ByteOffset + blockByteOffset, buffer));
        return (ushort)((buffer[0] << 8) | buffer[1]);
    }
}

public class ConveyorStationMachineTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private static void Advance(ConveyorStationMachine machine, int milliseconds)
    {
        int ticks = milliseconds / 50;
        for (int i = 0; i < ticks; i++)
        {
            machine.Tick(Tick);
        }
    }

    [Fact]
    public void Tick_BeforeAnyTask_StaysIdle()
    {
        StationRuntime station = StationFixtures.CreateStation();
        var machine = new ConveyorStationMachine(station, randomSeed: 1);

        Advance(machine, 500);

        Assert.Equal(StationState.Idle, machine.State);
    }

    [Fact]
    public void Tick_AfterWcsWritesTaskNumber_EntersExecuting()
    {
        StationRuntime station = StationFixtures.CreateStation(actionDelayMs: 1000);
        var machine = new ConveyorStationMachine(station, randomSeed: 1);

        StationFixtures.WcsWriteU16(station, 0, 1234);
        machine.Tick(Tick);

        Assert.Equal(StationState.Executing, machine.State);
    }

    [Fact]
    public void Tick_AfterActionDelay_RaisesHeartbeatAndEntersDone()
    {
        (ConveyorStationMachine machine, StationRuntime station) =
            StationFixtures.CreateMachineWithDownstream(actionDelayMs: 1000);

        StationFixtures.WcsWriteU16(station, 0, 1234);
        StationFixtures.WcsWriteU16(station, 6, 1003);   // 目标写下游站台，货才送得出去
        machine.Tick(Tick);
        Advance(machine, 1050);                          // 动作 1000 ms 走完，再一个 tick 送达

        Assert.Equal(StationState.Done, machine.State);
        Assert.Equal(1, StationFixtures.ReadRawU16(station, StationFixtures.HeartbeatByteOffset));
    }

    [Fact]
    public void Tick_AfterWcsClearsHeartbeat_ReturnsToIdleAndZeroesHeartbeat()
    {
        (ConveyorStationMachine machine, StationRuntime station) =
            StationFixtures.CreateMachineWithDownstream(actionDelayMs: 1000);

        StationFixtures.WcsWriteU16(station, 0, 1234);
        StationFixtures.WcsWriteU16(station, 6, 1003);
        machine.Tick(Tick);
        Advance(machine, 1050);

        // WCS 读到心跳 == 1 后写清零值 2（按 2026-09-23 修正后的规则，写入不做转换）。
        StationFixtures.WcsWriteU16(station, StationFixtures.HeartbeatByteOffset, 2);
        machine.Tick(Tick);

        Assert.Equal(StationState.Idle, machine.State);
        Assert.Equal(0, station.ReadIncomingU16("heartbeat"));
    }

    [Fact]
    public void Tick_WithUnchangedTaskNumber_DoesNotRetrigger()
    {
        (ConveyorStationMachine machine, StationRuntime station) =
            StationFixtures.CreateMachineWithDownstream(actionDelayMs: 1000);

        StationFixtures.WcsWriteU16(station, 0, 1234);
        StationFixtures.WcsWriteU16(station, 6, 1003);
        machine.Tick(Tick);
        Advance(machine, 1050);

        StationFixtures.WcsWriteU16(station, StationFixtures.HeartbeatByteOffset, 2);
        machine.Tick(Tick);
        Assert.Equal(StationState.Idle, machine.State);

        // 托盘离站时本站字段已经清空，不会有残留的任务号把它再触发一次。
        Advance(machine, 500);
        Assert.Equal(StationState.Idle, machine.State);
    }

    [Fact]
    public void Tick_WithNewTaskNumberAfterCompletion_Retriggers()
    {
        (ConveyorStationMachine machine, StationRuntime station) =
            StationFixtures.CreateMachineWithDownstream(actionDelayMs: 1000);

        StationFixtures.WcsWriteU16(station, 0, 1234);
        StationFixtures.WcsWriteU16(station, 6, 1003);
        machine.Tick(Tick);
        Advance(machine, 1050);
        StationFixtures.WcsWriteU16(station, StationFixtures.HeartbeatByteOffset, 2);
        machine.Tick(Tick);

        StationFixtures.WcsWriteU16(station, 0, 5678);
        machine.Tick(Tick);

        Assert.Equal(StationState.Executing, machine.State);
    }

    [Fact]
    public void Tick_WithoutWcsClearing_RaisesClearTimeout()
    {
        (ConveyorStationMachine machine, StationRuntime station) =
            StationFixtures.CreateMachineWithDownstream(actionDelayMs: 1000, clearTimeoutMs: 2000);

        bool timedOut = false;
        machine.ClearTimedOut += (_, _) => timedOut = true;

        StationFixtures.WcsWriteU16(station, 0, 1234);
        StationFixtures.WcsWriteU16(station, 6, 1003);
        machine.Tick(Tick);
        Advance(machine, 1050);
        Assert.Equal(StationState.Done, machine.State);

        Advance(machine, 2000);

        Assert.True(timedOut);
        Assert.Equal(StationState.Done, machine.State);
    }

    [Fact]
    public void SetLoaded_TogglesStatusBit8()
    {
        StationRuntime station = StationFixtures.CreateStation(loaded: false);
        var machine = new ConveyorStationMachine(station, randomSeed: 1);

        // 无货：X8 = 1（现场协议 X8 StaLoad：0 有货、1 无货）
        Assert.Equal(1, (StationFixtures.ReadRawU16(station, StationFixtures.StatusByteOffset) >> 8) & 1);

        machine.SetLoaded(true);

        // 有货：X8 = 0
        Assert.Equal(0, (StationFixtures.ReadRawU16(station, StationFixtures.StatusByteOffset) >> 8) & 1);
    }

    [Fact]
    public void StatusBits_DefaultStation_ShowsNoCargoWithAuto()
    {
        // 默认：无货（X8=1）+ 自动（X9=1）+ 无故障（X10=0）→ 状态字 0x0300
        StationRuntime station = StationFixtures.CreateStation();

        Assert.Equal(0x0300, StationFixtures.ReadRawU16(station, StationFixtures.StatusByteOffset));
    }

    [Fact]
    public void StatusBits_FaultBit_IsSetOnlyWhenFaulted()
    {
        StationRuntime station = StationFixtures.CreateStation(loaded: true, auto: false);
        var machine = new ConveyorStationMachine(station, randomSeed: 1);

        // X10 Fault：1 = 有故障。正常时不置位。
        Assert.Equal(0, (StationFixtures.ReadRawU16(station, StationFixtures.StatusByteOffset) >> 10) & 1);

        // 配置指定故障后，状态字应把该位置起来。
        station.Simulation.Initial.Fault = true;
        machine.SetLoaded(false);

        Assert.Equal(1, (StationFixtures.ReadRawU16(station, StationFixtures.StatusByteOffset) >> 10) & 1);
    }

    [Fact]
    public void StatusBits_AutoBit_FollowsAutoFlag()
    {
        StationRuntime auto = StationFixtures.CreateStation(loaded: true, auto: true);
        StationRuntime manual = StationFixtures.CreateStation(loaded: true, auto: false);

        // X9 AUTO：1 = 自动，0 = 手动
        Assert.Equal(1, (StationFixtures.ReadRawU16(auto, StationFixtures.StatusByteOffset) >> 9) & 1);
        Assert.Equal(0, (StationFixtures.ReadRawU16(manual, StationFixtures.StatusByteOffset) >> 9) & 1);
    }

    [Fact]
    public void Snapshot_ReflectsCurrentState()
    {
        StationRuntime station = StationFixtures.CreateStation(actionDelayMs: 1000);
        var machine = new ConveyorStationMachine(station, randomSeed: 1);

        StationFixtures.WcsWriteU16(station, 0, 1234);
        machine.Tick(Tick);

        StationSnapshot snapshot = machine.Snapshot();

        Assert.Equal("1004", snapshot.StationNo);
        Assert.Equal(StationState.Executing, snapshot.State);
        Assert.Equal(1234, snapshot.TaskNum);
        Assert.Equal("动作中", snapshot.StateText);
    }

    [Fact]
    public void Jitter_WithFixedSeed_ProducesDeterministicDelay()
    {
        StationRuntime first = StationFixtures.CreateStation(actionDelayMs: 1000, jitterMs: 200);
        StationRuntime second = StationFixtures.CreateStation(actionDelayMs: 1000, jitterMs: 200);

        var machineA = new ConveyorStationMachine(first, randomSeed: 7);
        var machineB = new ConveyorStationMachine(second, randomSeed: 7);

        StationFixtures.WcsWriteU16(first, 0, 1234);
        StationFixtures.WcsWriteU16(second, 0, 1234);
        machineA.Tick(Tick);
        machineB.Tick(Tick);

        Advance(machineA, 1400);
        Advance(machineB, 1400);

        Assert.Equal(machineA.State, machineB.State);
    }
}
