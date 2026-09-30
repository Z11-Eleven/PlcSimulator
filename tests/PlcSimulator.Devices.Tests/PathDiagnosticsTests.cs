using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Devices.Topology;

namespace PlcSimulator.Devices.Tests;

/// <summary>
/// 路径诊断：把「从 A 到 B 货物会怎么走」的推导过程摊开。
/// 诊断与运行共用同一张拓扑（<see cref="DeviceRuntime.Topology"/>）与同一套忙判定，
/// 所以这里的断言同时也是对拓扑与状态机语义的固化。
/// </summary>
public class PathDiagnosticsTests
{
    private const int StationLength = 30;

    private static StationConfig Station(
        string no,
        int x,
        int y,
        string arrow,
        string? name = null,
        string remark = "",
        int actionMs = 300,
        int transferMs = 300,
        int jitterMs = 0)
        => new()
        {
            StationNo = no,
            Name = name ?? no,
            Remark = remark,
            LengthBytes = StationLength,
            LocationX = x,
            LocationY = y,
            Width = 1,
            Height = 1,
            ArrowDirection = arrow,
            ZoneCode = "测试区",
            Simulation = new SimulationConfig
            {
                ActionDelayMs = actionMs,
                TransferDelayMs = transferMs,
                JitterMs = jitterMs,
                ClearTimeoutMs = 0,
            },
        };

    private static DeviceRuntime BuildDevice(params StationConfig[] stations)
    {
        for (int i = 0; i < stations.Length; i++)
        {
            stations[i].ByteOffset = i * StationLength;
        }

        var config = new DeviceConfig
        {
            Id = "path-test",
            Name = "路径诊断测试线",
            Ip = "127.0.0.1",
            ProtocolType = NtiProtocol.ProtocolName,
            Protocol = "Modbus",
            ReadByteOrderPolicy = "NTI-ConveyorRead",
            SingleFieldWriteByteOrderPolicy = "SingleFieldWrite",
            Blocks = [new RegisterBlockConfig { Group = string.Empty, BaseByteOffset = 0, LengthBytes = 1024 }],
            Stations = [.. stations],
        };

        var device = new DeviceRuntime(
            config,
            NtiProtocol.Instance,
            new ByteOrderPolicy { Name = "NTI-ConveyorRead", ExtraPairUnswapRanges = [[12, 28]] },
            new ByteOrderPolicy { Name = "SingleFieldWrite" });

        StationInitializer.Apply(device);
        return device;
    }

    /// <summary>
    /// 1001(30,21) → 1002(29,21) → 1003(28,21)，而 1002 还配了向上的出边 1009(29,20)。
    /// 1009 没有下游，用来验证「其它出边为什么没走」。
    /// </summary>
    private static DeviceRuntime BuildBranchLine()
        => BuildDevice(
            Station("1001", 30, 21, "3"),
            Station("1002", 29, 21, "1,3"),
            Station("1003", 28, 21, "3"),
            Station("1009", 29, 20, "1"));

    /// <summary>提升机两层：站台号前 4 位相同即归为同一节点，1270 的下一站正好是第二层 10452。</summary>
    private static DeviceRuntime BuildLifterLine()
        => BuildDevice(
            Station("1270", 5, 13, "1"),
            Station("10451", 5, 19, "1", remark: "提升机"),
            Station("10452", 5, 12, "1", remark: "提升机"),
            Station("1273", 5, 11, "1"));

    private static PathDiagnosticReport Analyze(DeviceRuntime device, string from, string to)
        => PathDiagnostics.Analyze(device.Config, device.Topology, from, to);

    private static PathDiagnosticReport AnalyzeLive(
        DeviceRuntime device, SimulationEngine engine, string from, string to)
        => PathDiagnostics.Analyze(
            device.Config,
            device.Topology,
            from,
            to,
            PathDiagnostics.BuildSnapshotLookup(device, engine.Snapshots()),
            engine.TickInterval);

    [Fact]
    public void EvaluateNeighbors_Selected_EqualsNeighborsOf()
    {
        // 决策明细与实际建图共用同一个判定循环，选中集合必须与出边逐站相等，
        // 否则诊断会说出与跑货不同的结论。
        DeviceRuntime device = BuildBranchLine();

        foreach (StationConfig station in device.Config.Stations)
        {
            string[] selected =
            [
                .. device.Topology.EvaluateNeighbors(station.StationNo)
                    .Where(static v => v.Outcome == NeighborOutcome.Selected)
                    .Select(static v => v.StationNo)
                    .OrderBy(static s => s, StringComparer.Ordinal),
            ];

            string[] exits =
            [
                .. device.Topology.NeighborsOf(station.StationNo)
                    .OrderBy(static s => s, StringComparer.Ordinal),
            ];

            Assert.Equal(exits, selected);
        }
    }

    [Fact]
    public void EvaluateNeighbors_LifterLayers_MergesBothLayersIntoOneNode()
    {
        // 同一台提升机的两层（站台号前缀一致）外加一行重复录入的 10581：
        // 归组只看站台号前 4 位，三行的邻居都进同一个节点——候选评估必须与建图同口径，
        // 否则诊断显示的候选邻居与实际出边对不上。
        DeviceRuntime device = BuildDevice(
            Station("10581", 13, 19, "1", remark: "提升机"),
            Station("10581", 12, 19, "1", remark: "提升机"),
            Station("10582", 12, 62, "2", remark: "提升机"),
            Station("1054", 13, 18, "2"),
            Station("1059", 12, 18, "2"),
            Station("1288", 12, 63, "1"));

        StationTopology topology = device.Topology;

        string[] selected =
        [
            .. topology.EvaluateNeighbors("10581")
                .Where(static v => v.Outcome == NeighborOutcome.Selected)
                .Select(static v => v.StationNo)
                .OrderBy(static s => s, StringComparer.Ordinal),
        ];

        string[] exits =
        [
            .. topology.NeighborsOf("10581").OrderBy(static s => s, StringComparer.Ordinal),
        ];

        Assert.Equal(["1054", "1059", "1288"], selected);
        Assert.Equal(exits, selected);
    }

    [Fact]
    public void Analyze_Branch_ReportsTakenAndRejectedCandidates()
    {
        DeviceRuntime device = BuildBranchLine();

        PathDiagnosticReport report = Analyze(device, "1001", "1003");

        Assert.Equal(PathDiagnosticOutcome.Found, report.Outcome);
        Assert.Equal(["1001", "1002", "1003"], report.Path);

        // 第 2 跳在 1002 分岔：向左走 1003，向上那条 1009 是死路
        HopDiagnosis hop = report.Hops[1];
        Assert.Equal("1002", hop.FromStationNo);
        Assert.Equal("3", hop.Direction);

        CandidateNote taken = hop.Candidates.Single(static c => c.StationNo == "1003");
        Assert.True(taken.IsTaken);
        Assert.Contains("本跳走它", taken.RouteNote!, StringComparison.Ordinal);

        CandidateNote rejected = hop.Candidates.Single(static c => c.StationNo == "1009");
        Assert.False(rejected.IsTaken);
        Assert.Contains("到不了目标站台", rejected.RouteNote!, StringComparison.Ordinal);

        Assert.Contains("第 2 跳", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_Timeline_QuantizesToTick()
    {
        // 动作与在途都不是整 tick 的倍数：320 ms 要等满下一 tick，等价于向上取整到 350 ms。
        DeviceRuntime device = BuildDevice(
            Station("1001", 30, 21, "3", actionMs: 320, transferMs: 320),
            Station("1002", 29, 21, "3"));

        PathDiagnosticReport report = Analyze(device, "1001", "1002");

        Assert.NotNull(report.Timeline);
        Assert.Equal(700, report.Timeline!.Nominal.TotalMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(700), report.Hops[0].ArriveAt);
        Assert.False(report.Timeline.HasJitter);
    }

    [Fact]
    public void Analyze_Timeline_WithJitter_ReportsFastestAndSlowest()
    {
        DeviceRuntime device = BuildDevice(
            Station("1001", 30, 21, "3", actionMs: 300, transferMs: 300, jitterMs: 100),
            Station("1002", 29, 21, "3"));

        PathDiagnosticReport report = Analyze(device, "1001", "1002");
        PathTimeline timeline = report.Timeline!;

        Assert.True(timeline.HasJitter);
        Assert.Equal(600, timeline.Nominal.TotalMilliseconds);
        Assert.Equal(500, timeline.Fastest.TotalMilliseconds);   // 动作 300-100 + 在途 300
        Assert.Equal(700, timeline.Slowest.TotalMilliseconds);   // 动作 300+100 + 在途 300
    }

    [Fact]
    public void Analyze_NotConnected_NamesBreakStation()
    {
        // 1010 孤零零放在远处：既到不了它，它也没有下游。
        DeviceRuntime device = BuildDevice(
            Station("1001", 30, 21, "3"),
            Station("1002", 29, 21, "3"),
            Station("1010", 5, 5, "3"));

        PathDiagnosticReport report = Analyze(device, "1001", "1010");

        Assert.Equal(PathDiagnosticOutcome.NoPath, report.Outcome);
        Assert.Contains(report.Issues, static i => i.Contains("1010", StringComparison.Ordinal));
        Assert.Contains(report.Issues, static i => i.Contains("1001 的出边", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_DuplicateStationNo_Warns()
    {
        // 现场数据里 10022 / 10531 这样的重复行：只有最后一个生效，报告要提醒。
        DeviceRuntime device = BuildDevice(
            Station("1001", 30, 21, "3"),
            Station("1002", 29, 21, "3"),
            Station("1002", 29, 21, "3"));

        PathDiagnosticReport report = Analyze(device, "1001", "1002");

        Assert.Contains(report.Issues, static i => i.Contains("出现 2 次", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_OutsideTopology_ReportsReason()
    {
        DeviceRuntime device = BuildDevice(
            Station("1001", 30, 21, "3"),
            Station("1002", 29, 21, "0"));

        PathDiagnosticReport report = Analyze(device, "1001", "1002");

        Assert.Equal(PathDiagnosticOutcome.ToNotInTopology, report.Outcome);
        Assert.Contains(report.Issues, static i => i.Contains("arrowdirection=0", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_LifterLayers_ReportsSameNode()
    {
        DeviceRuntime device = BuildLifterLine();

        PathDiagnosticReport report = Analyze(device, "10451", "10452");

        Assert.Equal(PathDiagnosticOutcome.SameNode, report.Outcome);
        Assert.Contains(report.Issues, static i => i.Contains("同属节点 1045", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_BrokenDirection_NamesTheHopAndNeededDirection()
    {
        // 1001 → 1002 → 1003 的直线，但 1002 的方向配错了（本该向左，却配成向右）。
        // 原规则走不通，放开方向能走通——报告要指出断在 1002 → 1003、并给出该补的方向。
        DeviceRuntime device = BuildDevice(
            Station("1001", 30, 21, "3"),
            Station("1002", 29, 21, "4"),
            Station("1003", 28, 21, "3"));

        PathDiagnosticReport report = Analyze(device, "1001", "1003");

        Assert.Equal(PathDiagnosticOutcome.NoPath, report.Outcome);

        DirectionCheck check = report.Directions!;
        Assert.Equal(["1001", "1002", "1003"], check.Path);

        HopCheck broken = check.Hops.Single(static h => !h.Passes);
        Assert.Equal("1002", broken.FromStationNo);
        Assert.Equal("1003", broken.ToStationNo);
        Assert.Contains("\"3\"", broken.Hint!, StringComparison.Ordinal);
        Assert.Contains("当前为 \"4\"", broken.Hint!, StringComparison.Ordinal);
        Assert.Contains("第一处断裂", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_WrongDirectionValue_SuspectsItIsMiswritten()
    {
        // 1002 配了 "1"（上），但上方没有相邻站台；左方有 1003 却没配 "3"。
        // 断点提示里应点出这是「方向值可能写反或写错」。
        DeviceRuntime device = BuildDevice(
            Station("1001", 30, 21, "3"),
            Station("1002", 29, 21, "1"),
            Station("1003", 28, 21, "3"));

        PathDiagnosticReport report = Analyze(device, "1001", "1003");

        Assert.Equal(PathDiagnosticOutcome.NoPath, report.Outcome);

        HopCheck broken = report.Directions!.Hops.Single(static h => !h.Passes);
        Assert.Equal("1002", broken.FromStationNo);
        Assert.Contains("可能写反", broken.Hint!, StringComparison.Ordinal);
        Assert.Contains("\"1\"（上）", broken.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_DisconnectedByZone_ReportsItIsNotADirectionProblem()
    {
        // 坐标相邻但 zonecode 不同：放开方向也连不上——那说明问题不在方向。
        StationConfig isolated = Station("1002", 29, 21, "3");
        isolated.ZoneCode = "另一区域";

        DeviceRuntime device = BuildDevice(Station("1001", 30, 21, "3"), isolated);

        PathDiagnosticReport report = Analyze(device, "1001", "1002");

        Assert.Equal(PathDiagnosticOutcome.NoPath, report.Outcome);

        DirectionCheck check = report.Directions!;
        Assert.Empty(check.Path);
        Assert.Contains("zonecode", check.UnreachableNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_HealthyLine_ReportsEveryHopPasses()
    {
        DeviceRuntime device = BuildBranchLine();

        PathDiagnosticReport report = Analyze(device, "1001", "1003");

        DirectionCheck check = report.Directions!;
        Assert.NotEmpty(check.Hops);
        Assert.All(check.Hops, static hop => Assert.True(hop.Passes));
    }

    [Fact]
    public void Analyze_WithoutSnapshots_MarksOffline()
    {
        DeviceRuntime device = BuildBranchLine();

        PathDiagnosticReport report = Analyze(device, "1001", "1003");

        Assert.Null(report.Live);
        Assert.Contains("离线分析", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ManualNextStation_StopsUpstream()
    {
        DeviceRuntime device = BuildBranchLine();
        var engine = new SimulationEngine(TimeSpan.FromMilliseconds(50));
        engine.RegisterDevice(device, randomSeed: 1);

        engine.Find("1002")!.SetManual(true);

        PathDiagnosticReport report = AnalyzeLive(device, engine, "1001", "1003");

        Assert.NotNull(report.Live);
        Assert.Equal("1001", report.Live!.StopStationNo);
        Assert.Contains("手动", report.Live.StopReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_BusyNextStation_StopsUpstream()
    {
        DeviceRuntime device = BuildBranchLine();
        var engine = new SimulationEngine(TimeSpan.FromMilliseconds(50));
        engine.RegisterDevice(device, randomSeed: 1);

        // 给 1002 一个任务，把它推进到「动作中」——正在作业的站台不接收新货。
        ConveyorStationMachine machine = engine.Find("1002")!;
        machine.SetLoaded(true);
        StationFixtures.WcsWriteU16(machine.Station, 0, 1001);
        engine.AdvanceOneTick();
        Assert.Equal(StationState.Executing, machine.State);

        PathDiagnosticReport report = AnalyzeLive(device, engine, "1001", "1003");

        Assert.Equal("1001", report.Live!.StopStationNo);
        Assert.Contains("收不下", report.Live.StopReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_LifterWithBlockedBeyond_StopsBeforeLifter()
    {
        // 提升机不能停盘：它的下一站收不下时，托盘留在提升机的**前一个**站台。
        DeviceRuntime device = BuildLifterLine();
        var engine = new SimulationEngine(TimeSpan.FromMilliseconds(50));
        engine.RegisterDevice(device, randomSeed: 1);

        engine.Find("1273")!.SetManual(true);

        PathDiagnosticReport report = AnalyzeLive(device, engine, "1270", "1273");

        Assert.Equal(["1270", "10451", "1273"], report.Path);
        Assert.Equal("1270", report.Live!.StopStationNo);
        Assert.Contains("提升机", report.Live.StopReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_AllClear_StopsAtDestination()
    {
        DeviceRuntime device = BuildBranchLine();
        var engine = new SimulationEngine(TimeSpan.FromMilliseconds(50));
        engine.RegisterDevice(device, randomSeed: 1);

        PathDiagnosticReport report = AnalyzeLive(device, engine, "1001", "1003");

        Assert.Equal("1003", report.Live!.StopStationNo);
        Assert.Contains("终点站台", report.Live.StopReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineBusy_FollowsTaskLifecycle()
    {
        // 忙的判定与快照同源：诊断说「目标站台忙」时，运行时确实不会把货送进去。
        DeviceRuntime device = BuildDevice(
            Station("1001", 30, 21, "3", actionMs: 100, transferMs: 100),
            Station("1002", 29, 21, "3"));

        var engine = new SimulationEngine(TimeSpan.FromMilliseconds(50));
        engine.RegisterDevice(device, randomSeed: 1);

        ConveyorStationMachine machine = engine.Find("1001")!;
        Assert.False(machine.IsBusy);
        Assert.True(machine.CanAcceptCargo);
        Assert.False(machine.Snapshot().IsBusy);

        machine.SetLoaded(true);
        StationFixtures.WcsWriteU16(machine.Station, 0, 1001);
        StationFixtures.WcsWriteU16(machine.Station, 6, 1002);   // 目标写下游，货才送得出去
        engine.AdvanceOneTick();

        Assert.Equal(StationState.Executing, machine.State);
        Assert.True(machine.IsBusy);
        Assert.True(machine.Snapshot().IsBusy);

        // 送达后停在待清零，仍算忙（等 WCS 清零期间下一个托盘进不来）
        for (int i = 0; i < 20; i++)
        {
            engine.AdvanceOneTick();
        }

        Assert.Equal(StationState.Done, machine.State);
        Assert.True(machine.Snapshot().IsBusy);

        // WCS 写清零值 → 回空闲，重新可接收
        StationFixtures.WcsWriteU16(machine.Station, 8, 2);
        engine.AdvanceOneTick();

        Assert.Equal(StationState.Idle, machine.State);
        Assert.False(machine.IsBusy);
        Assert.True(machine.CanAcceptCargo);
    }
}
