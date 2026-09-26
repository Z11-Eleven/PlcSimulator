using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Frames;
using PlcSimulator.Hosting;

namespace PlcSimulator.App.Shared;

/// <summary>一份配置的设备构成。</summary>
public enum DeviceComposition
{
    /// <summary>没有启用的设备。</summary>
    Empty,

    /// <summary>只有输送线站台。</summary>
    ConveyorOnly,

    /// <summary>只有堆垛机。</summary>
    SrmOnly,

    /// <summary>两类都有（会提示拆成两份配置，见窗口基类的横幅）。</summary>
    Both,
}

/// <summary>重新加载的结果。失败时 <see cref="Failure"/> 是可直接展示的原因。</summary>
public readonly record struct ReloadResult(bool Success, string Failure)
{
    public static ReloadResult Ok => new(true, string.Empty);

    public static ReloadResult Fail(string failure) => new(false, failure);
}

/// <summary>
/// 一份配置的运行时：宿主、报文环形缓冲，以及它的生命周期。
/// <para>
/// **这是唯一真相源**——窗口只是它的视图，谁都不直接调 <c>Host.StartAsync</c>。
/// 启停必须走这里：拆成多个窗口后，「主窗口全部启动」与「子窗口启动按钮」会同时打到
/// 同一个会话，而 <c>SimulatorHost.StartAsync</c> 不是并发安全的（它在绑完端口之前
/// 只检查 <c>_servers.Count</c>），并发会把服务端起两遍、撞端口，且现象偶发难查。
/// </para>
/// </summary>
public sealed class SimulatorSession : IAsyncDisposable
{
    /// <summary>日志历史保留条数。窗口后开也能补看开局那几条。</summary>
    private const int MaxLogHistory = 500;

    private readonly Lock _gate = new();
    private readonly Lock _logLock = new();
    private readonly Queue<string> _logHistory = new();

    /// <summary>启停与重载的串行链：并发调用依次排队。</summary>
    private Task _transition = Task.CompletedTask;

    /// <summary>正在进行的停止。主窗口关闭与子窗口关闭都会停同一会话，靠它做到幂等。</summary>
    private Task? _stopInFlight;

    private SimulatorHost _host;

    public SimulatorSession(string path, ConfigLoadResult result)
    {
        Path = path;
        Result = result;
        FrameLog = new RingBufferFrameLog();
        _host = CreateHost(result);
    }

    public string Path { get; }

    public ConfigLoadResult Result { get; private set; }

    public SimulatorHost Host => _host;

    /// <summary>
    /// 这份配置专属的报文缓冲。一条链路一个 host、一个 host 一个它，
    /// 「报文日志各看各的」就是这么来的，不需要按设备号过滤。
    /// </summary>
    public RingBufferFrameLog FrameLog { get; }

    public bool IsRunning => _host.IsRunning;

    /// <summary>报文计数，主窗口总览列用。</summary>
    public long LogRevision => FrameLog.TotalWritten;

    /// <summary>宿主换新之后触发（重载）。窗口据此重新绑定视图。</summary>
    public event EventHandler? Reloaded;

    /// <summary>宿主的一条状态日志。已带来源信息，窗口自己负责封送到 UI 线程。</summary>
    public event EventHandler<string>? Logged;

    /// <summary>最近若干条状态日志。窗口绑定时补显示，免得错过开局那几条。</summary>
    public IReadOnlyList<string> LogHistory
    {
        get
        {
            lock (_logLock)
            {
                return [.. _logHistory];
            }
        }
    }

    /// <summary>由宿主实况算，而不是数配置里的设备——被 <c>enabled: false</c> 的设备不占位置。</summary>
    public DeviceComposition Composition => (_host.Devices.Count > 0, _host.SrmDevices.Count > 0) switch
    {
        (true, true) => DeviceComposition.Both,
        (true, false) => DeviceComposition.ConveyorOnly,
        (false, true) => DeviceComposition.SrmOnly,
        _ => DeviceComposition.Empty,
    };

    public string DeviceSummary => Composition switch
    {
        DeviceComposition.ConveyorOnly => $"输送线 {_host.Devices.Count} 台",
        DeviceComposition.SrmOnly => $"堆垛机 {_host.SrmDevices.Count} 台",
        DeviceComposition.Both => $"输送线 {_host.Devices.Count} 台 + 堆垛机 {_host.SrmDevices.Count} 台",
        _ => "无设备",
    };

    /// <summary>树上与标题栏显示的名字（只取文件名，长路径放悬停提示）。</summary>
    public string DisplayName => System.IO.Path.GetFileName(Path);

    public string DisplayNameWithState => IsRunning ? $"{DisplayName}  ● 运行中" : DisplayName;

    /// <summary>悬停提示：完整路径 + 这份配置里有几台什么设备。</summary>
    public string ToolTip => $"{System.IO.Path.GetFullPath(Path)}\n{DeviceSummary}";

    public Task StartAsync() => RunExclusive(() => _host.IsRunning ? Task.CompletedTask : _host.StartAsync());

    /// <summary>
    /// 停止。幂等：主窗口关闭与子窗口关闭都会走到这里，同一时刻只会有一次真正在停。
    /// </summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            return _stopInFlight ??= RunExclusive(StopCoreAsync);
        }
    }

    /// <summary>
    /// 重新加载：读盘 → 校验 → 通过后才换宿主。
    /// 文件坏了时**正在跑的这份原样不动**，比「先关后开」稳妥。
    /// </summary>
    public Task<ReloadResult> ReloadAsync() => RunExclusive(ReloadCoreAsync);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public override string ToString() => DisplayName;

    private async Task StopCoreAsync()
    {
        try
        {
            await _host.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _stopInFlight = null;
            }
        }
    }

    private async Task<ReloadResult> ReloadCoreAsync()
    {
        ConfigLoadResult result;

        try
        {
            result = ConfigLoader.LoadFromFile(Path);
        }
        catch (Exception ex)
        {
            return ReloadResult.Fail($"加载配置失败：{ex.Message}");
        }

        if (!result.IsValid)
        {
            return ReloadResult.Fail(
                "配置校验未通过：\n\n" + string.Join("\n", result.Errors.Select(static e => "· " + e)));
        }

        bool wasRunning = _host.IsRunning;

        // 旧宿主必须停干净再换，否则端口没释放，新宿主绑不上。
        await _host.StopAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);

        Result = result;
        _host = CreateHost(result);

        if (wasRunning)
        {
            await _host.StartAsync().ConfigureAwait(false);
        }

        AppendLog($"配置已重新加载：{DisplayName}");
        Reloaded?.Invoke(this, EventArgs.Empty);

        return ReloadResult.Ok;
    }

    private SimulatorHost CreateHost(ConfigLoadResult result)
    {
        SimulatorHost host = SimulatorHost.Create(result.Config, FrameLog);

        // 日志出口挂在会话上（而不是窗口上）：窗口可能后开，
        // 挂在窗口上的话会错过 Build 阶段那几条（故障注入已启用、设备已禁用…）。
        host.Log = AppendLog;

        return host;
    }

    private void AppendLog(string message)
    {
        lock (_logLock)
        {
            _logHistory.Enqueue(message);

            while (_logHistory.Count > MaxLogHistory)
            {
                _logHistory.Dequeue();
            }
        }

        Logged?.Invoke(this, message);
    }

    /// <summary>
    /// 把一次状态变更排进串行链。前一次失败不能让后续操作卡住，所以链条本身吃掉异常，
    /// 但**本次调用者仍然看得到异常**。
    /// </summary>
    private Task RunExclusive(Func<Task> operation)
    {
        lock (_gate)
        {
            Task next = _transition
                .ContinueWith(
                    _ => operation(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();

            TrackTransition(next);
            return next;
        }
    }

    /// <summary>同 <see cref="RunExclusive(Func{Task})"/>，但带返回值。</summary>
    private Task<T> RunExclusive<T>(Func<Task<T>> operation)
    {
        lock (_gate)
        {
            Task<T> next = _transition
                .ContinueWith(
                    _ => operation(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();

            TrackTransition(next);
            return next;
        }
    }

    private void TrackTransition(Task task)
        => _transition = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
}
