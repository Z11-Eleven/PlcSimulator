using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Stations;

namespace PlcSimulator.Devices.Tests;

/// <summary>
/// 手动模式：被打为手动的站台冻结——上游的托盘进不来、站上的托盘也不动，
/// 用来人为制造堵塞；状态字 X9（AUTO 位）随之翻转，WCS 能读到本站已脱离自动。
/// 线路：1001(x=30) → 1002(x=29) → 1003(x=28) → 1004(x=27)，全部 arrowdirection = 3（向左）。
/// </summary>
public class ManualModeTests
{
    private const int StationLength = 30;
    private const int ActionDelayMs = 300;
    private const int TransferDelayMs = 300;

    private sealed record Line(SimulationEngine Engine, DeviceRuntime Device)
    {
        public ConveyorStationMachine Machine(string stationNo) => Engine.Find(stationNo)!;

        public StationRuntime Station(string stationNo) => Machine(stationNo).Station;
    }

    private static Line CreateLine()
    {
        string[] stationNos = ["1001", "1002", "1003", "1004"];

        var config = new DeviceConfig
        {
            Id = "manual-test",
            Name = "手动模式测试线",
            Ip = "127.0.0.1",
            ProtocolType = NtiProtocol.ProtocolName,
            Protocol = "Modbus",
            ReadByteOrderPolicy = "NTI-ConveyorRead",
            SingleFieldWriteByteOrderPolicy = "SingleFieldWrite",
            Blocks = [new RegisterBlockConfig { Group = string.Empty, BaseByteOffset = 0, LengthBytes = 256 }],
            Stations =
            [
                .. stationNos.Select((no, index) => new StationConfig
                {
                    StationNo = no,
                    ByteOffset = index * StationLength,
                    LengthBytes = StationLength,
                    LocationX = 30 - index,
                    LocationY = 21,
                    Width = 1,
                    Height = 1,
                    ArrowDirection = "3",
                    ZoneCode = "测试区",
                    Simulation = new SimulationConfig
                    {
                        ActionDelayMs = ActionDelayMs,
                        TransferDelayMs = TransferDelayMs,
                        JitterMs = 0,
                        ClearTimeoutMs = 0,
                    },
                }),
            ],
        };

        var device = new DeviceRuntime(
            config,
            NtiProtocol.Instance,
            new ByteOrderPolicy { Name = "NTI-ConveyorRead", ExtraPairUnswapRanges = [[12, 28]] },
            new ByteOrderPolicy { Name = "SingleFieldWrite" });

        StationInitializer.Apply(device);

        var engine = new SimulationEngine(TimeSpan.FromMilliseconds(50));
        engine.RegisterDevice(device, randomSeed: 1);

        return new Line(engine, device);
    }

    private static void Advance(SimulationEngine engine, int milliseconds)
    {
        for (int i = 0; i < milliseconds / 50; i++)
        {
            engine.AdvanceOneTick();
        }
    }

    private static void AdvanceUntil(SimulationEngine engine, Func<bool> condition, string message)
    {
        for (int elapsed = 0; elapsed < 20_000; elapsed += 50)
        {
            if (condition())
            {
                return;
            }

            engine.AdvanceOneTick();
        }

        Assert.Fail($"等待超时：{message}");
    }

    private static void AdvanceUntilHasCargo(Line line, string stationNo)
        => AdvanceUntil(line.Engine, () => HasCargo(line.Station(stationNo)), $"货物未到达站台 {stationNo}");

    /// <summary>状态字 X8：0 = 有货。</summary>
    private static bool HasCargo(StationRuntime station)
        => ((StationFixtures.ReadRawU16(station, 28) >> 8) & 1) == 0;

    /// <summary>状态字 X9：1 = 自动，0 = 手动。</summary>
    private static bool IsAutoBitSet(StationRuntime station)
        => ((StationFixtures.ReadRawU16(station, 28) >> 9) & 1) == 1;

    /// <summary>WCS 给起始站台下发一个到指定目标站台的任务。</summary>
    private static void SendTask(Line line, string from, ushort to, ushort taskNum)
    {
        line.Machine(from).SetLoaded(true);       // 货物从有货站台出发

        StationRuntime station = line.Station(from);
        StationFixtures.WcsWriteU16(station, 0, taskNum);   // tasknum  @ 字节偏移 0
        StationFixtures.WcsWriteU16(station, 6, to);        // to       @ 字节偏移 6
        station.WriteOutgoingAscii("barcode", "PLT20260923002");
    }

    [Fact]
    public void Manual_StationOnRoute_KeepsCargoOnUpstreamStation()
    {
        Line line = CreateLine();
        line.Machine("1002").SetManual(true);

        SendTask(line, from: "1001", to: 1004, taskNum: 1001);
        Advance(line.Engine, 2000);

        // 下一站手动收不下，托盘留在 1001 原地等，任务信息原样不动
        Assert.Equal(StationState.WaitingDownstream, line.Machine("1001").State);
        Assert.True(HasCargo(line.Station("1001")));
        Assert.Equal(1001, line.Station("1001").ReadIncomingU16("tasknum"));
        Assert.Equal(1004, line.Station("1001").ReadIncomingU16("to"));

        // 下游一个字节都没收到
        Assert.Equal(0, line.Station("1002").ReadIncomingU16("tasknum"));
        Assert.False(HasCargo(line.Station("1002")));
        Assert.Contains("手动", line.Machine("1001").LastEvent, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_BlockedFurtherDownstream_CargoStopsAtIntermediateStation()
    {
        // 1001 → 1004 要经过 1002、1003。托盘走到 1002 之后下游 1003 才被打手动，
        // 此时托盘停在 1002（它所在的位置），而不是退回起点。
        Line line = CreateLine();
        SendTask(line, from: "1001", to: 1004, taskNum: 1001);

        AdvanceUntilHasCargo(line, "1002");
        line.Machine("1003").SetManual(true);
        Advance(line.Engine, 3000);

        Assert.Equal(StationState.WaitingDownstream, line.Machine("1002").State);
        Assert.True(HasCargo(line.Station("1002")));
        Assert.Equal(1001, line.Station("1002").ReadIncomingU16("tasknum"));
        Assert.Equal(1004, line.Station("1002").ReadIncomingU16("to"));   // 目标不变，恢复后还是要往 1004 走

        // 恢复自动 → 继续经过 1003 直到终点
        line.Machine("1003").SetManual(false);
        AdvanceUntilHasCargo(line, "1004");

        Assert.Equal(1001, line.Station("1004").ReadIncomingU16("tasknum"));
    }

    [Fact]
    public void Manual_Restored_BlockedCargoContinuesToTarget()
    {
        Line line = CreateLine();
        line.Machine("1002").SetManual(true);
        SendTask(line, from: "1001", to: 1004, taskNum: 1001);
        Advance(line.Engine, 2000);
        Assert.Equal(StationState.WaitingDownstream, line.Machine("1001").State);
        Assert.True(HasCargo(line.Station("1001")));   // 托盘留在原地等着

        // 恢复自动：等了很久的托盘照常发车，并继续逐站前进到终点
        line.Machine("1002").SetManual(false);
        AdvanceUntilHasCargo(line, "1004");

        Assert.Equal(1001, line.Station("1004").ReadIncomingU16("tasknum"));
    }

    [Fact]
    public void Manual_WithCargoOnStation_FreezesThatCargo()
    {
        Line line = CreateLine();
        SendTask(line, from: "1001", to: 1004, taskNum: 1001);
        AdvanceUntil(line.Engine, () => line.Machine("1001").State == StationState.Executing, "未进入动作中");

        line.Machine("1002").SetManual(true);   // 上游动手之前先占住下一站
        line.Machine("1001").SetManual(true);

        Advance(line.Engine, 3000);

        // 本站的托盘原地留着，既不动作也不离开
        Assert.Equal(StationState.Executing, line.Machine("1001").State);
        Assert.True(HasCargo(line.Station("1001")));
        Assert.Equal(1001, line.Station("1001").ReadIncomingU16("tasknum"));
    }

    [Fact]
    public void Manual_FrozenDuration_IsNotCountedIntoActionDelay()
    {
        Line line = CreateLine();
        ConveyorStationMachine machine = line.Machine("1001");

        SendTask(line, from: "1001", to: 1004, taskNum: 1001);
        AdvanceUntil(line.Engine, () => machine.State == StationState.Executing, "未进入动作中");

        Advance(line.Engine, 100);             // 有效动作时间 100 ms，不足 300 ms
        machine.SetManual(true);
        Advance(line.Engine, 5000);            // 冻结 5 s
        Assert.Equal(StationState.Executing, machine.State);

        machine.SetManual(false);
        Advance(line.Engine, 100);             // 有效动作时间 200 ms，仍不足
        Assert.Equal(StationState.Executing, machine.State);

        Advance(line.Engine, 100);             // 有效动作时间 300 ms，够数
        Assert.NotEqual(StationState.Executing, machine.State);
    }

    [Fact]
    public void Manual_TogglesAutoBitInStatusWord()
    {
        Line line = CreateLine();
        ConveyorStationMachine machine = line.Machine("1002");

        Assert.True(IsAutoBitSet(line.Station("1002")));

        machine.SetManual(true);
        Assert.False(IsAutoBitSet(line.Station("1002")));

        machine.SetManual(false);
        Assert.True(IsAutoBitSet(line.Station("1002")));
    }

    [Fact]
    public void Manual_WcsTask_IsNotAcceptedUntilRestoredToAuto()
    {
        Line line = CreateLine();
        ConveyorStationMachine machine = line.Machine("1001");
        machine.SetManual(true);

        StationFixtures.WcsWriteU16(line.Station("1001"), 0, 1234);
        Advance(line.Engine, 1000);

        Assert.Equal(StationState.Idle, machine.State);

        machine.SetManual(false);
        AdvanceUntil(line.Engine, () => machine.State == StationState.Executing, "恢复自动后未接单");
    }

    [Fact]
    public void Manual_OnDoneStation_StillAnswersWcsClear()
    {
        Line line = CreateLine();
        ConveyorStationMachine machine = line.Machine("1001");

        // 无目标的任务：动作完成后直接把货视为离场并置心跳，停在待清零
        StationFixtures.WcsWriteU16(line.Station("1001"), 0, 9001);
        AdvanceUntil(line.Engine, () => machine.State == StationState.Done, "未进入待清零");

        machine.SetManual(true);
        Assert.Equal(1, line.Station("1001").ReadIncomingU16("heartbeat"));

        // 手动挡的是机械动作，不是通信：WCS 的清零写仍要处理
        StationFixtures.WcsWriteU16(line.Station("1001"), 8, 2);
        Advance(line.Engine, 200);

        Assert.Equal(StationState.Idle, machine.State);
        Assert.Equal(0, line.Station("1001").ReadIncomingU16("heartbeat"));
    }

    [Fact]
    public void Reset_ClearsManualBackToConfiguredInitial()
    {
        Line line = CreateLine();
        ConveyorStationMachine machine = line.Machine("1001");

        machine.SetManual(true);
        Assert.True(machine.IsManual);

        machine.Reset();

        Assert.False(machine.IsManual);
        Assert.True(IsAutoBitSet(line.Station("1001")));
    }
}
