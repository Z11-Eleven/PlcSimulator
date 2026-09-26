namespace PlcSimulator.Core.Protocols;

/// <summary>
/// WCS 下发的一条指令（26 字节指令帧里的 23 字节负载）。
/// 各字段偏移见 <see cref="SrmLayout.CommandFields"/>。
/// </summary>
public readonly record struct SrmCommand(
    ushort Fork1TaskNum,
    ushort Fork2TaskNum,
    byte CommandType,
    byte ForkNo,
    byte Fork1GoodsType,
    byte Fork2GoodsType,
    byte ActionPoint,
    byte Aisle,
    byte Row,
    byte Column,
    byte Cell,
    byte Level,
    byte Depth,
    byte FireFlag,
    byte Face1,
    byte Face2)
{
    /// <summary>是否为清除指令。</summary>
    public bool IsClear => CommandType == SrmCommandType.NoFunc;

    /// <summary>是否为火警避让移动：移动指令且任务号为 1199。</summary>
    public bool IsAvoidance =>
        CommandType == SrmCommandType.PosGet
        && (Fork1TaskNum == SrmCommandType.AvoidanceTaskNum
            || Fork2TaskNum == SrmCommandType.AvoidanceTaskNum);

    /// <summary>
    /// 本条指令针对哪个货叉：1 / 2，或 3（两个货叉一起）。
    /// <para>
    /// 以指令里的「货叉号」为主判据；它为 0 时按非零的工位任务号反推
    /// （工位1 对应货叉1、工位2 对应货叉2）。两者都无法判定时返回 0，
    /// 由调用方决定是当作「两个货叉一起」还是忽略。
    /// </para>
    /// </summary>
    public byte ResolveForkNo()
    {
        if (ForkNo is 1 or 2 or 3)
        {
            return ForkNo;
        }

        bool fork1HasTask = Fork1TaskNum != 0;
        bool fork2HasTask = Fork2TaskNum != 0;

        return (fork1HasTask, fork2HasTask) switch
        {
            (true, false) => 1,
            (false, true) => 2,
            (true, true) => 3,
            _ => 0,
        };
    }
}

/// <summary>
/// 状态区的一整套字段值。由状态机算好后一次性写入数据区，
/// 再由 <see cref="SrmFrames.WriteStatus"/> 编码成线路字节。
/// </summary>
public readonly record struct SrmStatusValues(
    ushort Fork1TaskNum,
    ushort Fork2TaskNum,
    byte FunctionReport,
    byte FunctionMode,
    byte ForkStatus,
    byte ActiveFork,
    byte ActionPoint,
    byte Aisle,
    byte Row,
    byte Column,
    byte Cell,
    byte Level,
    byte Depth,
    byte PhotoSensor,
    byte Face1,
    byte Face2,
    byte FireAlarm);
