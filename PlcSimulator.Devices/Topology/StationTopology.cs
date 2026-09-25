using PlcSimulator.Core.Configuration;

namespace PlcSimulator.Devices.Topology;

/// <summary>
/// 站台输送拓扑：把站台按坐标与输送方向连成有向图，供货物逐站推进使用。
/// <para>
/// 规则移植自 WCS 的 <c>FindPathBLL.BuildAdjacencyTable</c>：
/// 同 <c>zonecode</c> 内、沿 <c>arrowdirection</c> 方向正好相邻的站台即为下一站；
/// 提升机（<c>remark = "提升机"</c>）按站台号前 4 位归为一组，整组塌缩成一个节点。
/// <c>arrowdirection = "0"</c> 或 <c>field5 = "1"</c> 的站台不参与拓扑。
/// </para>
/// <para>
/// 与 WCS 有一处**有意的差异**：这里用 `stationNo` 作节点键，而 WCS 用的是 `itemname`。
/// 数据库里有 33 行（全是提升机）两者不一致，导致 WCS 寻路时查不到键、退回把最终目标
/// 当作下一站——这正是「货物一步到位、不经过中间站台」的原因。
/// </para>
/// <para>
/// 尚未实现：堆垛机取货口（`stationtype = 5`）与其放货站台的连接，那部分需要
/// `equipment` 表的 `stationpoint` / `equipmentnum`，不在当前的点位导出范围内。
/// </para>
/// </summary>
public sealed class StationTopology
{
    private readonly Dictionary<string, List<string>> _adjacency;
    private readonly Dictionary<string, string> _keyOf;
    private readonly Dictionary<string, string> _representative;
    private readonly IReadOnlyList<StationConfig> _active;
    private readonly Dictionary<string, List<StationConfig>> _members;
    private readonly Dictionary<string, StationConfig> _configByStationNo;

    /// <summary>四个方向值（1 上 / 2 下 / 3 左 / 4 右），用于遍历。</summary>
    private static readonly string[] AllDirectionsList = ["1", "2", "3", "4"];

    private StationTopology(
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, string> keyOf,
        Dictionary<string, string> representative,
        List<StationConfig> active,
        Dictionary<string, List<StationConfig>> members,
        Dictionary<string, StationConfig> configByStationNo)
    {
        _adjacency = adjacency;
        _keyOf = keyOf;
        _representative = representative;
        _active = active;
        _members = members;
        _configByStationNo = configByStationNo;
    }

    /// <summary>参与拓扑的节点数（提升机整组算一个）。</summary>
    public int NodeCount => _adjacency.Count;

    /// <summary>某站台是否参与拓扑。</summary>
    public bool Contains(string stationNo) => _adjacency.ContainsKey(KeyOf(stationNo));

    public static StationTopology Build(IEnumerable<StationConfig> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);

        List<StationConfig> all = [.. stations];

        // 不参与输送拓扑的站台（WCS 在 Init 里直接跳过这两类）
        List<StationConfig> active =
        [
            .. all.Where(static s => s.ArrowDirection != "0" && s.Field5 != "1"),
        ];

        Dictionary<string, string> keyOf = new(StringComparer.Ordinal);
        Dictionary<string, string> representative = new(StringComparer.Ordinal);
        Dictionary<string, List<StationConfig>> members = new(StringComparer.Ordinal);
        Dictionary<string, StationConfig> configByStationNo = new(StringComparer.Ordinal);

        foreach (StationConfig station in active)
        {
            // 提升机按**站台号前 4 位**归组，整组塌缩成一个节点（同一台设备的两层楼）。
            // 只用站台号：itemname 允许重复、只用于显示，不能作归组依据。
            string key = station.Remark == "提升机"
                ? ToStandard(station.StationNo)
                : station.StationNo;

            // 站台号重复录入时后面的覆盖前面的 —— 与建图（下面按 keyOf 展开）同一口径。
            keyOf[station.StationNo] = key;
            configByStationNo[station.StationNo] = station;
        }

        // 按**最终**的节点键分组、并取代表站台号。必须用 keyOf 查出来的键，不能用本行现算的键：
        // 站台号重复时本行的键可能已被覆盖，而建图是按 keyOf 展开的，两边口径不一致
        // 「候选评估」就会与「出边」对不上（诊断说的与跑货做的不是一回事）。
        foreach (StationConfig station in active)
        {
            string key = keyOf[station.StationNo];

            if (!members.TryGetValue(key, out List<StationConfig>? group))
            {
                group = [];
                members[key] = group;
            }

            group.Add(station);

            // 每个节点取一个代表站台号，提升机组取站台号最小的那个
            if (!representative.TryGetValue(key, out string? existing)
                || string.CompareOrdinal(station.StationNo, existing) < 0)
            {
                representative[key] = station.StationNo;
            }
        }

        Dictionary<string, List<string>> adjacency = new(StringComparer.Ordinal);

        foreach (StationConfig station in active)
        {
            string key = keyOf[station.StationNo];

            if (!adjacency.TryGetValue(key, out List<string>? neighbors))
            {
                neighbors = [];
                adjacency[key] = neighbors;
            }

            CollectNeighbors(station, active, keyOf, key, neighbors, verdictsOut: null);
        }

        return new StationTopology(adjacency, keyOf, representative, active, members, configByStationNo);
    }

    /// <summary>
    /// 取从 <paramref name="from"/> 前往 <paramref name="to"/> 的**下一站**（真实站台号）。
    /// 无拓扑、无路径、或已在终点时返回 false，调用方应退回按目标站台直接投送。
    /// </summary>
    public bool TryGetNextHop(string from, string to, out string nextStationNo)
    {
        nextStationNo = string.Empty;

        if (!TryGetPath(from, to, out IReadOnlyList<string> path) || path.Count < 2)
        {
            return false;
        }

        nextStationNo = path[1];
        return true;
    }

    /// <summary>
    /// 取从 <paramref name="from"/> 到 <paramref name="to"/> 的完整站台序列（含两端）。
    /// 无拓扑或无路径时返回 false。用于核对货物实际会经过哪些站台。
    /// </summary>
    public bool TryGetPath(string from, string to, out IReadOnlyList<string> path)
    {
        path = [];

        string start = KeyOf(from);
        string target = KeyOf(to);

        if (start == target
            || !_adjacency.ContainsKey(start)
            || !_adjacency.ContainsKey(target))
        {
            return false;
        }

        // 广度优先找最短路径
        Dictionary<string, string> previous = new(StringComparer.Ordinal) { [start] = start };
        Queue<string> queue = new();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (current == target)
            {
                break;
            }

            foreach (string neighbor in _adjacency[current])
            {
                if (previous.ContainsKey(neighbor))
                {
                    continue;
                }

                previous[neighbor] = current;
                queue.Enqueue(neighbor);
            }
        }

        if (!previous.ContainsKey(target))
        {
            return false;
        }

        // 回溯节点键，再换算成真实站台号
        LinkedList<string> keys = new();
        for (string node = target; node != start; node = previous[node])
        {
            keys.AddFirst(node);
        }

        keys.AddFirst(start);

        path = [.. keys.Select(RepresentStationNo)];
        return true;
    }

    /// <summary>
    /// 找一条「代价最小」的路径：每条边的代价由 <paramref name="edgeCost"/> 给出（0 或 1）。
    /// 用 0-1 BFS——代价 0 的边从队首入、代价 1 的从队尾入，先探索总代价小的路线。
    /// 路径诊断用它找「需要补方向的边最少」的路线，免得把一条与原路径无关的近路当对照。
    /// </summary>
    public bool TryGetPathByCost(
        string from,
        string to,
        Func<string, string, int> edgeCost,
        out IReadOnlyList<string> path,
        out int totalCost)
    {
        ArgumentNullException.ThrowIfNull(edgeCost);

        path = [];
        totalCost = 0;

        string start = KeyOf(from);
        string target = KeyOf(to);

        if (start == target
            || !_adjacency.ContainsKey(start)
            || !_adjacency.ContainsKey(target))
        {
            return false;
        }

        Dictionary<string, string> previous = new(StringComparer.Ordinal) { [start] = start };
        Dictionary<string, int> cost = new(StringComparer.Ordinal) { [start] = 0 };

        LinkedList<string> deque = new();
        deque.AddFirst(start);

        while (deque.Count > 0)
        {
            string current = deque.First!.Value;
            deque.RemoveFirst();

            if (string.Equals(current, target, StringComparison.Ordinal))
            {
                break;
            }

            foreach (string neighbor in _adjacency[current])
            {
                int weight = edgeCost(RepresentStationNo(current), RepresentStationNo(neighbor));
                int next = cost[current] + weight;

                if (cost.TryGetValue(neighbor, out int known) && known <= next)
                {
                    continue;
                }

                cost[neighbor] = next;
                previous[neighbor] = current;

                if (weight == 0)
                {
                    deque.AddFirst(neighbor);
                }
                else
                {
                    deque.AddLast(neighbor);
                }
            }
        }

        if (!cost.ContainsKey(target))
        {
            return false;
        }

        LinkedList<string> keys = new();
        for (string node = target; node != start; node = previous[node])
        {
            keys.AddFirst(node);
        }

        keys.AddFirst(start);

        path = [.. keys.Select(RepresentStationNo)];
        totalCost = cost[target];
        return true;
    }

    /// <summary>列出某站台在图中的出边（真实站台号），用于核对拓扑。</summary>
    public IReadOnlyList<string> NeighborsOf(string stationNo)
    {
        if (!_adjacency.TryGetValue(KeyOf(stationNo), out List<string>? neighbors))
        {
            return [];
        }

        return [.. neighbors.Select(RepresentStationNo)];
    }

    /// <summary>站台的拓扑节点键：提升机是归组后的键（站台号前 4 位），其余就是站台号本身。</summary>
    public string TopologyKeyOf(string stationNo) => KeyOf(stationNo);

    /// <summary>站台所属节点的代表站台号（同组取站台号最小的那个）。</summary>
    public string RepresentativeOf(string stationNo) => RepresentStationNo(KeyOf(stationNo));

    /// <summary>
    /// 评估本站**所在节点**的全部几何邻居（沿输送方向、坐标正好相邻的候选，通常 1~3 个）及各自结局，
    /// 供路径诊断说明「为什么选它 / 为什么没选」。其余数百个站台本来就不是候选，不在此列出。
    /// <para>
    /// 按节点而非单个站台评估：提升机的两层坐标不同、各有各的邻居，而图上是同一个节点、
    /// 出边取两层的并集——只看一层会漏掉实际走的那一站。
    /// </para>
    /// <para>站台不在配置里、或未参与拓扑（arrowdirection=0 / field5=1）时返回空列表。</para>
    /// </summary>
    public IReadOnlyList<NeighborVerdict> EvaluateNeighbors(string stationNo)
    {
        string key = KeyOf(stationNo);

        if (!_members.TryGetValue(key, out List<StationConfig>? members))
        {
            return [];
        }

        List<string> selected = [];
        List<NeighborVerdict> verdicts = [];

        foreach (StationConfig member in members)
        {
            CollectNeighbors(member, _active, _keyOf, key, selected, verdicts);
        }

        // 同一候选可能被节点内的多个成员站台看到（提升机两层），只保留首次结局。
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<NeighborVerdict> unique = [];

        foreach (NeighborVerdict verdict in verdicts)
        {
            if (seen.Add(verdict.StationNo))
            {
                unique.Add(verdict);
            }
        }

        return unique;
    }

    /// <summary>
    /// 要让 <paramref name="fromStationNo"/> 能通向 <paramref name="toStationNo"/>，
    /// 前者的 arrowdirection 需要包含哪些方向值（"1" 上 / "2" 下 / "3" 左 / "4" 右）。
    /// 两者跨 zonecode、或坐标根本不相邻时返回空——那种情况补方向也救不了。
    /// </summary>
    public IReadOnlyList<string> DirectionsFromTo(string fromStationNo, string toStationNo)
    {
        if (!_configByStationNo.TryGetValue(fromStationNo, out StationConfig? from)
            || !_configByStationNo.TryGetValue(toStationNo, out StationConfig? to)
            || !string.Equals(from.ZoneCode, to.ZoneCode, StringComparison.Ordinal))
        {
            return [];
        }

        List<int> xs = AxisValues(from.LocationX, from.Width);
        List<int> ys = AxisValues(from.LocationY, from.Height);
        List<string> directions = [];

        foreach (string direction in AllDirectionsList)
        {
            if (MatchesDirection(xs, ys, to, direction))
            {
                directions.Add(direction);
            }
        }

        return directions;
    }

    /// <summary>
    /// 本站周边**哪些方向上有相邻站台**——不管自身 arrowdirection 配了什么。
    /// 与配置的方向一比，就能看出「配了的方向上没有邻居、有邻居的方向却没配」这类写反或写错。
    /// </summary>
    public IReadOnlyList<string> DirectionsWithNeighbors(string stationNo)
    {
        if (!_configByStationNo.TryGetValue(stationNo, out StationConfig? station))
        {
            return [];
        }

        string selfKey = KeyOf(stationNo);
        List<int> xs = AxisValues(station.LocationX, station.Width);
        List<int> ys = AxisValues(station.LocationY, station.Height);
        HashSet<string> directions = new(StringComparer.Ordinal);

        foreach (StationConfig candidate in _active)
        {
            if (ReferenceEquals(candidate, station)
                || !string.Equals(station.ZoneCode, candidate.ZoneCode, StringComparison.Ordinal)
                || string.Equals(_keyOf[candidate.StationNo], selfKey, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string direction in AllDirectionsList)
            {
                if (MatchesDirection(xs, ys, candidate, direction))
                {
                    directions.Add(direction);
                    break;
                }
            }
        }

        return [.. directions.OrderBy(static d => d, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 各站点到目标站台的最短跳数（从目标出发的反向 BFS）。
    /// 键是节点的**代表站台号**（任意站台号先用 <see cref="RepresentativeOf"/> 换算）；
    /// 到不了目标的站点不在结果里。用于说明「这条出边为什么没走」。
    /// </summary>
    public IReadOnlyDictionary<string, int> DistancesTo(string stationNo)
    {
        string target = KeyOf(stationNo);
        Dictionary<string, int> byStationNo = new(StringComparer.Ordinal);

        if (!_adjacency.ContainsKey(target))
        {
            return byStationNo;
        }

        Dictionary<string, List<string>> reverse = BuildReverse();
        Dictionary<string, int> distances = new(StringComparer.Ordinal) { [target] = 0 };
        Queue<string> queue = new();
        queue.Enqueue(target);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();

            if (!reverse.TryGetValue(current, out List<string>? sources))
            {
                continue;
            }

            foreach (string source in sources)
            {
                if (distances.TryAdd(source, distances[current] + 1))
                {
                    queue.Enqueue(source);
                }
            }
        }

        foreach ((string key, int hops) in distances)
        {
            byStationNo[RepresentStationNo(key)] = hops;
        }

        return byStationNo;
    }

    /// <summary>从某站台出发沿出边可达的全部站台（含自身），返回各节点的代表站台号。</summary>
    public IReadOnlyList<string> ReachableFrom(string stationNo)
    {
        string start = KeyOf(stationNo);

        if (!_adjacency.ContainsKey(start))
        {
            return [];
        }

        HashSet<string> visited = new(StringComparer.Ordinal) { start };
        Queue<string> queue = new();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();

            foreach (string neighbor in _adjacency[current])
            {
                if (visited.Add(neighbor))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        return [.. visited.Select(RepresentStationNo)];
    }

    private Dictionary<string, List<string>> BuildReverse()
    {
        Dictionary<string, List<string>> reverse = new(StringComparer.Ordinal);

        foreach ((string node, List<string> neighbors) in _adjacency)
        {
            foreach (string neighbor in neighbors)
            {
                if (!reverse.TryGetValue(neighbor, out List<string>? sources))
                {
                    sources = [];
                    reverse[neighbor] = sources;
                }

                sources.Add(node);
            }
        }

        return reverse;
    }

    private string RepresentStationNo(string key)
        => _representative.TryGetValue(key, out string? stationNo) ? stationNo : key;

    private string KeyOf(string stationNo)
        => _keyOf.TryGetValue(stationNo, out string? key) ? key : stationNo;

    /// <summary>提升机的拓扑键取**站台号**前 4 位（与 WCS 的 ToStandard 一致）。</summary>
    private static string ToStandard(string stationNo)
        => stationNo.Length >= 4 ? stationNo[..4] : stationNo;

    /// <summary>
    /// 沿本站的每个输送方向找出正好相邻的站台，把它们的**节点键**加入
    /// <paramref name="selectedOut"/>，并按需把逐候选的结局记进 <paramref name="verdictsOut"/>。
    /// <para>
    /// 建图与路径诊断共用这一个循环：判定与去重一旦分叉，诊断说的「为什么选它」
    /// 就与真实跑货对不上。建图只传 <paramref name="selectedOut"/>（不产生额外分配），
    /// 诊断两个都传。
    /// </para>
    /// </summary>
    private static void CollectNeighbors(
        StationConfig station,
        IReadOnlyList<StationConfig> candidates,
        Dictionary<string, string> keyOf,
        string selfKey,
        List<string> selectedOut,
        List<NeighborVerdict>? verdictsOut)
    {
        List<int> xs = AxisValues(station.LocationX, station.Width);
        List<int> ys = AxisValues(station.LocationY, station.Height);

        foreach (string direction in station.ArrowDirection.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (StationConfig candidate in candidates)
            {
                if (ReferenceEquals(candidate, station)
                    || !string.Equals(station.ZoneCode, candidate.ZoneCode, StringComparison.Ordinal)
                    || !MatchesDirection(xs, ys, candidate, direction))
                {
                    continue;
                }

                string candidateKey = keyOf[candidate.StationNo];

                NeighborOutcome outcome;
                if (candidateKey == selfKey)
                {
                    // 与本站归为同一节点（提升机的另一层），不构成一次移动。
                    outcome = NeighborOutcome.FoldedIntoNode;
                }
                else if (selectedOut.Contains(candidateKey))
                {
                    outcome = NeighborOutcome.DuplicateNodeKey;
                }
                else
                {
                    selectedOut.Add(candidateKey);
                    outcome = NeighborOutcome.Selected;
                }

                // 同一候选可能命中多个方向，去重交给调用方（见 EvaluateNeighbors）。
                verdictsOut?.Add(new NeighborVerdict(
                    candidate.StationNo, candidateKey, direction, outcome));
            }
        }
    }

    /// <summary>
    /// 本站沿 <paramref name="direction"/> 是否正好指向 <paramref name="candidate"/>：
    /// 候选站台的坐标范围要覆盖「本站坐标按候选宽/高偏移后的位置」，且另一个轴向有重叠。
    /// </summary>
    private static bool MatchesDirection(List<int> xs, List<int> ys, StationConfig candidate, string direction)
    {
        List<int> candidateXs = AxisValues(candidate.LocationX, candidate.Width);
        List<int> candidateYs = AxisValues(candidate.LocationY, candidate.Height);

        return direction switch
        {
            "1" => ys.Exists(a => candidateYs.Contains(a - candidate.Height))
                && xs.Exists(candidateXs.Contains),
            "2" => ys.Exists(a => candidateYs.Contains(a + candidate.Height))
                && xs.Exists(candidateXs.Contains),
            "3" => xs.Exists(a => candidateXs.Contains(a - candidate.Width))
                && ys.Exists(candidateYs.Contains),
            "4" => xs.Exists(a => candidateXs.Contains(a + candidate.Width))
                && ys.Exists(candidateYs.Contains),
            _ => false,
        };
    }

    private static List<int> AxisValues(int start, int size)
    {
        List<int> values = [start];
        for (int i = 1; i < size; i++)
        {
            values.Add(start + i);
        }

        return values;
    }
}

/// <summary>本站的一个几何邻居在拓扑里的结局。</summary>
public enum NeighborOutcome
{
    /// <summary>已加入本站的出边。</summary>
    Selected,

    /// <summary>与本站归为同一节点（提升机的另一层），不构成一次移动。</summary>
    FoldedIntoNode,

    /// <summary>该节点键已由更早的候选站台加入，出边里不会重复出现。</summary>
    DuplicateNodeKey,
}

/// <summary>一个几何邻居及其结局，供路径诊断说明「为什么选它 / 为什么没选」。</summary>
public readonly record struct NeighborVerdict(
    string StationNo,
    string NodeKey,
    string Direction,
    NeighborOutcome Outcome);
