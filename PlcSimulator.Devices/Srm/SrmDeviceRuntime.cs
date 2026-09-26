using System.Collections.Concurrent;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocol;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Core.Registers;

namespace PlcSimulator.Devices.Srm;

/// <summary>
/// 一台堆垛机的数据区与 Socket 会话。
/// <para>
/// 三个区段（指令 / 状态 / 报警）在数据区里的基址纯属内部约定：
/// Socket 协议没有「地址」概念，改基址不影响 WCS 看到的任何一个字节。
/// </para>
/// <para>
/// 数据区自带锁，因此 Socket 线程（写指令区、读状态区）与引擎线程（读指令区、写状态区）
/// 之间不需要额外同步——每次交接都是一次完整的区段读写，不会读到写了一半的内容。
/// </para>
/// </summary>
public sealed class SrmDeviceRuntime : ISocketDeviceSession
{
    /// <summary>待处理指令队列的容量。双货叉一次下发两条也够用，超出说明对端在灌帧。</summary>
    private const int MaxPendingCommands = 64;

    private readonly ConcurrentQueue<SrmCommand> _commands = new();

    private SrmDeviceRuntime(DeviceConfig config, SocketPortsConfig ports, SrmOptionsConfig options)
    {
        Config = config;
        Ports = ports;
        Options = options;

        Space = new DeviceRegisterSpace(
            config.Id,
            [
                new RegisterBlock("command", SrmLayout.CommandAreaBase, SrmLayout.CommandAreaLength),
                new RegisterBlock("status", SrmLayout.StatusAreaBase, SrmLayout.StatusAreaLength),
                new RegisterBlock("alarm", SrmLayout.AlarmAreaBase, SrmLayout.AlarmAreaLength),
            ]);
    }

    public DeviceConfig Config { get; }

    public SocketPortsConfig Ports { get; }

    public SrmOptionsConfig Options { get; }

    public DeviceRegisterSpace Space { get; }

    public string DeviceId => Config.Id;

    public string Name => Config.Name;

    public int CommandFrameLength => SrmLayout.CommandFrameLength;

    public int StatusFrameLength => Ports.StatusFrameLength;

    public int AlarmFrameLength => Ports.AlarmFrameLength;

    /// <summary>因队列满而被丢弃的指令条数。非零说明对端在灌帧，值得排查。</summary>
    public long DroppedCommands { get; private set; }

    /// <summary>按配置造一台堆垛机，并把初值摆进数据区。</summary>
    public static SrmDeviceRuntime Create(DeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        SocketPortsConfig ports = config.SocketPorts
            ?? throw new InvalidDataException($"设备 {config.Id} 是 Socket 设备，但缺少 socketPorts 配置。");
        SrmOptionsConfig options = config.Srm
            ?? throw new InvalidDataException($"设备 {config.Id} 是堆垛机，但缺少 srm 配置。");

        SrmDeviceRuntime runtime = new(config, ports, options);
        runtime.ApplyInitialValues();
        return runtime;
    }

    // ---- 会话：Socket 线程调用 ----

    public void OnCommandFrame(ReadOnlySpan<byte> frame)
    {
        if (!SrmFrames.TryParseCommand(frame, out SrmCommand command))
        {
            return;
        }

        // 指令负载原样落写区：报文日志与点位监视表都能看到 WCS 究竟发了什么，
        // 排查「指令收到但没动作」时这是第一手证据。
        Space.TryWriteBytes(
            SrmLayout.CommandAreaBase,
            frame[SrmLayout.CommandPayloadOffset..SrmLayout.CommandFrameLength]);

        SubmitCommand(command);
    }

    public int WriteStatusFrame(Span<byte> destination)
    {
        int length = Math.Min(StatusFrameLength, destination.Length);
        Space.TryReadBytes(SrmLayout.StatusAreaBase, destination[..length]);
        return length;
    }

    public int WriteAlarmFrame(Span<byte> destination)
    {
        int length = Math.Min(AlarmFrameLength, destination.Length);
        Space.TryReadBytes(SrmLayout.AlarmAreaBase, destination[..length]);
        return length;
    }

    // ---- 指令交接：Socket 线程 → 引擎线程 ----

    /// <summary>投递一条指令。测试、命令行驱动器与界面走这里，不必经过 socket。</summary>
    public void SubmitCommand(in SrmCommand command)
    {
        if (_commands.Count >= MaxPendingCommands)
        {
            _commands.TryDequeue(out _);
            DroppedCommands++;
        }

        _commands.Enqueue(command);
    }

    public bool TryDequeueCommand(out SrmCommand command) => _commands.TryDequeue(out command);

    public int PendingCommandCount => _commands.Count;

    // ---- 数据区读写：引擎线程调用 ----

    /// <summary>把一整套状态值编码进状态区。</summary>
    public void WriteStatus(in SrmStatusValues values)
    {
        Span<byte> buffer = stackalloc byte[SrmLayout.StatusAreaLength];
        SrmFrames.WriteStatus(buffer, values);
        Space.TryWriteBytes(SrmLayout.StatusAreaBase, buffer);
    }

    /// <summary>清空报警位图。</summary>
    public void ClearAlarmBitmap()
    {
        Span<byte> zeros = stackalloc byte[SrmLayout.AlarmAreaLength];
        zeros.Clear();
        Space.TryWriteBytes(SrmLayout.AlarmAreaBase, zeros);
    }

    public void ApplyInitialValues()
    {
        SrmPoint initial = SrmPoint.FromConfig(Options.InitialPoint);

        byte mode = Options.Fault
            ? SrmFunctionMode.Fault
            : Options.Auto ? SrmFunctionMode.Standby : SrmFunctionMode.Manual;

        WriteStatus(new SrmStatusValues(
            Fork1TaskNum: 0,
            Fork2TaskNum: 0,
            FunctionReport: SrmFunctionReport.Idle,
            FunctionMode: mode,
            ForkStatus: 0,
            ActiveFork: 0,
            ActionPoint: initial.ActionPoint,
            Aisle: initial.Aisle,
            Row: initial.Row,
            Column: initial.Column,
            Cell: initial.Cell,
            Level: initial.Level,
            Depth: initial.Depth,
            PhotoSensor: 0,
            Face1: 0,
            Face2: 0,
            FireAlarm: Options.FireAlarm ? (byte)1 : (byte)0));

        ClearAlarmBitmap();
        Space.TryClearRange(SrmLayout.CommandAreaBase, SrmLayout.CommandAreaLength);
    }
}
