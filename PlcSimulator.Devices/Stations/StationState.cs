namespace PlcSimulator.Devices.Stations;

public enum StationState
{
    /// <summary>空闲无货，等待 WCS 分配任务。</summary>
    Idle,

    /// <summary>有货待命（由手动注入或配置初值进入）。</summary>
    Loaded,

    /// <summary>本站动作中：货物还在本站，正在被送出去。</summary>
    Executing,

    /// <summary>
    /// 等待下游：托盘仍在本站，但下一站收不下（忙或被打了手动），不能发车。
    /// 托盘不会推到一半悬在路上，等下一站空出来才真正离站。
    /// </summary>
    WaitingDownstream,

    /// <summary>
    /// 货物在途：已离开本站、尚未到达目标站台，此时两站都是无货。
    /// 途中目标站台变忙时货物会停在这个状态等待（堵塞），不会丢失。
    /// </summary>
    Transferring,

    /// <summary>货物已送达，心跳已置，等待 WCS 写清零值。</summary>
    Done,

    /// <summary>故障。</summary>
    Fault,
}

/// <summary>
/// 「忙 / 能否接收下一个托盘」的判定。状态机与路径诊断共用这一份实现——
/// 两处各写一遍状态列表迟早会漂移，而漂移的表现是诊断结论与实际运行对不上。
/// </summary>
internal static class StationStateRules
{
    /// <summary>正在作业：处于这些状态时目标站台不接收新货（「待清零」也算忙）。</summary>
    public static bool IsBusy(StationState state)
        => state is StationState.Executing
            or StationState.WaitingDownstream
            or StationState.Transferring
            or StationState.Done;

    /// <summary>能否再接收上游送来的托盘：正在作业或处于手动都不接收。</summary>
    public static bool CanAccept(StationState state, bool manual) => !IsBusy(state) && !manual;
}

/// <summary>站台状态与现场数据的只读快照，供 GUI 流程面板与站台状态图使用。</summary>
public readonly record struct StationSnapshot(
    string DeviceId,
    string StationNo,
    string StationName,
    StationState State,
    string LastEvent,
    ushort TaskNum,
    string Barcode,
    bool Loaded,
    bool Auto,
    bool Fault,
    TimeSpan TimeInState,
    long TickCount,
    bool Manual = false)
{
    /// <summary>是否正在作业（动作中 / 等下站 / 在途 / 待清零）。</summary>
    public bool IsBusy => StationStateRules.IsBusy(State);

    /// <summary>能否再接收上游送来的托盘：正在作业或处于手动都不接收。</summary>
    public bool CanAcceptCargo => StationStateRules.CanAccept(State, Manual);

    public string StateText => State switch
    {
        StationState.Idle => "空闲",
        StationState.Loaded => "有货待命",
        StationState.Executing => "动作中",
        StationState.WaitingDownstream => "等下站",
        StationState.Transferring => "在途",
        StationState.Done => "待清零",
        StationState.Fault => "故障",
        _ => State.ToString(),
    };
}
