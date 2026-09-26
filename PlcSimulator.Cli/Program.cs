using System.Net.Sockets;
using System.Text;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Frames;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Topology;
using PlcSimulator.Hosting;
using PlcSimulator.Protocol.Socket;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return 0;
}

// 配置文件读不出来（IO/JSON 格式错）时给一句干净的提示，而不是抛一堆堆栈。
try
{
    return args[0] switch
    {
        "serve" => await ServeAsync(args[1..]),
        "check" => Check(args[1..]),
        "path" => PrintPath(args[1..]),
        "srm" => await SrmAsync(args[1..]),
        _ => UnknownCommand(args[0]),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"执行失败：{ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("潜江太蓝 PLC 模拟器（Modbus TCP 服务端）");
    Console.WriteLine();
    Console.WriteLine("用法：");
    Console.WriteLine("  serve [--config <路径>] [--log-frames] [--log-file <路径>]   启动模拟器");
    Console.WriteLine("  check [<路径>]                            只校验配置文件，不启动");
    Console.WriteLine("  path <配置> <起点> <终点>                 打印货物会经过的站台序列");
    Console.WriteLine("  path <配置> <起点> <终点> --detail        完整诊断：逐跳决策、时间轴、失败原因");
    Console.WriteLine("  srm cycle [--config <配置>] [--pick <站台>] [--put <站台>] [--frames]");
    Console.WriteLine("                                            扮演 WCS 连上堆垛机跑一遍取货→放货→清除闭环");
    Console.WriteLine();
    Console.WriteLine("选项：");
    Console.WriteLine("  -c, --config <路径>   配置文件路径，默认 config/simulator.json");
    Console.WriteLine("      --log-frames      实时打印收发的报文");
    Console.WriteLine("      --log-file <路径> 把报文与状态机流转写入文件（即时落盘，便于留证）");
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"未知命令：{command}");
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

static async Task<int> ServeAsync(string[] args)
{
    string configPath = "config/simulator.json";
    bool logFrames = false;
    string? logFilePath = null;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--config" or "-c" when i + 1 < args.Length:
                configPath = args[++i];
                break;
            case "--log-frames":
                logFrames = true;
                break;
            case "--log-file" when i + 1 < args.Length:
                logFilePath = args[++i];
                break;
            default:
                Console.Error.WriteLine($"未知参数：{args[i]}");
                return 1;
        }
    }

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"找不到配置文件：{Path.GetFullPath(configPath)}");
        return 1;
    }

    ConfigLoadResult loadResult = ConfigLoader.LoadFromFile(configPath);

    foreach (string warning in loadResult.Warnings)
    {
        Console.WriteLine($"[警告] {warning}");
    }

    if (!loadResult.IsValid)
    {
        Console.Error.WriteLine("配置校验未通过：");
        foreach (string error in loadResult.Errors)
        {
            Console.Error.WriteLine($"  - {error}");
        }

        return 1;
    }

    Console.WriteLine($"已加载配置 {Path.GetFullPath(configPath)}，共 {loadResult.Config.Devices.Count} 台设备。");

    var frameLog = new RingBufferFrameLog();
    StreamWriter? logWriter = null;
    object logLock = new();

    if (logFilePath is not null)
    {
        logWriter = new StreamWriter(File.Create(logFilePath), Encoding.UTF8) { AutoFlush = true };
        logWriter.WriteLine($"# 潜江太蓝 PLC 模拟器日志  启动于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        logWriter.WriteLine($"# 配置 {Path.GetFullPath(configPath)}");
        Console.WriteLine($"日志将写入 {Path.GetFullPath(logFilePath)}");
    }

    void WriteLine(string message)
    {
        Console.WriteLine(message);

        if (logWriter is not null)
        {
            // 报文日志来自多条连接线程，StreamWriter 不是线程安全的：
            // 不加锁的话日志文件里会互相插队、切出半行。
            lock (logLock)
            {
                logWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
            }
        }
    }

    // 指定了日志文件时，无论是否要求打印到控制台，都把报文写进去（便于留证）。
    if (logFrames || logWriter is not null)
    {
        frameLog.EntryAdded += (_, entry) => WriteLine($"{entry.DirectionText} [{entry.DeviceId}] {entry.Summary}");
    }

    await using SimulatorHost host = SimulatorHost.Create(loadResult.Config, frameLog);
    host.Log = WriteLine;

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await host.StartAsync(cts.Token);

    Console.WriteLine();
    Console.WriteLine("模拟器已启动，按 Ctrl+C 停止。");
    if (!logFrames)
    {
        Console.WriteLine("（加 --log-frames 可实时打印报文）");
    }

    Console.WriteLine();

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // 用户按了 Ctrl+C。
    }

    Console.WriteLine("正在停止…");
    await host.StopAsync();
    logWriter?.Dispose();
    return 0;
}

static int Check(string[] args)
{
    string configPath = args.Length > 0 ? args[0] : "config/simulator.json";

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"找不到配置文件：{Path.GetFullPath(configPath)}");
        return 1;
    }

    ConfigLoadResult loadResult = ConfigLoader.LoadFromFile(configPath);

    foreach (string warning in loadResult.Warnings)
    {
        Console.WriteLine($"[警告] {warning}");
    }

    foreach (string error in loadResult.Errors)
    {
        Console.Error.WriteLine($"[错误] {error}");
    }

    if (loadResult.IsValid)
    {
        int srmCount = loadResult.Config.Devices.Count(
            static d => string.Equals(d.Protocol, "Socket", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(
            $"配置有效：{loadResult.Config.Devices.Count} 台设备"
            + $"（输送线 {loadResult.Config.Devices.Count - srmCount} 台、堆垛机 {srmCount} 台），"
            + $"{loadResult.Config.Devices.Sum(static d => d.Stations.Count)} 个站台。");

        Console.WriteLine();
        Console.WriteLine("点位映射：");

        foreach (DeviceConfig device in loadResult.Config.Devices)
        {
            if (string.Equals(device.Protocol, "Socket", StringComparison.OrdinalIgnoreCase))
            {
                PrintSrmDevice(device);
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"  设备 {device.Id}  {device.Ip}:{device.Port}  站号 {device.SlaveId}  对接协议 {device.ProtocolType}");

            foreach (RegisterBlockConfig block in device.Blocks)
            {
                Console.WriteLine(
                    $"    寄存器块：字节 [{block.BaseByteOffset}, {block.BaseByteOffset + block.LengthBytes})"
                    + $" = 寄存器 [{block.BaseByteOffset / 2}, {(block.BaseByteOffset + block.LengthBytes) / 2})");
            }

            foreach (StationConfig station in device.Stations)
            {
                Console.WriteLine(
                    $"    站台 {station.StationNo}：字节 [{station.ByteOffset}, {station.ByteOffset + station.LengthBytes})"
                    + $" = 寄存器 [{station.ByteOffset / 2}, {(station.ByteOffset + station.LengthBytes) / 2})");
            }
        }

        return 0;
    }

    return 1;
}

/// <summary>堆垛机（Socket 传输）的点位摘要：三个端口与三段区间的字节范围。</summary>
static void PrintSrmDevice(DeviceConfig device)
{
    Console.WriteLine();
    Console.WriteLine($"  设备 {device.Id}  {device.Ip}  对接协议 {device.ProtocolType}（堆垛机 · Socket 传输）");

    if (device.SocketPorts is SocketPortsConfig ports)
    {
        Console.WriteLine($"    端口：指令 {ports.Command} / 状态 {ports.Status} / 报警 {ports.Alarm}");
        Console.WriteLine(
            $"    帧长：指令 {SrmLayout.CommandFrameLength} 字节（固定）"
            + $"，状态 {ports.StatusFrameLength} 字节，报警 {ports.AlarmFrameLength} 字节");
    }

    Console.WriteLine(
        $"    数据区：指令 [{SrmLayout.CommandAreaBase}, {SrmLayout.CommandAreaBase + SrmLayout.CommandAreaLength})"
        + $"，状态 [{SrmLayout.StatusAreaBase}, {SrmLayout.StatusAreaBase + SrmLayout.StatusAreaLength})"
        + $"，报警 [{SrmLayout.AlarmAreaBase}, {SrmLayout.AlarmAreaBase + SrmLayout.AlarmAreaLength})");

    if (device.Srm is SrmOptionsConfig srm)
    {
        Console.WriteLine(
            $"    货叉 {srm.ForkCount} 个，行走 {srm.TravelDelayMs} ms，动作 {srm.ActionDelayMs} ms");
        Console.WriteLine(
            $"    取货站台 {string.Join("、", srm.PickStations)}"
            + $"，放货站台 {string.Join("、", srm.PutStations)}");
    }
}

/// <summary>
/// 扮演 WCS 跑一遍堆垛机闭环：进程内起模拟器，用真实 TCP 连它的三个端口，
/// 按 WCS 的时序下发取货 → 放货 → 清除，并逐步打印读到的作业状态。
/// 不依赖真 WCS，是「这台堆垛机跑通了」的最小可见证据。
/// </summary>
static async Task<int> SrmAsync(string[] args)
{
    if (args.Length == 0 || args[0] != "cycle")
    {
        Console.Error.WriteLine("用法：srm cycle [--config <配置>] [--pick <站台>] [--put <站台>] [--frames]");
        return 1;
    }

    string configPath = "config/simulator.srm.json";
    string? pickStation = null;
    string? putStation = null;
    bool showFrames = false;

    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--config" or "-c" when i + 1 < args.Length:
                configPath = args[++i];
                break;
            case "--pick" when i + 1 < args.Length:
                pickStation = args[++i];
                break;
            case "--put" when i + 1 < args.Length:
                putStation = args[++i];
                break;
            case "--frames":
                showFrames = true;
                break;
            default:
                Console.Error.WriteLine($"未知参数：{args[i]}");
                return 1;
        }
    }

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"找不到配置文件：{Path.GetFullPath(configPath)}");
        return 1;
    }

    ConfigLoadResult loadResult = ConfigLoader.LoadFromFile(configPath);

    foreach (string warning in loadResult.Warnings)
    {
        Console.WriteLine($"[警告] {warning}");
    }

    if (!loadResult.IsValid)
    {
        Console.Error.WriteLine("配置校验未通过：");
        foreach (string error in loadResult.Errors)
        {
            Console.Error.WriteLine($"  - {error}");
        }

        return 1;
    }

    DeviceConfig? device = loadResult.Config.Devices.FirstOrDefault(
        static d => string.Equals(d.Protocol, "Socket", StringComparison.OrdinalIgnoreCase));

    if (device is null)
    {
        Console.Error.WriteLine("配置里没有堆垛机（protocol = \"Socket\"）设备。");
        return 1;
    }

    if (device.SocketPorts is not SocketPortsConfig ports || device.Srm is not SrmOptionsConfig srm)
    {
        Console.Error.WriteLine($"设备 {device.Id} 缺少 socketPorts 或 srm 配置。");
        return 1;
    }

    string pick = pickStation ?? srm.PickStations.FirstOrDefault() ?? string.Empty;
    string put = putStation ?? srm.PutStations.FirstOrDefault() ?? string.Empty;

    if (pick.Length == 0 || put.Length == 0)
    {
        Console.Error.WriteLine("配置里没有取货站台或放货站台，请用 --pick / --put 指定。");
        return 1;
    }

    byte pickPoint = ResolveActionPoint(srm, pick);
    byte putPoint = ResolveActionPoint(srm, put);

    Console.WriteLine(
        $"堆垛机 {device.Id}（{device.Name}）  取货 {pick}（动作点 {pickPoint}）"
        + $" → 放货 {put}（动作点 {putPoint}）");

    var frameLog = new RingBufferFrameLog();
    if (showFrames)
    {
        frameLog.EntryAdded += (_, entry) =>
            Console.WriteLine($"  {entry.DirectionText} [{entry.DeviceId}] {entry.Summary}");
    }

    await using SimulatorHost host = SimulatorHost.Create(loadResult.Config, frameLog);
    host.Log = static message => Console.WriteLine($"  · {message}");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await host.StartAsync(cts.Token);

    try
    {
        using var client = new SrmWcsProbe(device.Ip, ports);

        Console.WriteLine();
        Console.WriteLine($"1) 下发取货指令 CR_GETC={SrmCommandType.GetC}（站台 {pick}）");
        client.SendCommand(BuildCommand(SrmCommandType.GetC, taskNum: 1, forkNo: 1, actionPoint: pickPoint));
        await WaitForReportAsync(client, SrmFunctionReport.GetDone, cts.Token);

        Console.WriteLine();
        Console.WriteLine($"2) 下发放货指令 CR_PUTC={SrmCommandType.PutC}（站台 {put}）");
        client.SendCommand(BuildCommand(SrmCommandType.PutC, taskNum: 1, forkNo: 1, actionPoint: putPoint));
        await WaitForReportAsync(client, SrmFunctionReport.PutDone, cts.Token);

        Console.WriteLine();
        Console.WriteLine($"3) 下发清除指令 CR_NO_FUNC={SrmCommandType.NoFunc}");
        client.SendCommand(BuildCommand(SrmCommandType.NoFunc, taskNum: 0, forkNo: 0, actionPoint: 0));
        await WaitForReportAsync(client, SrmFunctionReport.Idle, cts.Token);

        Console.WriteLine();
        byte[] alarm = await client.ReadAlarmAsync(cts.Token);
        Console.WriteLine($"报警位图 {alarm.Length} 字节，非零 {alarm.Count(static b => b != 0)} 个。");

        Console.WriteLine();
        Console.WriteLine("闭环完成。");
        return 0;
    }
    finally
    {
        await host.StopAsync();
    }
}

static byte ResolveActionPoint(SrmOptionsConfig srm, string station)
    => srm.StationPoints.TryGetValue(station, out int point) ? (byte)point : (byte)0;

static SrmCommand BuildCommand(byte type, ushort taskNum, byte forkNo, byte actionPoint) => new(
    Fork1TaskNum: forkNo == 2 ? (ushort)0 : taskNum,
    Fork2TaskNum: forkNo == 1 ? (ushort)0 : taskNum,
    CommandType: type,
    ForkNo: forkNo,
    Fork1GoodsType: 0,
    Fork2GoodsType: 0,
    ActionPoint: actionPoint,
    Aisle: 0,
    Row: 0,
    Column: 0,
    Cell: 0,
    Level: 0,
    Depth: 1,
    FireFlag: 0,
    Face1: 0,
    Face2: 0);

/// <summary>轮询状态口，直到读到期望的作业状态字；报出沿途每一次状态变化。</summary>
static async Task WaitForReportAsync(SrmWcsProbe client, byte expected, CancellationToken cancellationToken)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));

    byte last = byte.MaxValue;

    try
    {
        while (true)
        {
            byte report = await client.ReadFunctionReportAsync(timeout.Token);

            if (report != last)
            {
                Console.WriteLine($"   读到作业状态 {report}");
                last = report;
            }

            if (report == expected)
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        throw new TimeoutException($"等待作业状态 {expected} 超时，最后读到 {last}。");
    }
}

static int PrintPath(string[] args)
{
    bool detail = false;

    foreach (string arg in args.Where(a => a.StartsWith("-", StringComparison.Ordinal)))
    {
        if (string.Equals(arg, "--detail", StringComparison.Ordinal))
        {
            detail = true;
            continue;
        }

        Console.Error.WriteLine($"未知选项：{arg}");
        Console.Error.WriteLine("用法：path <配置路径> <起点站台> <终点站台> [--detail]");
        return 1;
    }

    string[] positional = [.. args.Where(a => !a.StartsWith("-", StringComparison.Ordinal))];

    if (positional.Length < 3)
    {
        Console.Error.WriteLine("用法：path <配置路径> <起点站台> <终点站台> [--detail]");
        return 1;
    }

    string configPath = positional[0];
    string from = positional[1];
    string to = positional[2];

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"找不到配置文件：{System.IO.Path.GetFullPath(configPath)}");
        return 1;
    }

    ConfigLoadResult loadResult = ConfigLoader.LoadFromFile(configPath);

    if (!loadResult.IsValid)
    {
        foreach (string error in loadResult.Errors)
        {
            Console.Error.WriteLine($"[错误] {error}");
        }

        return 1;
    }

    if (detail)
    {
        return PrintPathDiagnosis(loadResult.Config, from, to);
    }

    bool anyFound = false;

    foreach (DeviceConfig device in loadResult.Config.Devices)
    {
        StationTopology topology = StationTopology.Build(device.Stations);

        if (topology.TryGetPath(from, to, out IReadOnlyList<string> path))
        {
            anyFound = true;

            Console.WriteLine($"{from} → {to} 共 {path.Count - 1} 跳：");
            Console.WriteLine($"  {string.Join(" → ", path)}");
            Console.WriteLine();

            for (int i = 0; i < path.Count - 1; i++)
            {
                IReadOnlyList<string> neighbors = topology.NeighborsOf(path[i]);
                Console.WriteLine(
                    $"  {path[i]} 的出边：{string.Join("、", neighbors)}");
            }
        }
        else
        {
            Console.WriteLine(
                $"{from} → {to} 无路径（设备 {device.Id}，拓扑节点 {topology.NodeCount} 个）。"
                + "货物将按任务里的目标站台直接投送。");
        }
    }

    return anyFound ? 0 : 1;
}

/// <summary>
/// 完整诊断报告：逐跳的寻路决策、时间轴与失败原因。
/// 离线跑（CLI 不启动引擎），因此不含实时状态推演。
/// </summary>
static int PrintPathDiagnosis(SimulatorConfig config, string from, string to)
{
    bool anyFound = false;

    foreach (DeviceConfig device in config.Devices)
    {
        bool hasFrom = device.Stations.Any(s => string.Equals(s.StationNo, from, StringComparison.Ordinal));
        bool hasTo = device.Stations.Any(s => string.Equals(s.StationNo, to, StringComparison.Ordinal));

        if (!hasFrom || !hasTo)
        {
            Console.WriteLine($"设备 {device.Id}：配置里没有站台 {(hasFrom ? to : from)}，跳过。");
            Console.WriteLine();
            continue;
        }

        StationTopology topology = StationTopology.Build(device.Stations);
        PathDiagnosticReport report = PathDiagnostics.Analyze(device, topology, from, to);

        Console.WriteLine(report.ToText());
        anyFound |= report.Outcome == PathDiagnosticOutcome.Found;
    }

    return anyFound ? 0 : 1;
}

/// <summary>
/// 扮演 WCS 的最小客户端：连堆垛机的三个端口，按真实时序发指令、轮询状态。
/// 走真 TCP，因此它验证的是完整链路，而不是内部方法调用。
/// </summary>
sealed class SrmWcsProbe : IDisposable
{
    private readonly TcpClient _commandClient;
    private readonly TcpClient _statusClient;
    private readonly TcpClient _alarmClient;
    private readonly NetworkStream _command;
    private readonly NetworkStream _status;
    private readonly NetworkStream _alarm;
    private readonly int _statusFrameLength;
    private readonly int _alarmFrameLength;

    public SrmWcsProbe(string ip, SocketPortsConfig ports)
    {
        _commandClient = Connect(ip, ports.Command);
        _statusClient = Connect(ip, ports.Status);
        _alarmClient = Connect(ip, ports.Alarm);

        _command = _commandClient.GetStream();
        _status = _statusClient.GetStream();
        _alarm = _alarmClient.GetStream();

        _statusFrameLength = ports.StatusFrameLength;
        _alarmFrameLength = ports.AlarmFrameLength;
    }

    public void SendCommand(in SrmCommand command)
    {
        byte[] frame = new byte[SrmLayout.CommandFrameLength];
        SrmFrames.BuildCommandFrame(command, frame);
        _command.Write(frame);
    }

    public async Task<byte> ReadFunctionReportAsync(CancellationToken cancellationToken)
    {
        byte[] frame = await PollAsync(_status, _statusFrameLength, cancellationToken);
        return frame[6];
    }

    public Task<byte[]> ReadAlarmAsync(CancellationToken cancellationToken)
        => PollAsync(_alarm, _alarmFrameLength, cancellationToken);

    public void Dispose()
    {
        _commandClient.Dispose();
        _statusClient.Dispose();
        _alarmClient.Dispose();
    }

    private static async Task<byte[]> PollAsync(
        NetworkStream stream,
        int frameLength,
        CancellationToken cancellationToken)
    {
        byte[] trigger = [SocketTcpServer.PollTriggerByte];
        await stream.WriteAsync(trigger, cancellationToken);

        byte[] frame = new byte[frameLength];
        await stream.ReadExactlyAsync(frame, cancellationToken);
        return frame;
    }

    private static TcpClient Connect(string ip, int port)
    {
        var client = new TcpClient();
        client.Connect(ip, port);
        return client;
    }
}
