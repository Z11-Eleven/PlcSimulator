using System.Text;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Frames;
using PlcSimulator.Devices.Topology;
using PlcSimulator.Hosting;

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
        Console.WriteLine(
            $"配置有效：{loadResult.Config.Devices.Count} 台设备，"
            + $"{loadResult.Config.Devices.Sum(static d => d.Stations.Count)} 个站台。");

        Console.WriteLine();
        Console.WriteLine("地址映射（WCS 发出的读写地址必须落在寄存器块范围内）：");

        foreach (DeviceConfig device in loadResult.Config.Devices)
        {
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
