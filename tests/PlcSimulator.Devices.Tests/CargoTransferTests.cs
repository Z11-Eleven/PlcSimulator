using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Stations;

namespace PlcSimulator.Devices.Tests;

/// <summary>
/// 货物流转：本站动作完成后货物离场（两站都无货），送达目标站台后
/// 任务号与条码随货过去；目标站台忙时货物在线上等待；
/// 目标不在仿真范围内时托盘留在原地等 WCS 把目标改对。
/// <para>三个站台 1001 / 1002 / 1003 都没配坐标，因此不走寻路拓扑、按任务目标一步投送。</para>
/// </summary>
public class CargoTransferTests
{
    private const int StationLength = 30;

    private sealed record Line(SimulationEngine Engine, DeviceRuntime Device)
    {
        public ConveyorStationMachine A => Engine.Find("1001")!;

        public ConveyorStationMachine B => Engine.Find("1002")!;

        public ConveyorStationMachine C => Engine.Find("1003")!;
    }

    private static Line CreateLine(int actionDelayMs = 1000, int transferDelayMs = 500)
    {
        var config = new DeviceConfig
        {
            Id = "line-test",
            Name = "测试输送线",
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
                    StationNo = "1001",
                    ByteOffset = 0,
                    LengthBytes = StationLength,
                    Simulation = new SimulationConfig
                    {
                        ActionDelayMs = actionDelayMs,
                        TransferDelayMs = transferDelayMs,
                        JitterMs = 0,
                        ClearTimeoutMs = 0,
                    },
                },
                new StationConfig
                {
                    StationNo = "1002",
                    ByteOffset = StationLength,
                    LengthBytes = StationLength,
                    Simulation = new SimulationConfig
                    {
                        ActionDelayMs = actionDelayMs,
                        TransferDelayMs = transferDelayMs,
                        JitterMs = 0,
                        ClearTimeoutMs = 0,
                    },
                },
                new StationConfig
                {
                    StationNo = "1003",
                    ByteOffset = 2 * StationLength,
                    LengthBytes = StationLength,
                    Simulation = new SimulationConfig
                    {
                        ActionDelayMs = actionDelayMs,
                        TransferDelayMs = transferDelayMs,
                        JitterMs = 0,
                        ClearTimeoutMs = 0,
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

    /// <summary>模拟 WCS 按 PLC 约定写一个 16 位字段。</summary>
    private static void WcsWriteU16(StationRuntime station, int blockOffset, ushort value)
        => StationFixtures.WcsWriteU16(station, blockOffset, value);

    private static ushort ReadStatus(StationRuntime station)
        => StationFixtures.ReadRawU16(station, 28);

    private static bool IsNoCargo(StationRuntime station)
        => ((ReadStatus(station) >> 8) & 1) == 1;

    /// <summary>让 A 站台有货并向 B 下发任务。</summary>
    private static void SendTaskAToB(Line line, ushort taskNum = 1001, string barcode = "PLT20260922001")
    {
        line.A.SetLoaded(true);   // 货物从有货站台出发

        StationRuntime a = line.A.Station;
        WcsWriteU16(a, 0, taskNum);          // tasknum
        WcsWriteU16(a, 6, 1002);             // to = 目标站台 1002
        a.WriteOutgoingAscii("barcode", barcode);
    }

    [Fact]
    public void Transfer_CargoLeavesSourceAndArrivesAtTarget()
    {
        Line line = CreateLine(actionDelayMs: 1000, transferDelayMs: 500);
        SendTaskAToB(line);

        // 本站动作中
        Advance(line.Engine, 200);
        Assert.Equal(StationState.Executing, line.A.State);
        Assert.False(IsNoCargo(line.A.Station));

        // 动作完成 → 货物离场，进入在途
        Advance(line.Engine, 1200);
        Assert.Equal(StationState.Transferring, line.A.State);
        Assert.True(IsNoCargo(line.A.Station));

        // 在途期间：两站都无货（中间态）
        Assert.True(IsNoCargo(line.B.Station));

        // 送达
        Advance(line.Engine, 600);
        Assert.Equal(StationState.Done, line.A.State);
        Assert.Equal(StationState.Loaded, line.B.State);
        Assert.False(IsNoCargo(line.B.Station));
    }

    [Fact]
    public void Transfer_WholeTaskInfoMovesWithCargo()
    {
        Line line = CreateLine(actionDelayMs: 1000, transferDelayMs: 500);
        SendTaskAToB(line, taskNum: 2233, barcode: "PLT20260922099");

        Advance(line.Engine, 2000);

        Assert.Equal(StationState.Loaded, line.B.State);
        Assert.Equal(2233, line.B.Station.ReadIncomingU16("tasknum"));
        Assert.Equal("PLT20260922099", line.B.Station.ReadOutgoingAscii("barcode"));
        Assert.Equal(1001, line.B.Station.ReadIncomingU16("from"));
        Assert.Equal(1002, line.B.Station.ReadIncomingU16("to"));   // 目标站台随货过去
    }

    [Fact]
    public void Transfer_SourceIsFullyClearedAfterCargoLeaves()
    {
        Line line = CreateLine(actionDelayMs: 1000, transferDelayMs: 500);
        SendTaskAToB(line);

        Advance(line.Engine, 2000);

        // 托盘真正离站后，整份任务信息随货搬走，源站台不留残余
        Assert.Equal(0, line.A.Station.ReadIncomingU16("tasknum"));
        Assert.Equal(0, line.A.Station.ReadIncomingU16("to"));
        Assert.Equal(0, line.A.Station.ReadIncomingU16("goodstype"));
        Assert.Equal(0, line.A.Station.ReadIncomingU16("from"));
        Assert.Equal(string.Empty, line.A.Station.ReadOutgoingAscii("barcode"));

        // WCS 读到的也是干净的
        Assert.Equal(0, line.A.Station.ReadOutgoingU16("tasknum"));
        Assert.Equal(0, line.A.Station.ReadOutgoingU16("to"));
        Assert.Equal(string.Empty, line.A.Station.ReadOutgoingAscii("barcode"));
    }

    [Fact]
    public void Transfer_WhenTargetBusy_CargoWaitsUntilTargetFree()
    {
        Line line = CreateLine(actionDelayMs: 300, transferDelayMs: 300);

        // 先让 B 忙起来：B 把自己的货送到 1003，完成后停在待清零（等 WCS 清零期间一直算忙）
        line.B.SetLoaded(true);
        WcsWriteU16(line.B.Station, 0, 9001);
        WcsWriteU16(line.B.Station, 6, 1003);
        Advance(line.Engine, 800);
        Assert.Equal(StationState.Done, line.B.State);
        Assert.True(line.B.IsBusy);

        // A 送货 → 目标忙：托盘留在 A 等下一站空出来，不推到一半悬在路上
        SendTaskAToB(line);
        Advance(line.Engine, 1500);

        Assert.Equal(StationState.WaitingDownstream, line.A.State);
        Assert.False(IsNoCargo(line.A.Station));                                // 托盘还在 A 上
        Assert.Equal(1001, line.A.Station.ReadIncomingU16("tasknum"));          // 任务信息原样留着
        Assert.Equal(1002, line.A.Station.ReadIncomingU16("to"));
        Assert.Equal(StationState.Done, line.B.State);                          // B 的作业未被打扰
        Assert.Equal(0, line.B.Station.ReadIncomingU16("tasknum"));             // 它的货已送走、字段清空，也没被覆盖

        // B 清零回空闲后，A 立刻发车并送达
        WcsWriteU16(line.B.Station, 8, 2);
        Advance(line.Engine, 500);

        Assert.Equal(StationState.Done, line.A.State);
        Assert.Equal(StationState.Loaded, line.B.State);
        Assert.Equal(1001, line.B.Station.ReadIncomingU16("tasknum"));
    }

    [Fact]
    public void Transfer_WithoutTargetStation_CargoStaysPut()
    {
        Line line = CreateLine(actionDelayMs: 300, transferDelayMs: 300);

        // 只写任务号、不写目标站台（to 保持 0）
        line.A.SetLoaded(true);
        WcsWriteU16(line.A.Station, 0, 1001);
        Advance(line.Engine, 600);

        // 目标不在仿真范围 → 托盘原地不动，任务信息原样保留，也不置心跳
        Assert.Equal(StationState.Loaded, line.A.State);
        Assert.False(IsNoCargo(line.A.Station));
        Assert.Equal(1001, line.A.Station.ReadIncomingU16("tasknum"));
        Assert.Equal(0, line.A.Station.ReadIncomingU16("heartbeat"));
        Assert.Equal(StationState.Idle, line.B.State);
    }

    [Fact]
    public void Transfer_ToUnknownStation_CargoStaysPut()
    {
        Line line = CreateLine(actionDelayMs: 300, transferDelayMs: 300);

        line.A.SetLoaded(true);
        WcsWriteU16(line.A.Station, 0, 1001);
        WcsWriteU16(line.A.Station, 6, 9999);   // 不在仿真范围内的站台
        Advance(line.Engine, 600);

        // 托盘留在原地，连那个坏目标都一个字节不动 —— 等 WCS 改对
        Assert.Equal(StationState.Loaded, line.A.State);
        Assert.False(IsNoCargo(line.A.Station));
        Assert.Equal(1001, line.A.Station.ReadIncomingU16("tasknum"));
        Assert.Equal(9999, line.A.Station.ReadIncomingU16("to"));
        Assert.Equal(0, line.A.Station.ReadIncomingU16("heartbeat"));
        Assert.Equal(StationState.Idle, line.B.State);
    }

    [Fact]
    public void Transfer_TargetCorrectedLater_CargoResumes()
    {
        Line line = CreateLine(actionDelayMs: 300, transferDelayMs: 300);

        line.A.SetLoaded(true);
        WcsWriteU16(line.A.Station, 0, 1001);
        WcsWriteU16(line.A.Station, 6, 9999);
        Advance(line.Engine, 600);
        Assert.Equal(StationState.Loaded, line.A.State);

        // WCS 把目标改对：不用重下任务号，站台自己发现 to 变了并重新执行
        WcsWriteU16(line.A.Station, 6, 1002);
        Advance(line.Engine, 1200);

        Assert.Equal(StationState.Done, line.A.State);
        Assert.Equal(StationState.Loaded, line.B.State);
        Assert.Equal(1001, line.B.Station.ReadIncomingU16("tasknum"));
    }

    [Theory]
    [InlineData(StationState.Executing, 50)]
    [InlineData(StationState.WaitingDownstream, 350)]
    [InlineData(StationState.Transferring, 350)]
    [InlineData(StationState.Done, 700)]
    public void SetLoaded_MovingOrAwaitingClear_CancelsLocalCargo(StationState state, int elapsedMs)
    {
        Line line = CreateLine(actionDelayMs: 300, transferDelayMs: 300);
        if (state == StationState.WaitingDownstream)
        {
            line.B.SetManual(true);
        }

        SendTaskAToB(line);
        WcsWriteU16(line.A.Station, 2, 7);
        WcsWriteU16(line.A.Station, 4, 1003);
        Advance(line.Engine, elapsedMs);
        Assert.Equal(state, line.A.State);

        // 手动冻结中的任务同样可以清空，清空后仍保持手动。
        line.A.SetManual(true);
        line.Engine.Enqueue(() => line.A.SetLoaded(false));

        Assert.Equal(StationState.Idle, line.A.State);
        Assert.True(line.A.IsManual);
        Assert.True(IsNoCargo(line.A.Station));
        Assert.Equal(0, line.A.Snapshot().TaskNum);
        foreach (string field in new[] { "tasknum", "goodstype", "from", "to", "heartbeat" })
        {
            Assert.Equal(0, line.A.Station.ReadIncomingU16(field));
        }
        Assert.Equal(string.Empty, line.A.Station.ReadOutgoingAscii("barcode"));

        line.A.SetManual(false);
        line.B.SetManual(false);
        Advance(line.Engine, 2000);
        Assert.Equal(StationState.Idle, line.A.State);
        Assert.Equal(0, line.A.Station.ReadIncomingU16("heartbeat"));
        if (state == StationState.Done)
        {
            // 已送达的托盘属于下游，清空源站不能把它删除。
            Assert.Equal(StationState.Loaded, line.B.State);
            Assert.Equal(1001, line.B.Station.ReadIncomingU16("tasknum"));
        }
        else
        {
            Assert.Equal(StationState.Idle, line.B.State);
            Assert.Equal(0, line.B.Station.ReadIncomingU16("tasknum"));
        }

        // 使用同一任务号重新下发，仍然可以正常搬运。
        line.B.SetLoaded(false);
        SendTaskAToB(line);
        Advance(line.Engine, 1000);
        Assert.Equal(StationState.Done, line.A.State);
        Assert.Equal(StationState.Loaded, line.B.State);
    }

    [Fact]
    public void SetLoaded_CatlCargo_ClearsBothLayoutsAndAck()
    {
        var config = new DeviceConfig
        {
            Id = "catl-clear",
            ProtocolType = CatlProtocol.ProtocolName,
            Blocks = [new RegisterBlockConfig { BaseByteOffset = 0, LengthBytes = 64 }],
            Stations = [new StationConfig { StationNo = "1001", ByteOffset = 0, LengthBytes = 40 }],
        };
        var device = new DeviceRuntime(config, CatlProtocol.Instance, ByteOrderPolicy.Identity, ByteOrderPolicy.Identity);
        var machine = new ConveyorStationMachine(device.Stations[0]);
        machine.ReceiveCargo(1234, "CATL-PALLET", 7, "1002", "1003");
        machine.Station.WriteOutgoingU8("ack", 1);
        WcsWriteU16(machine.Station, 4, 1003);
        WcsWriteU16(machine.Station, 6, 7);

        machine.SetLoaded(false);

        Assert.Equal(StationState.Idle, machine.State);
        Assert.Equal(0, machine.Station.ReadOutgoingU8("ack"));
        Assert.Equal(0, machine.Station.ReadOutgoingU8("goodstype"));
        foreach (string field in new[] { "tasknum", "from", "to" })
        {
            Assert.Equal(0, machine.Station.ReadOutgoingU16(field));
        }
        Assert.Equal(0, machine.Station.ReadIncomingU16("tasknum"));
        Assert.Equal(0, machine.Station.ReadIncomingU16("from"));
        // CATL 写布局的 to@4 与读布局的 fault@5 重叠；正常状态会把 fault 写成 1。
        // 任务号必须清零，状态同步后这个重叠值不能触发旧任务。
        Assert.Equal(1, machine.Station.ReadIncomingU16("to"));
        Assert.Equal(string.Empty, machine.Station.ReadOutgoingAscii("barcode"));
        machine.Tick(TimeSpan.FromSeconds(10));
        Assert.Equal(StationState.Idle, machine.State);
    }

    [Fact]
    public void SetLoaded_RelayedCargoInTransit_CancelsForwarding()
    {
        Line line = CreateLine(actionDelayMs: 300, transferDelayMs: 300);
        line.B.ReceiveCargo(1234, "RELAY-PALLET", 7, "1001", "1003");
        Advance(line.Engine, 350);
        Assert.Equal(StationState.Transferring, line.B.State);

        line.B.SetLoaded(false);
        Advance(line.Engine, 2000);

        Assert.Equal(StationState.Idle, line.B.State);
        Assert.Equal(StationState.Idle, line.C.State);
        Assert.Equal(0, line.C.Station.ReadIncomingU16("tasknum"));
        Assert.Equal(string.Empty, line.C.Station.ReadOutgoingAscii("barcode"));
        Assert.Equal(0, line.B.Station.ReadIncomingU16("heartbeat"));
    }

    [Fact]
    public void SetLoaded_FalseClearsCargoTaskInfo_SoItIsNotReExecuted()
    {
        // 调试台的「清空」按钮是人工介入：站台既被清干净，随货留下的任务号也该一并清掉，
        // 否则下一个 tick 的接单会把它当成新任务再执行一遍——站台反而更忙。
        Line line = CreateLine(actionDelayMs: 300, transferDelayMs: 300);

        SendTaskAToB(line);
        Advance(line.Engine, 1200);
        Assert.Equal(StationState.Loaded, line.B.State);
        Assert.Equal(1001, line.B.Station.ReadIncomingU16("tasknum"));

        line.B.SetLoaded(false);

        Assert.Equal(StationState.Idle, line.B.State);
        Assert.Equal(0, line.B.Station.ReadIncomingU16("tasknum"));
        Assert.Equal(0, line.B.Station.ReadIncomingU16("to"));

        // 站台不会因为残留的任务号自己再跑一遍、把心跳置起来
        Advance(line.Engine, 1000);
        Assert.Equal(StationState.Idle, line.B.State);
        Assert.Equal(0, line.B.Station.ReadIncomingU16("heartbeat"));
    }
}
