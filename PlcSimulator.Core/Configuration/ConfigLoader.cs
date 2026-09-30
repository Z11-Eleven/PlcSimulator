using System.Net;
using System.Text.Json;
using PlcSimulator.Core.Faults;
using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Core.Configuration;

public sealed record ConfigLoadResult(
    SimulatorConfig Config,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ConfigLoader
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static ConfigLoadResult LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromJson(File.ReadAllText(path));
    }

    public static ConfigLoadResult LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        SimulatorConfig config = JsonSerializer.Deserialize<SimulatorConfig>(json, SerializerOptions)
            ?? throw new InvalidDataException("配置文件内容为空或不是有效的 JSON 对象。");

        return Validate(config);
    }

    public static string Serialize(SimulatorConfig config)
        => JsonSerializer.Serialize(config, SerializerOptions);

    public static ConfigLoadResult Validate(SimulatorConfig config)
    {
        List<string> errors = [];
        List<string> warnings = [];

        HashSet<string> deviceIds = new(StringComparer.OrdinalIgnoreCase);

        // 同一个 ip:port 监听两次，要到 Start() 才炸（SocketException），这里先拦下来。
        HashSet<(string Ip, int Port)> endpoints = [];

        foreach (ListenEndpointConfig listen in config.Server.Listen)
        {
            if (listen.Enabled && !endpoints.Add((listen.Ip, listen.Port)))
            {
                errors.Add($"监听端点重复：{listen.Ip}:{listen.Port}。");
            }
        }

        // 同一端点下站号重复时，解析器按站号查只会命中第一台，后面的设备永远不可达。
        // 只对 Modbus 设备做这项检查：Socket 设备没有站号，用默认值会把它们两两误报成冲突。
        foreach (IGrouping<(string Ip, int Port), DeviceConfig> group in config.Devices
            .Where(static d => d.Enabled && IsModbusDevice(d))
            .GroupBy(static d => (d.Ip, d.Port)))
        {
            HashSet<byte> slaveIds = [];

            foreach (DeviceConfig device in group)
            {
                if (!slaveIds.Add(device.SlaveId))
                {
                    errors.Add(
                        $"端点 {group.Key.Ip}:{group.Key.Port} 上有多台设备的站号同为 {device.SlaveId}，"
                        + $"靠后的（{device.Id}）不会被访问到。");
                }
            }
        }

        foreach (DeviceConfig device in config.Devices)
        {
            string label = string.IsNullOrWhiteSpace(device.Id) ? "(未命名设备)" : device.Id;

            if (string.IsNullOrWhiteSpace(device.Id))
            {
                errors.Add("存在 id 为空的设备。");
            }
            else if (!deviceIds.Add(device.Id))
            {
                errors.Add($"设备 id 重复：{device.Id}。");
            }

            if (!IPAddress.TryParse(device.Ip, out _))
            {
                errors.Add($"设备 {label} 的 ip \"{device.Ip}\" 不是合法地址。");
            }

            // Socket 设备是另一套模型：没有站号、没有寄存器块，端口来自 socketPorts 而不是 port，
            // 因此走单独的校验分支，不落到下面的 Modbus 检查上。
            if (IsSocketDevice(device) || IsS7Device(device))
            {
                ValidateSrmDevice(device, label, errors, warnings, endpoints);
                continue;
            }

            if (device.Port is < 1 or > 65535)
            {
                errors.Add($"设备 {label} 的端口 {device.Port} 超出范围。");
            }

            if (string.Equals(device.Protocol, "Modbus", StringComparison.OrdinalIgnoreCase)
                && device.ProtocolTypeRaw?.Contains("Mobdus", StringComparison.OrdinalIgnoreCase) == true)
            {
                // WCS 源码里 RobotPLC 的确写成 Contains("Mobdus")，若数据库真是这个拼写，
                // 那条分支的实际行为与直觉相反。原样提示，不替用户纠正。
                warnings.Add(
                    $"设备 {label} 的 protocolType 含拼写 \"Mobdus\"（疑似 “Modbus” 拼错）。"
                    + "WCS 源码里该拼写走的是另一条分支，请确认数据库中的原值。");
            }

            if (!ProtocolRegistry.TryGet(device.ProtocolType, out IProtocolTemplate protocol))
            {
                errors.Add(
                    $"设备 {label} 引用了未知对接协议 \"{device.ProtocolType}\"，可用：{string.Join(", ", ProtocolRegistry.KnownNames)}。");
                continue;
            }

            ValidateStations(device, protocol, label, errors, warnings);
            ValidateBlocks(device, label, errors);
        }

        ValidateFaultRules(config, warnings);

        return new ConfigLoadResult(config, errors, warnings);
    }

    /// <summary>
    /// 故障规则的 effect 拼错时，运行期会静默按 ExceptionResponse 处理——语义悄悄变了却毫无提示。
    /// 这里在加载阶段就点出来。
    /// </summary>
    private static void ValidateFaultRules(SimulatorConfig config, List<string> warnings)
    {
        string[] known = [.. Enum.GetNames<FaultAction>(), "HeartbeatStop"];

        foreach (FaultRuleConfig rule in config.FaultInjection.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Effect)
                || known.Contains(rule.Effect.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string label = string.IsNullOrWhiteSpace(rule.Name) ? "(未命名规则)" : rule.Name;
            warnings.Add(
                $"故障规则 {label} 的 effect \"{rule.Effect}\" 无法识别，运行期会按 ExceptionResponse 处理。"
                + $"可用：{string.Join("、", known)}。");
        }
    }

    private static bool IsModbusDevice(DeviceConfig device)
        => string.Equals(device.Protocol, "Modbus", StringComparison.OrdinalIgnoreCase);

    private static bool IsSocketDevice(DeviceConfig device)
        => string.Equals(device.Protocol, "Socket", StringComparison.OrdinalIgnoreCase);

    private static bool IsS7Device(DeviceConfig device)
        => string.Equals(device.Protocol, "S7", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 堆垛机（Socket 传输）的专项校验。它不查站台与寄存器块，
    /// 查的是三个端口、协议别名与状态帧容量。
    /// </summary>
    private static void ValidateSrmDevice(
        DeviceConfig device,
        string label,
        List<string> errors,
        List<string> warnings,
        HashSet<(string Ip, int Port)> endpoints)
    {
        if (IsS7Device(device))
        {
            ValidateS7Device(device, label, errors, endpoints);
            ValidateSrmOptions(device, label, errors, warnings);
            return;
        }

        SocketPortsConfig? ports = device.SocketPorts;
        if (ports is null)
        {
            errors.Add($"设备 {label} 的 protocol 是 Socket，但缺少 socketPorts 配置（三个端口）。");
            return;
        }

        (string Name, int Port)[] rolePorts =
        [
            ("command", ports.Command),
            ("status", ports.Status),
            ("alarm", ports.Alarm),
        ];

        foreach ((string name, int port) in rolePorts)
        {
            if (port is < 1 or > 65535)
            {
                errors.Add($"设备 {label} 的 socketPorts.{name}={port} 超出端口范围。");
            }
        }

        if (ports.Command == ports.Status || ports.Status == ports.Alarm || ports.Command == ports.Alarm)
        {
            errors.Add(
                $"设备 {label} 的三个 Socket 端口必须互不相同，"
                + $"当前为 {ports.Command}/{ports.Status}/{ports.Alarm}。");
        }

        foreach ((string name, int port) in rolePorts)
        {
            // 端口被占时 TcpListener.Start() 会抛 SocketException，八台设备全配 127.0.0.1 时
            // 现场看到的是"启动就崩"，这里在 check 阶段就点明该怎么改。
            if (!endpoints.Add((device.Ip, port)))
            {
                errors.Add(
                    $"Socket 端点重复：{device.Ip}:{port}（设备 {label} 的 {name} 口）。"
                    + "多台堆垛机请各用一个回环地址（127.0.0.1、127.0.0.2 …），"
                    + "并同步改数据库 wcs_equipmentinfo.RIPADDR。");
            }
        }

        if (ports.StatusFrameLength < SrmLayout.MinStatusFrameLength)
        {
            errors.Add(
                $"设备 {label} 的 statusFrameLength={ports.StatusFrameLength} 小于 "
                + $"{SrmLayout.MinStatusFrameLength}，装不下状态字段。");
        }

        if (ports.AlarmFrameLength < 1)
        {
            errors.Add($"设备 {label} 的 alarmFrameLength 必须为正数。");
        }

        ValidateSrmOptions(device, label, errors, warnings);
    }

    private static void ValidateS7Device(DeviceConfig device, string label, List<string> errors,
        HashSet<(string Ip, int Port)> endpoints)
    {
        S7OptionsConfig? s7 = device.S7;
        if (s7 is null)
        {
            errors.Add($"设备 {label} 的 protocol 是 S7，但缺少 s7 配置。");
            return;
        }

        if (s7.Port is < 1 or > 65535)
        {
            errors.Add($"设备 {label} 的 s7.port 超出端口范围。");
        }
        if (!endpoints.Add((device.Ip, s7.Port)))
        {
            errors.Add($"S7 端点重复：{device.Ip}:{s7.Port}（设备 {label}）。多台堆垛机请使用不同 IP 或端口。");
        }
        if (s7.MaxPduLength is < 240 or > 960)
        {
            errors.Add($"设备 {label} 的 s7.maxPduLength 必须为 240～960。");
        }
        if (s7.CommandPayloadOffset is not (0 or 2))
        {
            errors.Add($"设备 {label} 的 s7.commandPayloadOffset 只能为 0 或 2。");
        }
        if (s7.StatusLength is < SrmLayout.MinStatusFrameLength or > SrmLayout.StatusAreaLength
            || s7.AlarmLength is < 1 or > SrmLayout.AlarmAreaLength)
        {
            errors.Add($"设备 {label} 的 s7.statusLength 必须为 23～171，alarmLength 必须为 1～100。");
        }

        (string Name, S7DbRegionConfig? Region, int Length)[] regions =
        [
            ("command", s7.Command, s7.CommandLength),
            ("status", s7.Status, s7.StatusLength),
            ("alarm", s7.Alarm, s7.AlarmLength),
        ];
        for (int i = 0; i < regions.Length; i++)
        {
            var current = regions[i];
            if (current.Region is null || current.Region.DbNumber is < 1 or > 65535
                || current.Region.ByteOffset < 0 || current.Region.ByteOffset > 65536 - current.Length)
            {
                errors.Add($"设备 {label} 的 s7.{current.Name} DB 编号或字节范围无效。");
                continue;
            }
            for (int j = 0; j < i; j++)
            {
                var previous = regions[j];
                if (previous.Region is not null && current.Region.DbNumber == previous.Region.DbNumber
                    && current.Region.ByteOffset < (long)previous.Region.ByteOffset + previous.Length
                    && previous.Region.ByteOffset < (long)current.Region.ByteOffset + current.Length)
                {
                    errors.Add($"设备 {label} 的 s7.{previous.Name} 与 s7.{current.Name} 在同一个 DB 中重叠。");
                }
            }
        }
    }

    private static void ValidateSrmOptions(DeviceConfig device, string label, List<string> errors, List<string> warnings)
    {
        if (!SrmLayout.ProtocolTypeAliases.Contains(device.ProtocolType, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(
                $"设备 {label} 的 protocolType \"{device.ProtocolType}\" 不是已知的堆垛机对接协议，"
                + $"可用：{string.Join(", ", SrmLayout.ProtocolTypeAliases)}。");
        }

        SrmOptionsConfig? srm = device.Srm;
        if (srm is null)
        {
            errors.Add($"设备 {label} 的 protocol 是 {device.Protocol}，但缺少 srm 配置。");
            return;
        }

        if (srm.TravelDelayMs < 0 || srm.ActionDelayMs < 0 || srm.JitterMs < 0)
        {
            errors.Add($"设备 {label} 的 srm 延时参数不能为负。");
        }

        if (srm.ForkCount is < 1 or > 2)
        {
            errors.Add($"设备 {label} 的 srm.forkCount={srm.ForkCount} 只能是 1 或 2。");
        }

        foreach (string station in srm.PickStations.Concat(srm.PutStations))
        {
            if (!srm.StationPoints.ContainsKey(station))
            {
                warnings.Add(
                    $"设备 {label} 的站台 {station} 出现在 pickStations/putStations 中，"
                    + "但 stationPoints 里没有它的动作点，下发指令时会对不上。");
            }
        }
    }

    private static void ValidateStations(
        DeviceConfig device,
        IProtocolTemplate protocol,
        string label,
        List<string> errors,
        List<string> warnings)
    {
        List<(int Start, int End, string StationNo)> ranges = [];

        foreach (StationConfig station in device.Stations)
        {
            string stationLabel = string.IsNullOrWhiteSpace(station.StationNo) ? "(未编号站台)" : station.StationNo;

            if (string.IsNullOrWhiteSpace(station.StationNo))
            {
                errors.Add($"设备 {label} 存在 stationNo 为空的站台。");
            }

            if (station.LengthBytes < protocol.MinBlockBytes)
            {
                errors.Add(
                    $"设备 {label} 站台 {stationLabel} 的 lengthBytes={station.LengthBytes} 小于 "
                    + $"对接协议 {protocol.Name} 要求的 {protocol.MinBlockBytes} 字节，WCS 解析时会越界。");
            }

            if (station.ByteOffset % 2 != 0)
            {
                warnings.Add(
                    $"设备 {label} 站台 {stationLabel} 的 byteOffset={station.ByteOffset} 是奇数，"
                    + "换算成寄存器地址会取整丢失，请核对数据库 value 字段。");
            }

            ranges.Add((station.ByteOffset, station.ByteOffset + station.LengthBytes, stationLabel));
        }

        ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        int sharedRangeCount = 0;

        for (int i = 1; i < ranges.Count; i++)
        {
            if (ranges[i].Start >= ranges[i - 1].End)
            {
                continue;
            }

            bool identical = ranges[i].Start == ranges[i - 1].Start && ranges[i].End == ranges[i - 1].End;
            if (identical)
            {
                // 成对站台共用同一段寄存器是现场的正常用法（同一工位的两个方向/货叉），
                // 它们会读到相同的数据，WCS 本身也是这样解析的。
                sharedRangeCount++;
                continue;
            }

            errors.Add(
                $"设备 {label} 的站台 {ranges[i - 1].StationNo} 与 {ranges[i].StationNo} "
                + $"地址区间部分重叠：[{ranges[i - 1].Start},{ranges[i - 1].End}) 与 [{ranges[i].Start},{ranges[i].End})。"
                + "部分重叠会让两个站台的字段互相错位。");
        }

        if (sharedRangeCount > 0)
        {
            warnings.Add(
                $"设备 {label} 有 {sharedRangeCount} 组站台共用同一段地址区间，"
                + "它们会被当作同一个物理站台共用一个状态机（WCS 用多个图标显示同一个站台的正常用法）。"
                + "如果这不符合预期，请核对数据库 value 字段。");
        }
    }

    private static void ValidateBlocks(DeviceConfig device, string label, List<string> errors)
    {
        foreach (RegisterBlockConfig block in device.Blocks)
        {
            if (block.LengthBytes <= 0)
            {
                errors.Add($"设备 {label} 的寄存器块 {block.Group} 长度必须为正数。");
            }
        }

        foreach (StationConfig station in device.Stations)
        {
            bool covered = device.Blocks.Exists(
                b => station.ByteOffset >= b.BaseByteOffset
                    && station.ByteOffset + station.LengthBytes <= b.BaseByteOffset + b.LengthBytes);

            if (!covered)
            {
                errors.Add(
                    $"设备 {label} 站台 {station.StationNo} 的地址区间 "
                    + $"[{station.ByteOffset},{station.ByteOffset + station.LengthBytes}) 没有被任何寄存器块覆盖。");
            }
        }
    }
}
