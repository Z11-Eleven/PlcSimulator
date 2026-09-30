using System.Globalization;
using System.Text;

namespace PlcSimulator.Devices.Topology;

/// <summary>路径诊断的结论。</summary>
public enum PathDiagnosticOutcome
{
    /// <summary>找到路径。</summary>
    Found,

    /// <summary>起点站台不在配置里。</summary>
    FromNotInConfig,

    /// <summary>终点站台不在配置里。</summary>
    ToNotInConfig,

    /// <summary>起点站台不参与输送拓扑（arrowdirection=0 或 field5=1）。</summary>
    FromNotInTopology,

    /// <summary>终点站台不参与输送拓扑。</summary>
    ToNotInTopology,

    /// <summary>起终点同属一个节点（提升机的两层），不需要移动。</summary>
    SameNode,

    /// <summary>两点都在拓扑里，但走不通。</summary>
    NoPath,
}

/// <summary>一站几何邻居的诊断记录。</summary>
public sealed record CandidateNote(
    string StationNo,
    string NodeKey,
    string Direction,
    NeighborOutcome Outcome,

    /// <summary>走它到目标还要几跳之类的说明；与移动无关的结局为 null。</summary>
    string? RouteNote,

    /// <summary>本跳是否走的这条路。</summary>
    bool IsTaken);

/// <summary>一跳的完整诊断。</summary>
public sealed record HopDiagnosis(
    int Index,
    string FromStationNo,
    string FromLabel,
    string ToStationNo,
    string ToLabel,
    string Direction,
    int ActionDelayMs,
    int JitterMs,
    int TransferDelayMs,
    bool ToIsLifter,
    IReadOnlyList<CandidateNote> Candidates)
{
    /// <summary>本跳发车时刻（相对起点），由时间轴推演回填。</summary>
    public TimeSpan DepartAt { get; init; }

    /// <summary>本跳到达下一站的时刻（相对起点）。</summary>
    public TimeSpan ArriveAt { get; init; }
}

/// <summary>全程耗时推演。</summary>
public sealed record PathTimeline(
    TimeSpan Tick,
    TimeSpan Nominal,
    TimeSpan Fastest,
    TimeSpan Slowest,
    bool HasJitter);

/// <summary>沿途各站的当前运行状态。</summary>
public sealed record StationLiveNote(
    string StationNo,
    string Label,
    string StateText,
    bool Manual,

    /// <summary>收不下上游送来的托盘（站上有货 / 正在作业 / 等 WCS 清零）。</summary>
    bool NotAccepting,
    string LastEvent);

/// <summary>按当前运行状态推演托盘实际会走到哪。</summary>
public sealed record LiveProjection(
    IReadOnlyList<StationLiveNote> Stations,

    /// <summary>与停顿点无关的提醒（起点忙、故障站台等）。</summary>
    IReadOnlyList<string> Notes,
    string? StopStationNo,
    string? StopLabel,
    string? StopReason);

/// <summary>
/// 「放开箭头约束」后的对照：同一套建图算法，只把所有站台的 arrowdirection 当成四向皆可。
/// 它走通而原规则走不通时，断点就是某几个站台的方向没配对。
/// </summary>
public sealed record DirectionCheck(
    /// <summary>放开方向后的路线；走不通时为空。</summary>
    IReadOnlyList<string> Path,

    /// <summary>逐跳对照（沿放开方向后的路线走）。</summary>
    IReadOnlyList<HopCheck> Hops,

    /// <summary>放开方向后仍走不通时的说明——那说明问题不在方向上。</summary>
    string? UnreachableNote);

/// <summary>放开方向后的路线里，某一跳在原规则下能否走通。</summary>
public sealed record HopCheck(
    string FromStationNo,
    string FromLabel,
    string ToStationNo,
    string ToLabel,
    bool Passes,

    /// <summary>走不通时：需要补的方向值，或"跨 zone / 坐标不相邻"这类无解说明。</summary>
    string? Hint);

/// <summary>路径诊断的完整结果。<see cref="ToText"/> 渲染成中文报告，CLI 与调试台共用。</summary>
public sealed record PathDiagnosticReport
{
    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    public required string TopologySummary { get; init; }

    public required PathDiagnosticOutcome Outcome { get; init; }

    /// <summary>失败原因与告警，逐条一句中文。</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>完整站台序列（含两端）；失败时为空。</summary>
    public IReadOnlyList<string> Path { get; init; } = [];

    public IReadOnlyList<HopDiagnosis> Hops { get; init; } = [];

    public PathTimeline? Timeline { get; init; }

    /// <summary>方向对照：放开箭头约束后的路线，以及逐跳在原规则下通不通。</summary>
    public DirectionCheck? Directions { get; init; }

    /// <summary>实时推演；诊断时没给快照则为 null。</summary>
    public LiveProjection? Live { get; init; }

    public string ToText()
    {
        var text = new StringBuilder();

        text.AppendLine($"路径诊断 · 设备 {DeviceId}（{DeviceName}）");
        text.AppendLine(TopologySummary);
        text.AppendLine();

        AppendConclusion(text);
        AppendTimeline(text);
        AppendHops(text);
        AppendLive(text);
        AppendDirections(text);

        string? hint = Outcome switch
        {
            PathDiagnosticOutcome.Found => null,
            PathDiagnosticOutcome.SameNode =>
                "提示：起终点是同一个站台、或同一个物理设备（提升机的两层），运行时货物不会移动。",
            PathDiagnosticOutcome.FromNotInConfig or PathDiagnosticOutcome.ToNotInConfig =>
                "提示：站台不在仿真范围内时，托盘会留在原地不动、任务信息不丢，等 WCS 把目标改对。",
            _ =>
                "提示：运行时找不到路径会退回按任务里的目标站台直接投送（一步到位），"
                + "不经过中间站台。",
        };

        if (hint is not null)
        {
            text.AppendLine();
            text.AppendLine(hint);
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    private void AppendConclusion(StringBuilder text)
    {
        text.Append("结论  ");
        text.AppendLine(Outcome switch
        {
            PathDiagnosticOutcome.Found => $"找到路径，共 {Path.Count - 1} 跳",
            PathDiagnosticOutcome.FromNotInConfig => $"起点站台 {From} 不在配置里",
            PathDiagnosticOutcome.ToNotInConfig => $"终点站台 {To} 不在配置里",
            PathDiagnosticOutcome.FromNotInTopology => $"起点站台 {From} 不参与输送拓扑",
            PathDiagnosticOutcome.ToNotInTopology => $"终点站台 {To} 不参与输送拓扑",
            PathDiagnosticOutcome.SameNode => $"{From} 与 {To} 同属一个拓扑节点，不需要移动",
            PathDiagnosticOutcome.NoPath => $"从 {From} 到 {To} 走不通",
            _ => Outcome.ToString(),
        });

        if (Path.Count > 0)
        {
            text.Append("路线  ");
            text.AppendLine(string.Join(" → ", Path));
        }

        if (Issues.Count > 0)
        {
            text.AppendLine("原因");
            foreach (string issue in Issues)
            {
                text.AppendLine($"  · {issue}");
            }
        }
    }

    private void AppendTimeline(StringBuilder text)
    {
        if (Timeline is not { } timeline)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine($"耗时  名义 {Seconds(timeline.Nominal)} s"
            + $"（按 {timeline.Tick.TotalMilliseconds:F0} ms tick 量化）");

        if (timeline.HasJitter)
        {
            text.AppendLine($"      最快 {Seconds(timeline.Fastest)} s ／ 最慢 {Seconds(timeline.Slowest)} s"
                + "（动作段含随机抖动，在途段固定）");
        }
    }

    private void AppendHops(StringBuilder text)
    {
        foreach (HopDiagnosis hop in Hops)
        {
            text.AppendLine();
            text.AppendLine($"第 {hop.Index} 跳  {hop.FromLabel} → {hop.ToLabel}");

            string jitter = hop.JitterMs > 0 ? $"(±{hop.JitterMs})" : string.Empty;
            text.AppendLine($"  {DirectionText(hop.Direction)} · 动作 {hop.ActionDelayMs} ms{jitter}"
                + $" + 在途 {hop.TransferDelayMs} ms · 发车 t≈{Seconds(hop.DepartAt)} s"
                + $" → 到达 t≈{Seconds(hop.ArriveAt)} s");

            if (hop.ToIsLifter)
            {
                text.AppendLine("  目标站台是提升机：发车前会先确认它的下一站收得下，"
                    + "收不下就留在本站等（托盘不能在提升机上停留）");
            }

            if (hop.Candidates.Count > 0)
            {
                text.AppendLine("  候选邻居：");
                foreach (CandidateNote note in hop.Candidates)
                {
                    text.AppendLine($"    {CandidateLine(note)}");
                }
            }
            else
            {
                text.AppendLine("  候选邻居：无（本站没有任何方向相邻的站台）");
            }
        }
    }

    private static string CandidateLine(CandidateNote note)
    {
        string mark = note.IsTaken ? "✓" : "  ";

        string outcome = note.Outcome switch
        {
            NeighborOutcome.Selected => "选中，成为出边",
            NeighborOutcome.FoldedIntoNode => $"与本站同属节点 {note.NodeKey}（提升机的另一层），不算移动",
            NeighborOutcome.DuplicateNodeKey => $"节点 {note.NodeKey} 已由更早的候选加入",
            _ => note.Outcome.ToString(),
        };

        string route = note.RouteNote is null ? string.Empty : $" · {note.RouteNote}";

        return $"{mark} {note.StationNo}  {DirectionText(note.Direction)} · {outcome}{route}";
    }

    private void AppendLive(StringBuilder text)
    {
        text.AppendLine();

        if (Live is not { } live)
        {
            text.AppendLine("实时状态  未提供（离线分析）");
            return;
        }

        text.AppendLine("实时状态  取自当前运行快照");
        foreach (StationLiveNote station in live.Stations)
        {
            var flags = new List<string>();
            if (station.Manual)
            {
                flags.Add("手动");
            }

            if (station.NotAccepting)
            {
                flags.Add("收不下");
            }

            string suffix = flags.Count > 0 ? $"  ← {string.Join("、", flags)}" : string.Empty;
            text.AppendLine($"  {station.Label}  {station.StateText}{suffix}");
            if (suffix.Length > 0 && !string.IsNullOrWhiteSpace(station.LastEvent))
            {
                text.AppendLine($"      {station.LastEvent}");
            }
        }

        foreach (string note in live.Notes)
        {
            text.AppendLine($"  提醒：{note}");
        }

        text.AppendLine(live.StopStationNo is null
            ? "  推演：路径为空，没有可推演的移动。"
            : $"  推演：托盘会停在 {live.StopLabel} —— {live.StopReason}");
    }

    /// <summary>
    /// 方向对照：同一张图、只把 arrowdirection 放开成四向皆可，看能不能走通。
    /// 这条路线里原规则走不通的那一跳，就是要改方向的地方。
    /// </summary>
    private void AppendDirections(StringBuilder text)
    {
        if (Directions is not { } check)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("方向对照  同一张图、把 arrowdirection 放开成四向皆可");

        if (check.Path.Count == 0)
        {
            text.AppendLine($"  放开方向后仍然走不通：{check.UnreachableNote}");
            return;
        }

        text.AppendLine($"  放开方向后的路线：{string.Join(" → ", check.Path)}");

        int breaks = check.Hops.Count(static h => !h.Passes);

        if (breaks == 0)
        {
            text.AppendLine("  逐跳对照：每一跳在原规则下都能走通（方向配置没有问题）");
            return;
        }

        text.AppendLine($"  这条路线要补 {breaks} 处方向（按断点最少选取，不是跳数最少）");
        text.AppendLine("  逐跳对照：");
        bool firstBreakShown = false;

        foreach (HopCheck hop in check.Hops)
        {
            if (hop.Passes)
            {
                text.AppendLine($"    ✓ {hop.FromStationNo} → {hop.ToStationNo}");
                continue;
            }

            string mark = firstBreakShown ? string.Empty : "   ← 第一处断裂";
            firstBreakShown = true;
            text.AppendLine($"    ✗ {hop.FromStationNo} → {hop.ToStationNo}{mark}");

            if (hop.Hint is not null)
            {
                foreach (string line in hop.Hint.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    text.AppendLine($"        {line.Trim()}");
                }
            }
        }
    }

    private static string DirectionText(string direction) => direction switch
    {
        "1" => "向上",
        "2" => "向下",
        "3" => "向左",
        "4" => "向右",
        _ => direction,
    };

    private static string Seconds(TimeSpan value)
        => (value.TotalMilliseconds / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>把站台号、名称与备注拼成一行可读的标签。</summary>
internal static class StationLabel
{
    public static string Of(string stationNo, string name, string remark)
    {
        List<string> extras = [];

        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, stationNo, StringComparison.Ordinal))
        {
            extras.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(remark))
        {
            extras.Add(remark);
        }

        return extras.Count == 0 ? stationNo : $"{stationNo}（{string.Join("，", extras)}）";
    }
}

/// <summary>方向值的中文名（"1" 上 / "2" 下 / "3" 左 / "4" 右）。</summary>
internal static class DirectionNames
{
    public static string Of(string direction) => direction switch
    {
        "1" => "上",
        "2" => "下",
        "3" => "左",
        "4" => "右",
        _ => direction,
    };
}
