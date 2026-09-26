using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Devices.Srm;

/// <summary>
/// 堆垛机状态机。设备级一个实例，内部含两个货叉子状态。
/// <para>
/// 作业状态字（状态区偏移 6）在 WCS 侧只有一个字节，而货叉有两个，
/// 因此由 <see cref="ResolveFunctionReport"/> 把两个货叉的状态合成一个值——
/// 该规则集中在一处，现场确证后可一处修改。
/// </para>
/// </summary>
public sealed class SrmMachine : IDeviceMachine
{
    /// <summary>一次 tick 内最多处理几条指令，防止对端灌帧把一帧拖长。</summary>
    private const int MaxCommandsPerTick = 8;

    private readonly SrmDeviceRuntime _runtime;
    private readonly SrmOptionsConfig _options;
    private readonly SrmFork[] _forks;
    private readonly Random _random;

    private SrmPoint _position;
    private bool _faulted;
    private bool _manual;
    private bool _fireAlarm;
    private byte _activeFork;
    private SrmStatusValues _lastWritten;
    private bool _hasWritten;

    public SrmMachine(SrmDeviceRuntime runtime, int? randomSeed = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
        _options = runtime.Options;
        _random = randomSeed is int seed ? new Random(seed) : new Random();

        int forkCount = Math.Clamp(_options.ForkCount, 1, 2);
        _forks = new SrmFork[forkCount];
        for (int i = 0; i < forkCount; i++)
        {
            _forks[i] = new SrmFork((byte)(i + 1));
        }

        Reset();
    }

    public string DeviceId => _runtime.DeviceId;

    public string LastEvent { get; private set; } = string.Empty;

    /// <summary>状态发生变化时触发（值没变不发）。</summary>
    public event EventHandler<SrmSnapshot>? StateChanged;

    public SrmDeviceRuntime Runtime => _runtime;

    /// <summary>手动模式：脱离自动，WCS 会判设备不可用。</summary>
    public bool Manual
    {
        get => _manual;
        set
        {
            if (_manual == value)
            {
                return;
            }

            _manual = value;
            LastEvent = value ? "打手动" : "恢复自动";
            Publish();
        }
    }

    /// <summary>火警：置位后状态区偏移 22 上报，用于造避让场景。</summary>
    public bool FireAlarm
    {
        get => _fireAlarm;
        set
        {
            if (_fireAlarm == value)
            {
                return;
            }

            _fireAlarm = value;
            LastEvent = value ? "置火警" : "清火警";
            Publish();
        }
    }

    public bool Faulted => _faulted;

    public void Tick(TimeSpan delta)
    {
        ApplyPendingCommands();
        AdvanceForks(delta.TotalMilliseconds);
        Publish();
    }

    public void Reset()
    {
        foreach (SrmFork fork in _forks)
        {
            fork.Reset();
        }

        _position = SrmPoint.FromConfig(_options.InitialPoint);
        _faulted = _options.Fault;
        _manual = !_options.Auto;
        _fireAlarm = _options.FireAlarm;
        _activeFork = 0;
        _hasWritten = false;
        LastEvent = "复位";

        _runtime.ApplyInitialValues();
        Publish();
    }

    public void MarkFaulted(string reason)
    {
        _faulted = true;
        LastEvent = $"故障：{reason}";
        Publish();
    }

    /// <summary>投递一条指令（测试、命令行驱动器、界面用；也可由 socket 线程经运行时投递）。</summary>
    public void SubmitCommand(in SrmCommand command) => _runtime.SubmitCommand(command);

    public SrmSnapshot Snapshot() => new(
        DeviceId,
        ResolveFunctionReport(),
        BuildFunctionMode(),
        _faulted,
        _position,
        [.. _forks.Select(static f => new SrmForkSnapshot(f.ForkNo, f.State, f.TaskNum, f.Loaded))],
        LastEvent);

    // ---- 指令处理 ----

    private void ApplyPendingCommands()
    {
        int budget = MaxCommandsPerTick;
        while (budget-- > 0 && _runtime.TryDequeueCommand(out SrmCommand command))
        {
            ApplyCommand(command);
        }
    }

    private void ApplyCommand(in SrmCommand command)
    {
        if (command.FireFlag != 0)
        {
            _fireAlarm = true;
            LastEvent = "收到火警标志";
        }

        if (command.IsClear)
        {
            ClearForks(command);
            return;
        }

        SrmForkState? target = command.CommandType switch
        {
            SrmCommandType.GetC => SrmForkState.PickLocating,
            SrmCommandType.PutC => SrmForkState.PutLocating,
            SrmCommandType.Stock => SrmForkState.StockLocating,
            SrmCommandType.PosGet => SrmForkState.MoveLocating,
            _ => null,
        };

        if (target is null)
        {
            // 不改状态、不断连接：帧仍然落到写区，报文日志里看得到原始字节。
            LastEvent = $"未识别的指令类型 {command.CommandType}，已忽略";
            return;
        }

        StartForkAction(command.ResolveForkNo(), target.Value, command);
    }

    /// <summary>
    /// 货叉只在「等这个动作」时才接受新指令：
    /// 取货/盘点/移动只在空闲时接受，放货只在取货完成后接受。
    /// 其余情况一律忽略并记日志——这既避免打断进行中的循环，
    /// 也是观测「WCS 是否在重发同一指令」的手段（日志里数得出重复帧）。
    /// </summary>
    private static bool Accepts(SrmForkState state, byte commandType) => commandType switch
    {
        SrmCommandType.GetC or SrmCommandType.Stock or SrmCommandType.PosGet => state == SrmForkState.Idle,
        SrmCommandType.PutC => state == SrmForkState.PickDone,
        _ => false,
    };

    private void StartForkAction(byte forkNo, SrmForkState target, in SrmCommand command)
    {
        bool anyStarted = false;

        foreach (SrmFork fork in SelectForks(forkNo))
        {
            if (!Accepts(fork.State, command.CommandType))
            {
                LastEvent = $"货叉{fork.ForkNo} 当前为{fork.State.ToChineseText()}，"
                    + $"指令 {command.CommandType} 不被接受，已忽略";
                continue;
            }

            fork.State = target;
            fork.TaskNum = ResolveTaskNum(fork, command);
            fork.GoodsType = fork.ForkNo == 1 ? command.Fork1GoodsType : command.Fork2GoodsType;
            fork.Target = SrmPoint.FromCommand(command);
            fork.ElapsedMs = 0;
            fork.RequiredMs = RequiredMsFor(target);

            _activeFork = fork.ForkNo;
            anyStarted = true;
            LastEvent = $"货叉{fork.ForkNo} {target.ToChineseText()}，任务号 {fork.TaskNum}";
        }

        if (!anyStarted)
        {
            return;
        }
    }

    /// <summary>
    /// 取本货叉的任务号。WCS 在双货叉下会把不动的那个工位置 0，
    /// 因此本货叉为 0 时退回用另一个工位的号，免得状态区里任务号是空的。
    /// </summary>
    private static ushort ResolveTaskNum(SrmFork fork, in SrmCommand command)
    {
        ushort own = fork.ForkNo == 1 ? command.Fork1TaskNum : command.Fork2TaskNum;
        if (own != 0)
        {
            return own;
        }

        return command.Fork1TaskNum != 0 ? command.Fork1TaskNum : command.Fork2TaskNum;
    }

    private void ClearForks(in SrmCommand command)
    {
        byte forkNo = command.ResolveForkNo();

        foreach (SrmFork fork in SelectForks(forkNo))
        {
            fork.Reset();
        }

        if (forkNo is 0 or 3)
        {
            _activeFork = 0;
        }

        LastEvent = forkNo switch
        {
            0 or 3 => "清除指令：两个货叉归零",
            _ => $"清除指令：货叉{forkNo} 归零",
        };
    }

    private IEnumerable<SrmFork> SelectForks(byte forkNo) => forkNo switch
    {
        1 => [_forks[0]],
        2 when _forks.Length > 1 => [_forks[1]],
        0 or 3 => _forks,
        _ => [],
    };

    // ---- 动作推进 ----

    private void AdvanceForks(double deltaMs)
    {
        foreach (SrmFork fork in _forks)
        {
            if (!fork.State.IsInMotion())
            {
                continue;
            }

            fork.ElapsedMs += deltaMs;
            if (fork.ElapsedMs >= fork.RequiredMs)
            {
                CompleteFork(fork);
            }
        }
    }

    private void CompleteFork(SrmFork fork)
    {
        fork.State = fork.State switch
        {
            SrmForkState.PickLocating => SrmForkState.PickDone,
            SrmForkState.PutLocating => SrmForkState.PutDone,
            SrmForkState.MoveLocating => SrmForkState.MoveDone,
            SrmForkState.StockLocating => SrmForkState.StockDone,
            _ => fork.State,
        };

        switch (fork.State)
        {
            case SrmForkState.PickDone:
                fork.Loaded = true;
                break;
            case SrmForkState.PutDone:
                fork.Loaded = false;
                break;
        }

        _position = fork.Target;
        LastEvent = $"货叉{fork.ForkNo} 动作完成（{fork.State.ToChineseText()}）";
    }

    private double RequiredMsFor(SrmForkState target)
    {
        int baseMs = target == SrmForkState.MoveLocating
            ? _options.TravelDelayMs
            : _options.TravelDelayMs + _options.ActionDelayMs;

        return Math.Max(0, baseMs + NextJitter());
    }

    private int NextJitter()
        => _options.JitterMs <= 0 ? 0 : _random.Next(-_options.JitterMs, _options.JitterMs + 1);

    // ---- 状态上报 ----

    /// <summary>
    /// 两个货叉的状态合成一个作业状态字：任一货叉非空闲时上报它的状态，
    /// 两个都非空闲时货叉 1 优先（数组顺序即优先级）。
    /// </summary>
    private byte ResolveFunctionReport()
    {
        foreach (SrmFork fork in _forks)
        {
            if (fork.State != SrmForkState.Idle)
            {
                return fork.State.ToFunctionReport();
            }
        }

        return SrmFunctionReport.Idle;
    }

    /// <summary>
    /// 工作模式。**就绪位不因作业而清零**：WCS 用 <c>[0]==1 &amp;&amp; [4]==1 &amp;&amp; [7]==0</c>
    /// 判设备可用，作业中清零会让它把正在干活的堆垛机判成不可用。
    /// </summary>
    private byte BuildFunctionMode()
    {
        if (_faulted)
        {
            return SrmFunctionMode.Fault;
        }

        return _manual ? SrmFunctionMode.Manual : SrmFunctionMode.Standby;
    }

    private byte BuildForkStatus()
    {
        byte status = 0;

        foreach (SrmFork fork in _forks)
        {
            if (!fork.Loaded)
            {
                continue;
            }

            status |= fork.ForkNo == 1 ? SrmForkStatusBits.Fork1 : SrmForkStatusBits.Fork2;
        }

        return status;
    }

    /// <summary>把当前状态写进数据区。值没变就不写——省掉每 tick 一次的无谓写入。</summary>
    private void Publish()
    {
        SrmStatusValues values = BuildStatusValues();

        if (_hasWritten && values == _lastWritten)
        {
            return;
        }

        _runtime.WriteStatus(values);
        _lastWritten = values;
        _hasWritten = true;

        StateChanged?.Invoke(this, Snapshot());
    }

    private SrmStatusValues BuildStatusValues()
    {
        SrmFork fork1 = _forks[0];
        SrmFork? fork2 = _forks.Length > 1 ? _forks[1] : null;

        return new SrmStatusValues(
            Fork1TaskNum: fork1.TaskNum,
            Fork2TaskNum: fork2?.TaskNum ?? 0,
            FunctionReport: ResolveFunctionReport(),
            FunctionMode: BuildFunctionMode(),
            ForkStatus: BuildForkStatus(),
            ActiveFork: _activeFork,
            ActionPoint: _position.ActionPoint,
            Aisle: _position.Aisle,
            Row: _position.Row,
            Column: _position.Column,
            Cell: _position.Cell,
            Level: _position.Level,
            Depth: _position.Depth,
            PhotoSensor: (byte)(fork1.Loaded || (fork2?.Loaded ?? false) ? 1 : 0),
            Face1: 0,
            Face2: 0,
            FireAlarm: _fireAlarm ? (byte)1 : (byte)0);
    }
}

/// <summary>单个货叉的运行时状态。</summary>
internal sealed class SrmFork(byte forkNo)
{
    public byte ForkNo { get; } = forkNo;

    public SrmForkState State { get; set; } = SrmForkState.Idle;

    public ushort TaskNum { get; set; }

    public byte GoodsType { get; set; }

    public bool Loaded { get; set; }

    public SrmPoint Target { get; set; }

    public double ElapsedMs { get; set; }

    public double RequiredMs { get; set; }

    public void Reset()
    {
        State = SrmForkState.Idle;
        TaskNum = 0;
        GoodsType = 0;
        Loaded = false;
        ElapsedMs = 0;
        RequiredMs = 0;
    }
}
