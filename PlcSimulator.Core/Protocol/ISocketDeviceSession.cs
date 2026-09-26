namespace PlcSimulator.Core.Protocol;

/// <summary>
/// 一台 Socket 设备的会话。
/// <para>
/// 服务器只懂「定长帧进、触发字节→回一帧」，帧里是什么由设备侧解释。
/// 这样协议无关的连接管理、限流与报文日志可以只有一份，
/// 而堆垛机（以及将来同类的设备）只需实现这一小组方法。
/// </para>
/// </summary>
public interface ISocketDeviceSession
{
    string DeviceId { get; }

    /// <summary>指令帧的线长（堆垛机为 26 字节）。</summary>
    int CommandFrameLength { get; }

    /// <summary>收到一条完整指令帧时调用。实现不应抛异常——帧是外部输入。</summary>
    void OnCommandFrame(ReadOnlySpan<byte> frame);

    /// <summary>状态帧线长。</summary>
    int StatusFrameLength { get; }

    /// <summary>把当前状态写成状态帧，返回实际写入的字节数。</summary>
    int WriteStatusFrame(Span<byte> destination);

    /// <summary>报警帧线长。</summary>
    int AlarmFrameLength { get; }

    /// <summary>把当前报警位图写成报警帧，返回实际写入的字节数。</summary>
    int WriteAlarmFrame(Span<byte> destination);
}
