namespace PlcSimulator.Core.Faults;

public enum FaultAction
{
    /// <summary>正常放行。</summary>
    Pass = 0,

    /// <summary>直接回 Modbus 异常响应。</summary>
    ExceptionResponse,

    /// <summary>不回帧，让客户端自己超时（用于验证 WCS 的读失败与降速重连）。</summary>
    SilentDrop,

    /// <summary>延迟一段时间后正常处理。</summary>
    DelayThenPass,

    /// <summary>延迟一段时间后回异常响应。</summary>
    DelayThenException,

    /// <summary>直接断开连接（用于验证 WCS 的重连逻辑）。</summary>
    CloseConnection,
}

public enum ConnectionRole
{
    Unknown = 0,
    Read,
    Write,
}

/// <summary>一次请求的上下文，供故障规则匹配。</summary>
public readonly record struct FaultRequestContext(
    string DeviceId,
    string ConnectionId,
    string RemoteAddress,
    ConnectionRole Role,
    byte FunctionCode,
    ushort Address,
    ushort Quantity);

public readonly record struct FaultDecision(
    FaultAction Action,
    int DelayMs,
    byte ExceptionCode,
    string? RuleName)
{
    public static readonly FaultDecision Pass = new(FaultAction.Pass, 0, 0, null);

    public bool IsPass => Action == FaultAction.Pass;
}

/// <summary>供 GUI 展示的规则状态。</summary>
public sealed record FaultRuleStatus(
    string Name,
    string Effect,
    string MatchSummary,
    int HitCount,
    bool RuntimeEnabled);

/// <summary>
/// 故障注入门面。默认实现是零分配的直通单例，
/// 只有配置里开启并且有启用规则时才会换成真正会匹配的实现。
/// </summary>
public interface IFaultInjector
{
    bool Enabled { get; }

    IReadOnlyList<FaultRuleStatus> Rules { get; }

    FaultDecision Decide(in FaultRequestContext context);

    /// <summary>是否抑制该站台的心跳置位（语义级故障，用于验证 WCS 在设备无响应时是否会卡死）。</summary>
    bool SuppressHeartbeat(string deviceId, string stationNo);

    /// <summary>运行时开关某条规则（GUI 用）。按序号，因为规则名可以重复或为空。</summary>
    bool SetRuleEnabledAt(int index, bool enabled);
}

public sealed class PassThroughFaultInjector : IFaultInjector
{
    public static readonly PassThroughFaultInjector Instance = new();

    private PassThroughFaultInjector()
    {
    }

    public bool Enabled => false;

    public IReadOnlyList<FaultRuleStatus> Rules => [];

    public FaultDecision Decide(in FaultRequestContext context) => FaultDecision.Pass;

    public bool SuppressHeartbeat(string deviceId, string stationNo) => false;

    public bool SetRuleEnabledAt(int index, bool enabled) => false;
}
