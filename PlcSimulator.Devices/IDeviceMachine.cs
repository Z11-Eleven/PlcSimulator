namespace PlcSimulator.Devices;

/// <summary>
/// 设备级状态机：一台设备一个行为主体（如堆垛机），由引擎按 tick 推进。
/// <para>
/// 与站台状态机的区别：站台是「一个坐标点上的搬运工」，要靠拓扑找上下游；
/// 设备级状态机自成一体，只被引擎推进、只与自己的数据区打交道。
/// </para>
/// </summary>
public interface IDeviceMachine
{
    string DeviceId { get; }

    /// <summary>最近一次状态变化的说明，供日志与界面展示。</summary>
    string LastEvent { get; }

    void Tick(TimeSpan delta);

    /// <summary>复位成配置初值。</summary>
    void Reset();

    /// <summary>推进异常时的兜底：把设备标成故障，而不是让整个仿真循环终结。</summary>
    void MarkFaulted(string reason);
}
