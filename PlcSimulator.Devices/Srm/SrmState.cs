using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Devices.Srm;

/// <summary>
/// 单个货叉的作业状态。括号里是对应的作业状态字（状态区偏移 6）。
/// </summary>
public enum SrmForkState
{
    /// <summary>空闲（0）。</summary>
    Idle,

    /// <summary>取货定位中（113）。</summary>
    PickLocating,

    /// <summary>取货完成（121），等 WCS 下发放货指令。</summary>
    PickDone,

    /// <summary>放货定位中（129）。</summary>
    PutLocating,

    /// <summary>放货完成（137），等 WCS 下发清除指令。</summary>
    PutDone,

    /// <summary>移动/避让中（17）。</summary>
    MoveLocating,

    /// <summary>移动完成（25）。</summary>
    MoveDone,

    /// <summary>盘点/探货中（48）。</summary>
    StockLocating,

    /// <summary>探货完成（57）。</summary>
    StockDone,
}

public static class SrmForkStateExtensions
{
    /// <summary>货叉状态对应的作业状态字。</summary>
    public static byte ToFunctionReport(this SrmForkState state) => state switch
    {
        SrmForkState.Idle => SrmFunctionReport.Idle,
        SrmForkState.PickLocating => SrmFunctionReport.GetPosRunning,
        SrmForkState.PickDone => SrmFunctionReport.GetDone,
        SrmForkState.PutLocating => SrmFunctionReport.PutPosRunning,
        SrmForkState.PutDone => SrmFunctionReport.PutDone,
        SrmForkState.MoveLocating => SrmFunctionReport.PosGetRunning,
        SrmForkState.MoveDone => SrmFunctionReport.PosGetDone,
        SrmForkState.StockLocating => SrmFunctionReport.StockRunning,
        SrmForkState.StockDone => SrmFunctionReport.StockDone,
        _ => SrmFunctionReport.Idle,
    };

    /// <summary>
    /// 是否正在动作：这段时间里货叉被占用，新的任务类指令会被忽略。
    /// 「完成待处置」（PickDone 等）不算在动作中——它正等着 WCS 的下一步指令。
    /// </summary>
    public static bool IsInMotion(this SrmForkState state)
        => state is SrmForkState.PickLocating
            or SrmForkState.PutLocating
            or SrmForkState.MoveLocating
            or SrmForkState.StockLocating;

    public static string ToChineseText(this SrmForkState state) => state switch
    {
        SrmForkState.Idle => "空闲",
        SrmForkState.PickLocating => "取货定位中",
        SrmForkState.PickDone => "取货完成",
        SrmForkState.PutLocating => "放货定位中",
        SrmForkState.PutDone => "放货完成",
        SrmForkState.MoveLocating => "移动中",
        SrmForkState.MoveDone => "移动完成",
        SrmForkState.StockLocating => "盘点中",
        SrmForkState.StockDone => "盘点完成",
        _ => state.ToString(),
    };
}

/// <summary>堆垛机的一个位置。</summary>
public readonly record struct SrmPoint(
    byte ActionPoint,
    byte Aisle,
    byte Row,
    byte Column,
    byte Cell,
    byte Level,
    byte Depth)
{
    public static SrmPoint FromConfig(SrmPointConfig config) => new(
        config.ActionPoint,
        config.Aisle,
        config.Row,
        config.Column,
        config.Cell,
        config.Level,
        config.Depth);

    /// <summary>从指令里取目标位置。</summary>
    public static SrmPoint FromCommand(in SrmCommand command) => new(
        command.ActionPoint,
        command.Aisle,
        command.Row,
        command.Column,
        command.Cell,
        command.Level,
        command.Depth);

    public string ToChineseText()
        => $"动作点{ActionPoint} 排{Row} 列{Column} 层{Level} 深{Depth}";
}

/// <summary>一个货叉的对外快照。</summary>
public readonly record struct SrmForkSnapshot(
    byte ForkNo,
    SrmForkState State,
    ushort TaskNum,
    bool Loaded)
{
    public string StateText => State.ToChineseText() + (Loaded ? "·载货" : string.Empty);
}

/// <summary>整台堆垛机的对外快照，供界面与日志展示。</summary>
public readonly record struct SrmSnapshot(
    string DeviceId,
    byte FunctionReport,
    byte FunctionMode,
    bool Fault,
    SrmPoint Position,
    IReadOnlyList<SrmForkSnapshot> Forks,
    string LastEvent);
