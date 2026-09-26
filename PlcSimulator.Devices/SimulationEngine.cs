using System.Collections.Concurrent;
using PlcSimulator.Core.Faults;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Devices.Topology;

namespace PlcSimulator.Devices;

/// <summary>
/// 以固定 tick 单线程驱动所有站台状态机。
/// 统一 tick 让 GUI 的时间轴一致，也让测试可以用 <see cref="AdvanceOneTick"/> 确定性推进。
/// </summary>
public sealed class SimulationEngine : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTick = TimeSpan.FromMilliseconds(50);

    private readonly List<ConveyorStationMachine> _machines = [];

    private readonly List<IDeviceMachine> _deviceMachines = [];

    /// <summary>站台号 → 状态机。别名站台号也在这张表里，指向同一个物理站台的状态机。</summary>
    private readonly Dictionary<string, ConveyorStationMachine> _byStationNo = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly TimeSpan _tick;
    private readonly IFaultInjector _faultInjector;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SimulationEngine(TimeSpan? tick = null, IFaultInjector? faultInjector = null)
    {
        _tick = tick ?? DefaultTick;
        _faultInjector = faultInjector ?? PassThroughFaultInjector.Instance;
    }

    public IReadOnlyList<ConveyorStationMachine> Machines => _machines;

    /// <summary>设备级状态机（堆垛机这类整机设备）。与站台分开推进，互不影响。</summary>
    public IReadOnlyList<IDeviceMachine> DeviceMachines => _deviceMachines;

    public TimeSpan TickInterval => _tick;

    public bool IsRunning => _cts is not null;

    /// <summary>引擎自己的日志出口（推进异常、界面操作异常）。由宿主接上。</summary>
    public Action<string>? Log { get; set; }

    public event EventHandler<StationSnapshot>? StationChanged;

    public void Add(ConveyorStationMachine machine)
    {
        machine.StateChanged += (_, snapshot) => StationChanged?.Invoke(this, snapshot);
        _machines.Add(machine);
    }

    /// <summary>
    /// 登记一台设备级状态机。它不参与站台拓扑，只被 tick 推进。
    /// 状态变化的对外通知由调用方挂在具体类型上——站台与设备的快照形状不同，
    /// 塞进同一个事件只会让订阅方做类型判断。
    /// </summary>
    public void AddDeviceMachine(IDeviceMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        _deviceMachines.Add(machine);
    }

    /// <summary>
    /// 把一次站台操作排进引擎队列，交给引擎在自己的线程上执行。
    /// <para>
    /// GUI 的操作入口走这里，而不是直接调状态机方法：状态机是单线程驱动的，
    /// 界面线程直接改字段会与同一时刻的 tick 撞车——例如点「复位」的同时货正在在途，
    /// 复位清掉了目标站台，在途推进就会拿到空目标并抛异常，把整个仿真循环打断。
    /// </para>
    /// <para>引擎没在运行时不存在并发，直接执行，免得界面上的按钮看起来没反应。</para>
    /// </summary>
    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!IsRunning)
        {
            action();
            return;
        }

        _pending.Enqueue(action);
    }

    public void RegisterDevice(DeviceRuntime device, int? randomSeed = null)
    {
        // 用设备自身缓存的那张图：路径诊断会拿同一张来推路线，
        // 两边若各建一次，诊断结论就可能与实际跑货不一致。
        StationTopology topology = device.Topology;

        foreach (StationRuntime station in device.PhysicalStations)
        {
            // 传入 Find 让站台之间能互查——货物送达时需要判断目标站台是否忙。
            Add(new ConveyorStationMachine(station, randomSeed, _faultInjector, Find, topology));
        }

        // 站台号 → 状态机：别名站台号指向它所属物理站台的那一个，
        // 这样拓扑里出现的任何站台号都能找到对应的机器。
        foreach (StationRuntime station in device.Stations)
        {
            ConveyorStationMachine? machine = _machines.Find(
                m => string.Equals(m.Station.StationNo, station.PrimaryStationNo, StringComparison.Ordinal));

            if (machine is not null)
            {
                _byStationNo[station.StationNo] = machine;
            }
        }
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        if (cts is null)
        {
            return;
        }

        _cts = null;

        await cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 预期的取消。
            }

            _loop = null;
        }

        cts.Dispose();
    }

    /// <summary>手工推进一帧，供确定性测试使用。异常不隔离，测试要能看见它。</summary>
    public void AdvanceOneTick()
    {
        ApplyPendingOperations(isolateFaults: false);

        foreach (ConveyorStationMachine machine in _machines)
        {
            machine.Tick(_tick);
        }

        foreach (IDeviceMachine machine in _deviceMachines)
        {
            machine.Tick(_tick);
        }
    }

    public ConveyorStationMachine? Find(string stationNo)
        => _byStationNo.TryGetValue(stationNo, out ConveyorStationMachine? machine) ? machine : null;

    public IReadOnlyList<StationSnapshot> Snapshots()
        => [.. _machines.Select(static m => m.Snapshot())];

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// 执行 GUI 排进来的操作。始终在引擎线程上调用，因此状态机无需加锁。
    /// <paramref name="isolateFaults"/> 为真时逐条隔离：一次操作失败不带走整个仿真循环。
    /// </summary>
    private void ApplyPendingOperations(bool isolateFaults)
    {
        while (_pending.TryDequeue(out Action? action))
        {
            if (!isolateFaults)
            {
                action();
                continue;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"执行界面操作失败（已跳过）：{ex.Message}");
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_tick);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                ApplyPendingOperations(isolateFaults: true);

                foreach (ConveyorStationMachine machine in _machines)
                {
                    // 逐台隔离：某台站台推进时抛异常，只把它标成故障，不能带走整个仿真。
                    // 否则循环静默终结、站台全部定格，看起来就像"WCS 卡死了"。
                    try
                    {
                        machine.Tick(_tick);
                    }
                    catch (Exception ex)
                    {
                        machine.MarkFaulted($"推进异常：{ex.Message}");
                        Log?.Invoke($"站台 {machine.Station.StationNo} 推进异常，已置为故障：{ex.Message}");
                    }
                }

                foreach (IDeviceMachine machine in _deviceMachines)
                {
                    // 与站台同样的逐台隔离：设备推进异常只把它标成故障。
                    try
                    {
                        machine.Tick(_tick);
                    }
                    catch (Exception ex)
                    {
                        machine.MarkFaulted($"推进异常：{ex.Message}");
                        Log?.Invoke($"设备 {machine.DeviceId} 推进异常，已置为故障：{ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 预期的取消。
        }
    }
}
