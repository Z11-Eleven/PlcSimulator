using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Stations;

namespace PlcSimulator.Devices.Tests;

/// <summary>
/// 逐站推进：货物按拓扑一站一站地向目标前进，中间站台都会被真实占用，
/// 从而让下游堵塞传导到上游。
/// 线路：1001(x=30) → 1002(x=29) → 1003(x=28) → 1004(x=27)，全部 arrowdirection = 3（向左）。
/// </summary>
public class HopByHopTransferTests
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
            Id = "hop-test",
            Name = "逐站推进测试线",
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

    /// <summary>推进到条件成立为止；每跳约 600 ms，按固定时刻断言容易撞在中间态上。</summary>
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

    private static void WcsWriteU16(StationRuntime station, int blockByteOffset, ushort value)
        => StationFixtures.WcsWriteU16(station, blockByteOffset, value);

    /// <summary>状态字 X8：0 = 有货。</summary>
    private static bool HasCargo(StationRuntime station)
        => ((StationFixtures.ReadRawU16(station, 28) >> 8) & 1) == 0;

    /// <summary>WCS 给起始站台下发一个到指定目标站台的任务。</summary>
    private static void SendTask(Line line, string from, ushort to, ushort taskNum)
    {
        line.Machine(from).SetLoaded(true);       // 货物从有货站台出发

        StationRuntime station = line.Station(from);
        WcsWriteU16(station, 0, taskNum);         // tasknum  @ 字节偏移 0
        WcsWriteU16(station, 6, to);              // to       @ 字节偏移 6
        station.WriteOutgoingAscii("barcode", "PLT20260923001");
    }

    [Fact]
    public void HopByHop_CargoWalksThroughEachIntermediateStation()
    {
        Line line = CreateLine();
        SendTask(line, from: "1001", to: 1004, taskNum: 1001);

        // 第 1 跳：1001 → 1002
        AdvanceUntilHasCargo(line, "1002");
        Assert.Equal(0, line.Station("1001").ReadIncomingU16("tasknum"));   // 源站已清空
        Assert.Equal(0, line.Station("1001").ReadIncomingU16("to"));

        // 第 2 跳：1002 → 1003
        AdvanceUntilHasCargo(line, "1003");
        Assert.Equal(0, line.Station("1002").ReadIncomingU16("tasknum"));

        // 第 3 跳：1003 → 1004（终点）
        AdvanceUntilHasCargo(line, "1004");
        Assert.Equal(1001, line.Station("1004").ReadIncomingU16("tasknum"));
        Assert.Equal(1004, line.Station("1004").ReadIncomingU16("to"));
        Assert.Equal(1003, line.Station("1004").ReadIncomingU16("from"));

        // 货已到终点，停住不再继续走
        Advance(line.Engine, 1500);
        Assert.Equal(StationState.Loaded, line.Machine("1004").State);
        Assert.True(HasCargo(line.Station("1004")));

        // 界面上的任务号列取自快照，收到货时就该显示，而不是等 WCS 再写一次
        Assert.Equal(1001, line.Machine("1004").Snapshot().TaskNum);
    }

    [Fact]
    public void HopByHop_BarcodeIsNotFlippedByEachStation()
    {
        // 条码在数据区里是以「交换态」存放的（WCS 写它的时候交换过一次），
        // 中转必须以同口径取出再写回。曾经读侧少做一次交换，
        // 结果条码每经过一个站台就高低位翻转一次。
        Line line = CreateLine();
        SendTask(line, from: "1001", to: 1004, taskNum: 1001);

        AdvanceUntilHasCargo(line, "1004");

        Assert.Equal("PLT20260923001", line.Station("1004").ReadOutgoingAscii("barcode"));
    }

    /// <summary>
    /// 一条带提升机的测试线：1270(y=13) → 提升机（10451@y=19 与 10452@y=12，站台号前缀相同、
    /// 共用一段寄存器）→ 1273(y=11)。上游站台的下一站正好是提升机的**第二层**（10452 那一层）。
    /// </summary>
    private static Line CreateLifterLine()
    {
        var config = new DeviceConfig
        {
            Id = "lifter-route-test",
            Name = "提升机两层测试线",
            Ip = "127.0.0.1",
            ProtocolType = NtiProtocol.ProtocolName,
            Protocol = "Modbus",
            ReadByteOrderPolicy = "NTI-ConveyorRead",
            SingleFieldWriteByteOrderPolicy = "SingleFieldWrite",
            Blocks = [new RegisterBlockConfig { Group = string.Empty, BaseByteOffset = 0, LengthBytes = 256 }],
            Stations =
            [
                // 上游站台，它的下一站正好是提升机的**第二层**
                new StationConfig
                {
                    StationNo = "1270", ByteOffset = 0, LengthBytes = 30,
                    LocationX = 5, LocationY = 13, Width = 1, Height = 1,
                    ArrowDirection = "1", ZoneCode = "测试区", Simulation = LifterSimulation(),
                },
                // 提升机的两层：站台号前缀相同（归组依据）、同一段寄存器（别名），坐标一上一下
                new StationConfig
                {
                    StationNo = "10451", Name = "1045-1", Remark = "提升机",
                    ByteOffset = 60, LengthBytes = 30,
                    LocationX = 5, LocationY = 19, Width = 1, Height = 1,
                    ArrowDirection = "1", ZoneCode = "测试区", Simulation = LifterSimulation(),
                },
                new StationConfig
                {
                    StationNo = "10452", Name = "1045-2", Remark = "提升机",
                    ByteOffset = 60, LengthBytes = 30,
                    LocationX = 5, LocationY = 12, Width = 1, Height = 1,
                    ArrowDirection = "1", ZoneCode = "测试区", Simulation = LifterSimulation(),
                },
                // 下游站台
                new StationConfig
                {
                    StationNo = "1273", ByteOffset = 120, LengthBytes = 30,
                    LocationX = 5, LocationY = 11, Width = 1, Height = 1,
                    ArrowDirection = "1", ZoneCode = "测试区", Simulation = LifterSimulation(),
                },
                // 1273 的下游死角：只为「先把 1273 占住」的场景有地方把它的货送出去
                new StationConfig
                {
                    StationNo = "1274", ByteOffset = 150, LengthBytes = 30,
                    LocationX = 5, LocationY = 10, Width = 1, Height = 1,
                    ArrowDirection = "1", ZoneCode = "测试区", Simulation = LifterSimulation(),
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

    private static SimulationConfig LifterSimulation() => new()
    {
        ActionDelayMs = 100,
        TransferDelayMs = 100,
        JitterMs = 0,
        ClearTimeoutMs = 0,
    };

    [Fact]
    public void HopByHop_LifterSecondLayerHoldsTheRoute_CargoStillWalks()
    {
        // 提升机的两层（10451/10452 那种）共用一段寄存器，但**坐标不同**，
        // 而拓扑里它们被归组塌缩成一个节点、出边取两层的并集。
        // 如果建图时按「物理站台」去重、把第二层丢掉，那一层的邻居就全没了：
        // 上游站台找不到下一站、节点也少了通往终点的边，货物于是查不到路径，
        // 直接按任务里的目标站台一步投送。
        Line line = CreateLifterLine();
        SendTask(line, from: "1270", to: 1273, taskNum: 1001);

        // 必须先经过提升机那一站，而不是一步到位
        AdvanceUntilHasCargo(line, "10451");
        Assert.True(HasCargo(line.Station("10451")));

        AdvanceUntilHasCargo(line, "1273");
        Assert.Equal(1001, line.Station("1273").ReadIncomingU16("tasknum"));
    }

    [Fact]
    public void HopByHop_LifterNeverHoldsPallet_BusyDownstreamKeepsItUpstream()
    {
        // 提升机的规矩：托盘不能停在盘上。前方有盘时，托盘要停在提升机的**前一个站台**，
        // 不能先送进提升机再等着。
        Line line = CreateLifterLine();

        // 先占住下游 1273：让 1273 把自己的货送到 1274，完成后停在待清零（等 WCS 清零期间一直算忙）
        line.Machine("1273").SetLoaded(true);
        WcsWriteU16(line.Station("1273"), 0, 9001);
        WcsWriteU16(line.Station("1273"), 6, 1274);
        AdvanceUntil(
            line.Engine,
            () => line.Machine("1273").State == StationState.Done,
            "1273 未进入待清零");

        SendTask(line, from: "1270", to: 1273, taskNum: 1001);
        Advance(line.Engine, 2000);

        // 托盘留在 1270 等，没有进提升机
        Assert.Equal(StationState.WaitingDownstream, line.Machine("1270").State);
        Assert.True(HasCargo(line.Station("1270")));
        Assert.False(HasCargo(line.Station("10451")));

        // 下游空出来 → 托盘经提升机送到 1273
        WcsWriteU16(line.Station("1273"), 8, 2);
        AdvanceUntilHasCargo(line, "1273");
        Assert.Equal(1001, line.Station("1273").ReadIncomingU16("tasknum"));
    }

    [Fact]
    public void HopByHop_OnlyTheStationGivenTaskByWcsRaisesHeartbeat()
    {
        Line line = CreateLine();
        SendTask(line, from: "1001", to: 1004, taskNum: 1001);

        // 起点站台执行的是 WCS 下发的任务，把货转发出去后置心跳
        AdvanceUntil(
            line.Engine,
            () => line.Machine("1001").State == StationState.Done,
            "起点站台未进入待清零");

        Assert.Equal(1, line.Station("1001").ReadIncomingU16("heartbeat"));

        // 中间站台只中转：转发完回到空闲，且不置心跳
        AdvanceUntilHasCargo(line, "1003");
        Assert.Equal(StationState.Idle, line.Machine("1002").State);
        Assert.Equal(0, line.Station("1002").ReadIncomingU16("heartbeat"));
    }

    [Fact]
    public void HopByHop_WhenNextStationBusy_CargoWaitsOnLine()
    {
        Line line = CreateLine();

        // 先让 1003 忙起来：把货送到 1004，完成后停在待清零（等 WCS 清零期间算忙）
        line.Machine("1003").SetLoaded(true);
        WcsWriteU16(line.Station("1003"), 0, 9001);
        WcsWriteU16(line.Station("1003"), 6, 1004);
        AdvanceUntil(
            line.Engine,
            () => line.Machine("1003").State == StationState.Done,
            "1003 未进入待清零");

        // 1001 → 1004 的货走到 1002 后，下一站 1003 还忙，托盘留在 1002 等
        SendTask(line, from: "1001", to: 1004, taskNum: 1002);
        AdvanceUntilHasCargo(line, "1002");
        Advance(line.Engine, 2000);

        Assert.Equal(StationState.WaitingDownstream, line.Machine("1002").State);
        Assert.True(HasCargo(line.Station("1002")));                           // 托盘还在 1002，没推到路上
        Assert.Equal(0, line.Station("1003").ReadIncomingU16("tasknum"));      // 1003 自己的任务已收尾，也没被覆盖

        // 1003 清零回空闲 → 堵着的货继续前进
        WcsWriteU16(line.Station("1003"), 8, 2);    // 心跳寄存器 @ 字节偏移 8
        AdvanceUntilHasCargo(line, "1003");

        Assert.Equal(1002, line.Station("1003").ReadIncomingU16("tasknum"));
    }

    [Fact]
    public void TaskNumber_ReusedAfterCargoLeaves_IsAcceptedAgain()
    {
        // WCS 会用同一个任务号下发新任务：托盘离站、本站清零回空闲之后，
        // 再写同一个号必须还能触发——站台不记「我执行过哪个号」。
        Line line = CreateLine();
        SendTask(line, from: "1001", to: 1004, taskNum: 5001);

        AdvanceUntil(
            line.Engine,
            () => line.Machine("1001").State == StationState.Done,
            "起点站未进入待清零");

        WcsWriteU16(line.Station("1001"), 8, 2);   // WCS 清零
        Advance(line.Engine, 200);

        Assert.Equal(StationState.Idle, line.Machine("1001").State);
        Assert.Equal(0, line.Station("1001").ReadIncomingU16("tasknum"));   // 任务号已随托盘离站

        SendTask(line, from: "1001", to: 1004, taskNum: 5001);   // 同一个任务号
        Advance(line.Engine, 200);

        Assert.Equal(StationState.Executing, line.Machine("1001").State);
    }

    [Fact]
    public void CargoReachingTarget_IsNotRetriggeredByItsOwnTaskNumber()
    {
        // 货到终点站停住后，随货带来的任务号不能被当成「新任务」反复执行，
        // 否则站台会反复动作、反复置心跳。
        Line line = CreateLine();
        SendTask(line, from: "1001", to: 1004, taskNum: 5001);

        AdvanceUntilHasCargo(line, "1004");
        Assert.Equal(StationState.Loaded, line.Machine("1004").State);

        Advance(line.Engine, 5000);   // 停住后再跑 5 秒

        Assert.Equal(StationState.Loaded, line.Machine("1004").State);
        Assert.Equal(0, line.Station("1004").ReadIncomingU16("heartbeat"));
        Assert.Equal(5001, line.Station("1004").ReadIncomingU16("tasknum"));   // 任务号随货留在终点站
    }

    [Fact]
    public void CargoWaitingForWcs_NewTaskNumber_IsAccepted()
    {
        // 站上有等 WCS 处置的货时，WCS 下新任务号要能接单（把托盘送走）。
        Line line = CreateLine();
        SendTask(line, from: "1001", to: 1004, taskNum: 5001);

        AdvanceUntilHasCargo(line, "1004");
        Assert.Equal(StationState.Loaded, line.Machine("1004").State);

        WcsWriteU16(line.Station("1004"), 0, 6002);   // 新任务号
        Advance(line.Engine, 200);

        Assert.Equal(StationState.Executing, line.Machine("1004").State);
    }

    [Fact]
    public void SecondPallet_TargetHoldsWaitingPallet_StopsAtPreviousStation()
    {
        // 先后两个托盘去同一个目标站。第一个送达后停在站上等 WCS（有货待命），
        // 第二个走到目标的前一站时必须停下等——「有货待命」不是「作业中」，
        // 但站上有货，送进去会把前一个托盘的任务信息覆盖掉。
        Line line = CreateLine();

        SendTask(line, from: "1001", to: 1004, taskNum: 5001);
        AdvanceUntilHasCargo(line, "1004");
        Assert.Equal(StationState.Loaded, line.Machine("1004").State);
        Assert.Equal(5001, line.Station("1004").ReadIncomingU16("tasknum"));

        // 第一个任务收尾，起点回空闲，才下得进第二个任务
        AdvanceUntil(
            line.Engine,
            () => line.Machine("1001").State == StationState.Done,
            "起点站未进入待清零");
        WcsWriteU16(line.Station("1001"), 8, 2);
        Advance(line.Engine, 200);
        Assert.Equal(StationState.Idle, line.Machine("1001").State);

        SendTask(line, from: "1001", to: 1004, taskNum: 5002);
        AdvanceUntilHasCargo(line, "1003");   // 走到 1004 的前一站
        Advance(line.Engine, 3000);           // 再等一会儿，确认它不会往前送

        // 停在 1003 等，托盘与任务信息都在
        Assert.Equal(StationState.WaitingDownstream, line.Machine("1003").State);
        Assert.True(HasCargo(line.Station("1003")));
        Assert.Equal(5002, line.Station("1003").ReadIncomingU16("tasknum"));

        // 1004 上第一个托盘的信息原样保留
        Assert.Equal(StationState.Loaded, line.Machine("1004").State);
        Assert.Equal(5001, line.Station("1004").ReadIncomingU16("tasknum"));
    }
}
