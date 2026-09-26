using PlcSimulator.Core.Configuration;
using PlcSimulator.Devices.Stations;

namespace PlcSimulator.Devices.Topology;

/// <summary>
/// 路径诊断：把「从 A 到 B 货物会怎么走」的推导过程摊开——
/// 每跳选了哪个邻居、其它候选为什么没选、按配置要花多久、当前状态下会停在哪。
/// <para>
/// 只读：不下发任务、不碰数据区，可以随时反复查。诊断与运行共用同一张拓扑
/// （<see cref="DeviceRuntime.Topology"/>）和同一套忙/可接收判定
/// （<see cref="StationStateRules"/>），因此结论与实际跑货一致。
/// </para>
/// </summary>
public static class PathDiagnostics
{
    private const string LifterRemark = "提升机";

    /// <summary>「放开方向约束」时替换上去的值：四向皆可。</summary>
    private const string AllDirections = "1,2,3,4";

    /// <summary>
    /// 诊断从 <paramref name="from"/> 到 <paramref name="to"/> 的输送路径。
    /// </summary>
    /// <param name="snapshots">
    /// 当前运行快照（键为站台号，见 <see cref="BuildSnapshotLookup"/>）。
    /// 传 null 表示离线分析，报告里不含实时推演。
    /// </param>
    /// <param name="tickInterval">状态机的 tick 间隔，用于把耗时量化到整 tick；默认 50 ms。</param>
    public static PathDiagnosticReport Analyze(
        DeviceConfig device,
        StationTopology topology,
        string from,
        string to,
        IReadOnlyDictionary<string, StationSnapshot>? snapshots = null,
        TimeSpan? tickInterval = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(topology);

        from = (from ?? string.Empty).Trim();
        to = (to ?? string.Empty).Trim();

        Dictionary<string, StationConfig> configByNo = new(StringComparer.Ordinal);
        foreach (StationConfig station in device.Stations)
        {
            // 站台号重复时取最后一个 —— 与 StationTopology.Build 的口径一致。
            configByNo[station.StationNo] = station;
        }

        string summary = $"拓扑节点 {topology.NodeCount} 个 / 站台 {device.Stations.Count} 个";

        PathDiagnosticReport Base(
            PathDiagnosticOutcome outcome,
            List<string>? issues = null,
            IReadOnlyList<string>? path = null,
            List<HopDiagnosis>? hops = null,
            PathTimeline? timeline = null,
            LiveProjection? live = null,
            DirectionCheck? directions = null)
            => new()
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                From = from,
                To = to,
                TopologySummary = summary,
                Outcome = outcome,
                Issues = issues ?? [],
                Path = path ?? [],
                Hops = hops ?? [],
                Timeline = timeline,
                Live = live,
                Directions = directions,
            };

        var issues = new List<string>();

        WarnIfDuplicated(device, from, issues);
        WarnIfDuplicated(device, to, issues);

        // ---- 基本校验 ----
        if (!configByNo.ContainsKey(from))
        {
            issues.Add($"配置里没有站台 {from}（站台号可能写错）");
            return Base(PathDiagnosticOutcome.FromNotInConfig, issues);
        }

        if (!configByNo.ContainsKey(to))
        {
            issues.Add($"配置里没有站台 {to}（站台号可能写错）");
            return Base(PathDiagnosticOutcome.ToNotInConfig, issues);
        }

        // 起终点都在配置里了：放开箭头约束再跑一遍。同一套建图算法，只是让每个站台
        // 四向皆可——它走通而原规则走不通时，断点就是方向没配对的地方。
        DirectionCheck directions = BuildDirectionCheck(device, topology, from, to, configByNo);

        if (!InTopology(configByNo[from]))
        {
            issues.Add(NotInTopologyReason(configByNo[from]));
            return Base(PathDiagnosticOutcome.FromNotInTopology, issues, directions: directions);
        }

        if (!InTopology(configByNo[to]))
        {
            issues.Add(NotInTopologyReason(configByNo[to]));
            return Base(PathDiagnosticOutcome.ToNotInTopology, issues, directions: directions);
        }

        // ---- 同一个节点：提升机的两层是同一台设备，货物无需移动 ----
        string fromKey = topology.TopologyKeyOf(from);
        string toKey = topology.TopologyKeyOf(to);

        if (string.Equals(fromKey, toKey, StringComparison.Ordinal))
        {
            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                issues.Add($"起点与终点是同一个站台 {from}");
            }
            else
            {
                issues.Add($"{from} 与 {to} 同属节点 {fromKey}"
                    + $"（代表站台 {topology.RepresentativeOf(from)}）——提升机的两层是同一个物理设备，货物无需移动");
            }

            return Base(PathDiagnosticOutcome.SameNode, issues);
        }

        // ---- 寻路 ----
        if (!topology.TryGetPath(from, to, out IReadOnlyList<string> path))
        {
            issues.AddRange(ExplainDisconnection(topology, from, to));
            return Base(PathDiagnosticOutcome.NoPath, issues, directions: directions);
        }

        TimeSpan tick = tickInterval is { TotalMilliseconds: > 0 } t ? t : SimulationEngine.DefaultTick;
        IReadOnlyDictionary<string, int> distances = topology.DistancesTo(to);

        List<HopDiagnosis> hops = BuildHops(topology, configByNo, path, distances);
        PathTimeline timeline = BuildTimeline(configByNo, path, tick, hops);
        LiveProjection? live = snapshots is null
            ? null
            : ProjectLive(configByNo, path, snapshots);

        return Base(PathDiagnosticOutcome.Found, issues, path, hops, timeline, live, directions);
    }

    /// <summary>
    /// 把引擎给出的物理站台快照，按设备里的**每个**站台号铺成查表——
    /// 路径里出现的可能是共用寄存器别名站台号，直接按站台号查会漏。
    /// </summary>
    public static Dictionary<string, StationSnapshot> BuildSnapshotLookup(
        DeviceRuntime device,
        IEnumerable<StationSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(snapshots);

        Dictionary<string, StationSnapshot> byStationNo = new(StringComparer.Ordinal);
        foreach (StationSnapshot snapshot in snapshots)
        {
            byStationNo[snapshot.StationNo] = snapshot;
        }

        Dictionary<string, StationSnapshot> lookup = new(StringComparer.Ordinal);
        foreach (StationRuntime station in device.Stations)
        {
            if (byStationNo.TryGetValue(station.PrimaryStationNo, out StationSnapshot snapshot))
            {
                lookup[station.StationNo] = snapshot;
            }
        }

        return lookup;
    }

    // ---- 逐跳决策 ----

    private static List<HopDiagnosis> BuildHops(
        StationTopology topology,
        Dictionary<string, StationConfig> configByNo,
        IReadOnlyList<string> path,
        IReadOnlyDictionary<string, int> distancesToTarget)
    {
        List<HopDiagnosis> hops = [];

        for (int i = 0; i < path.Count - 1; i++)
        {
            string fromNo = path[i];
            string toNo = path[i + 1];
            StationConfig fromConfig = configByNo[fromNo];
            StationConfig toConfig = configByNo[toNo];
            string toKey = topology.TopologyKeyOf(toNo);

            int? currentHops = TryDistance(topology, distancesToTarget, fromNo);
            List<CandidateNote> candidates = [];
            string direction = "?";

            foreach (NeighborVerdict verdict in topology.EvaluateNeighbors(fromNo))
            {
                bool isTaken = string.Equals(verdict.NodeKey, toKey, StringComparison.Ordinal);
                int? hopsToTarget = verdict.Outcome == NeighborOutcome.Selected
                    ? TryDistance(topology, distancesToTarget, verdict.StationNo)
                    : null;

                if (isTaken)
                {
                    direction = verdict.Direction;
                }

                candidates.Add(new CandidateNote(
                    verdict.StationNo,
                    verdict.NodeKey,
                    verdict.Direction,
                    verdict.Outcome,
                    RouteNote(verdict.Outcome, isTaken, hopsToTarget, currentHops),
                    isTaken));
            }

            hops.Add(new HopDiagnosis(
                Index: i + 1,
                FromStationNo: fromNo,
                FromLabel: Label(fromConfig),
                ToStationNo: toNo,
                ToLabel: Label(toConfig),
                Direction: direction,
                ActionDelayMs: fromConfig.Simulation.ActionDelayMs,
                JitterMs: fromConfig.Simulation.JitterMs,
                TransferDelayMs: fromConfig.Simulation.TransferDelayMs,
                ToIsLifter: IsLifter(toConfig),
                Candidates: candidates));
        }

        return hops;
    }

    /// <summary>候选邻居走它到目标还要几跳；到不了（或不是出边）返回 null。</summary>
    private static int? TryDistance(
        StationTopology topology,
        IReadOnlyDictionary<string, int> distancesToTarget,
        string stationNo)
    {
        string representative = topology.RepresentativeOf(stationNo);
        return distancesToTarget.TryGetValue(representative, out int hops) ? hops : null;
    }

    private static string? RouteNote(
        NeighborOutcome outcome, bool isTaken, int? hopsToTarget, int? currentHops)
    {
        if (outcome != NeighborOutcome.Selected)
        {
            return null;
        }

        if (isTaken)
        {
            return "本跳走它";
        }

        if (hopsToTarget is not int hops)
        {
            return "走它到不了目标站台";
        }

        if (currentHops is int current)
        {
            int delta = hops - (current - 1);
            return delta switch
            {
                < 0 => $"走它到目标还有 {hops} 跳",
                0 => $"走它到目标还有 {hops} 跳（与本跳等长，BFS 取先入队的）",
                _ => $"走它到目标还有 {hops} 跳（比本跳远 {delta} 跳）",
            };
        }

        return $"走它到目标还有 {hops} 跳";
    }

    // ---- 时间轴 ----

    private static PathTimeline BuildTimeline(
        Dictionary<string, StationConfig> configByNo,
        IReadOnlyList<string> path,
        TimeSpan tick,
        List<HopDiagnosis> hops)
    {
        long nominal = 0;
        long fastest = 0;
        long slowest = 0;
        bool hasJitter = false;

        for (int i = 0; i < hops.Count; i++)
        {
            SimulationConfig simulation = configByNo[path[i]].Simulation;

            int transfer = Quantize(simulation.TransferDelayMs, tick);
            int action = Quantize(simulation.ActionDelayMs, tick);
            int fast = Quantize(Math.Max(0, simulation.ActionDelayMs - simulation.JitterMs), tick);
            int slow = Quantize(simulation.ActionDelayMs + simulation.JitterMs, tick);

            hasJitter |= simulation.JitterMs > 0;

            nominal += action + transfer;
            fastest += fast + transfer;
            slowest += slow + transfer;

            // 每跳的到达时刻写回诊断，报告里直接显示。
            hops[i] = hops[i] with
            {
                DepartAt = TimeSpan.FromMilliseconds(nominal - action - transfer),
                ArriveAt = TimeSpan.FromMilliseconds(nominal),
            };
        }

        return new PathTimeline(
            tick,
            TimeSpan.FromMilliseconds(nominal),
            TimeSpan.FromMilliseconds(fastest),
            TimeSpan.FromMilliseconds(slowest),
            hasJitter);
    }

    /// <summary>
    /// 把毫秒量化到 tick 的整数倍。状态机每 tick 累加一个 delta、判定是「停留时间 ≥ 延时」，
    /// 所以除不尽的部分要等到下一 tick 才满足 —— 等价于向上取整。
    /// </summary>
    private static int Quantize(int milliseconds, TimeSpan tick)
    {
        double tickMs = tick.TotalMilliseconds;
        if (tickMs <= 0)
        {
            return Math.Max(0, milliseconds);
        }

        return (int)(Math.Ceiling(Math.Max(0, milliseconds) / tickMs) * tickMs);
    }

    // ---- 实时推演 ----

    private static LiveProjection ProjectLive(
        Dictionary<string, StationConfig> configByNo,
        IReadOnlyList<string> path,
        IReadOnlyDictionary<string, StationSnapshot> snapshots)
    {
        List<StationLiveNote> stations = [];
        List<string> notes = [];

        foreach (string stationNo in path)
        {
            StationConfig config = configByNo[stationNo];
            string label = Label(config);

            if (snapshots.TryGetValue(stationNo, out StationSnapshot snapshot))
            {
                stations.Add(new StationLiveNote(
                    stationNo,
                    label,
                    snapshot.StateText,
                    snapshot.Manual,
                    !snapshot.CanAcceptCargo,
                    snapshot.LastEvent));

                if (snapshot.Fault)
                {
                    // 故障只把本站的推进冻住，判定上不算占用 —— 托盘照样会被送进去。
                    notes.Add($"{label} 处于故障：模拟器的故障只冻结本站推进，"
                        + "不会阻止托盘送入（判定上不算收不下）");
                }
            }
            else
            {
                stations.Add(new StationLiveNote(stationNo, label, "无实时数据", false, false, string.Empty));
            }
        }

        string LabelOf(string stationNo) => stations
            .First(s => string.Equals(s.StationNo, stationNo, StringComparison.Ordinal)).Label;

        // 起点手动：本站的任务号根本不会被受理。
        if (snapshots.TryGetValue(path[0], out StationSnapshot start) && start.Manual)
        {
            return new LiveProjection(
                stations,
                notes,
                path[0],
                LabelOf(path[0]),
                "起点站台处于手动，下发到本站的任务号不会被受理，托盘不会出发");
        }

        if (snapshots.TryGetValue(path[0], out StationSnapshot startBusy) && startBusy.IsBusy)
        {
            notes.Add($"{LabelOf(path[0])} 当前忙（{startBusy.StateText}），"
                + "任务号要等它空闲后才会被受理，之后照常往下走");
        }

        for (int i = 0; i < path.Count - 1; i++)
        {
            string next = path[i + 1];

            if (!snapshots.TryGetValue(next, out StationSnapshot snapshot))
            {
                continue;   // 没有实时数据，按可接收继续推
            }

            if (snapshot.Manual)
            {
                return StopAt(i, $"下游站台 {LabelOf(next)} 处于手动，托盘留在本站等待");
            }

            if (!snapshot.CanAcceptCargo)
            {
                return StopAt(i, $"下游站台 {LabelOf(next)} 收不下（{snapshot.StateText}），托盘留在本站等待");
            }

            // 提升机不能停盘：发车前要多看一站，下一站收不下就留在提升机的前一站。
            if (IsLifter(configByNo[next]) && i + 2 < path.Count)
            {
                string beyond = path[i + 2];
                if (snapshots.TryGetValue(beyond, out StationSnapshot beyondSnapshot)
                    && !beyondSnapshot.CanAcceptCargo)
                {
                    string why = beyondSnapshot.Manual ? "处于手动" : $"忙（{beyondSnapshot.StateText}）";
                    return StopAt(
                        i,
                        $"{LabelOf(next)} 是提升机、托盘不能停在盘上，"
                        + $"而它的下一站 {LabelOf(beyond)} {why}，托盘留在本站等待");
                }
            }
        }

        return new LiveProjection(
            stations,
            notes,
            path[^1],
            LabelOf(path[^1]),
            "沿途都能接收，托盘会一路走到终点站台，停下等 WCS 处理");

        LiveProjection StopAt(int index, string reason)
            => new(stations, notes, path[index], LabelOf(path[index]), reason);
    }

    // ---- 方向对照 ----

    /// <summary>
    /// 放开箭头约束重跑一遍：克隆一份配置、把每个站台的 arrowdirection 设成四向皆可，
    /// 再交给**同一个** <see cref="StationTopology.Build"/>。
    /// 判定逻辑只有一份，不会出现「诊断的图与跑货的图不一致」。
    /// </summary>
    private static DirectionCheck BuildDirectionCheck(
        DeviceConfig device,
        StationTopology topology,
        string from,
        string to,
        Dictionary<string, StationConfig> configByNo)
    {
        List<StationConfig> relaxed = [];

        foreach (StationConfig station in device.Stations)
        {
            StationConfig copy = station.Clone();
            copy.ArrowDirection = AllDirections;
            relaxed.Add(copy);
        }

        StationTopology anyDirection = StationTopology.Build(relaxed);

        // 找「需要补方向的边最少」的那条路线：原规则下能走的边记 0、要补方向的边记 1。
        // 若直接取最短跳数，可能挑出一条与原路径毫无关系的近路，对比就失去意义了。
        bool found = anyDirection.TryGetPathByCost(
            from,
            to,
            (a, b) => PassesOriginal(topology, a, b) ? 0 : 1,
            out IReadOnlyList<string> path,
            out _);

        if (!found)
        {
            IReadOnlyList<string> reachable = anyDirection.ReachableFrom(from);

            string note = reachable.Count <= 1
                ? $"{from} 在坐标上不与任何站台相邻——检查坐标与宽高是否配好、zonecode 是否与周围站台一致"
                : $"从 {from} 出发能到达 {reachable.Count - 1} 个站台，但都到不了 {to}——"
                    + "检查两端是否属于同一 zonecode、中途站台的坐标是否配齐";

            return new DirectionCheck([], [], note);
        }

        List<HopCheck> hops = [];

        for (int i = 0; i < path.Count - 1; i++)
        {
            string a = path[i];
            string b = path[i + 1];
            bool passes = PassesOriginal(topology, a, b);

            hops.Add(new HopCheck(
                a,
                Label(a, configByNo),
                b,
                Label(b, configByNo),
                passes,
                passes ? null : BreakHint(topology, configByNo, a, b)));
        }

        return new DirectionCheck(path, hops, null);
    }

    /// <summary>原规则下 <paramref name="a"/> 能否直达 <paramref name="b"/>。</summary>
    private static bool PassesOriginal(StationTopology topology, string a, string b)
        => topology.TryGetNextHop(a, b, out string next)
            && string.Equals(next, topology.RepresentativeOf(b), StringComparison.Ordinal);

    /// <summary>断在这两站之间时，给出需要补的方向值，以及「方向可能写反」这类体检结论。</summary>
    private static string BreakHint(
        StationTopology topology,
        Dictionary<string, StationConfig> configByNo,
        string from,
        string to)
    {
        string current = configByNo.TryGetValue(from, out StationConfig? config)
            ? config.ArrowDirection
            : string.Empty;

        IReadOnlyList<string> needed = topology.DirectionsFromTo(from, to);

        if (needed.Count == 0)
        {
            return $"{from} 与 {to} 不在同一 zonecode、或坐标根本不相邻——补方向救不了，检查这两项";
        }

        string where = string.Join("、", needed.Select(static d => DirectionNames.Of(d)));
        string values = string.Join("、", needed.Select(static d => $"\"{d}\""));

        string hint = $"{to} 位于 {from} 的{where}方：{from} 的 arrowdirection 需要包含 {values}"
            + $"（当前为 \"{current}\"）";

        string? suspect = SuspectWrongDirection(topology, from, current);

        return suspect is null ? hint : $"{hint}\n{suspect}";
    }

    /// <summary>
    /// 方向体检：本站**配了却没有邻居**的那个方向，多半是写反或写错了。
    /// 只有「有邻居但没配」的方向确实存在时才提示，免得对孤点站台误报。
    /// </summary>
    private static string? SuspectWrongDirection(StationTopology topology, string from, string current)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return null;   // 空配置属于「漏配」，前一句已经说清楚要补什么
        }

        IReadOnlyList<string> withNeighbors = topology.DirectionsWithNeighbors(from);

        if (withNeighbors.Count == 0)
        {
            return null;   // 周边没有邻居，那是孤点问题，不是方向问题
        }

        string[] configured =
        [
            .. current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        ];

        string[] wasted = [.. configured.Where(d => !withNeighbors.Contains(d, StringComparer.Ordinal))];
        string[] missing = [.. withNeighbors.Where(d => !configured.Contains(d, StringComparer.Ordinal))];

        if (wasted.Length == 0 || missing.Length == 0)
        {
            return null;
        }

        string wastedText = string.Join("、", wasted.Select(static d => $"\"{d}\"（{DirectionNames.Of(d)}）"));
        string missingText = string.Join("、", missing.Select(static d => $"\"{d}\"（{DirectionNames.Of(d)}）"));

        return $"{from} 配的 {wastedText} 方向没有相邻站台，"
            + $"而 {missingText} 方向有邻居却没配 —— 方向值可能写反或写错了";
    }

    private static string Label(string stationNo, Dictionary<string, StationConfig> configByNo)
        => configByNo.TryGetValue(stationNo, out StationConfig? config)
            ? StationLabel.Of(config.StationNo, config.Name, config.Remark)
            : stationNo;

    // ---- 失败原因 ----

    private static IEnumerable<string> ExplainDisconnection(
        StationTopology topology, string from, string to)
    {
        List<string> issues = [];

        IReadOnlyList<string> reachable = topology.ReachableFrom(from);
        IReadOnlyDictionary<string, int> toTarget = topology.DistancesTo(to);

        issues.Add($"从 {from} 出发能到达 {Math.Max(0, reachable.Count - 1)} 个站台，但没有一条通向 {to}");

        IReadOnlyList<string> exits = topology.NeighborsOf(from);

        if (exits.Count == 0)
        {
            issues.Add($"{from} 没有任何出边（arrowdirection 为空，或该方向上没有相邻站台），是死路");
        }
        else
        {
            issues.Add($"{from} 的出边：{string.Join("、", exits)}，走这些方向都到不了 {to}");
        }

        if (toTarget.Count <= 1)
        {
            issues.Add($"{to} 没有任何上游站台能通向它，"
                + "检查它周围的站台是否配了指向它的 arrowdirection");
        }
        else
        {
            List<string> deadEnds = [.. reachable.Where(s => topology.NeighborsOf(s).Count == 0)];

            if (deadEnds.Count > 0)
            {
                string shown = string.Join("、", deadEnds.Take(8));
                string more = deadEnds.Count > 8 ? $" 等 {deadEnds.Count} 个" : string.Empty;
                issues.Add($"走到头也没有下一站的站台：{shown}{more}");
            }
        }

        return issues;
    }

    private static bool InTopology(StationConfig station)
        => !string.Equals(station.ArrowDirection, "0", StringComparison.Ordinal)
            && !string.Equals(station.Field5, "1", StringComparison.Ordinal);

    private static string NotInTopologyReason(StationConfig station)
    {
        List<string> reasons = [];

        if (string.Equals(station.ArrowDirection, "0", StringComparison.Ordinal))
        {
            reasons.Add("arrowdirection=0");
        }

        if (string.Equals(station.Field5, "1", StringComparison.Ordinal))
        {
            reasons.Add("field5=1");
        }

        return $"站台 {station.StationNo} 不参与输送拓扑（{string.Join("、", reasons)}）";
    }

    private static void WarnIfDuplicated(DeviceConfig device, string stationNo, List<string> issues)
    {
        int count = device.Stations.Count(s => string.Equals(s.StationNo, stationNo, StringComparison.Ordinal));

        if (count > 1)
        {
            issues.Add($"站台号 {stationNo} 在配置里出现 {count} 次（重复录入），只有最后一个生效");
        }
    }

    private static bool IsLifter(StationConfig station)
        => string.Equals(station.Remark, LifterRemark, StringComparison.Ordinal);

    private static string Label(StationConfig station)
        => StationLabel.Of(station.StationNo, station.Name, station.Remark);
}
