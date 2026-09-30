using System.Net;
using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Faults;
using PlcSimulator.Core.Frames;
using PlcSimulator.Core.Protocol;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices;
using PlcSimulator.Devices.Srm;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Protocol.Modbus;
using PlcSimulator.Protocol.Socket;
using PlcSimulator.Protocol.S7;

namespace PlcSimulator.Hosting;

/// <summary>
/// 把配置、寄存器数据区与协议服务端组装起来并管理生命周期。
/// 命令行宿主与 WinForms 调试台共用它。
/// </summary>
public sealed class SimulatorHost : IAsyncDisposable
{
    private readonly SimulatorConfig _config;
    private readonly IFrameLog _frameLog;
    private readonly List<DeviceRuntime> _devices = [];
    private readonly List<SrmDeviceRuntime> _srmDevices = [];
    private readonly List<IProtocolServer> _servers = [];
    private readonly Dictionary<string, ByteOrderPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private Action<string>? _log;

    private SimulatorHost(SimulatorConfig config, IFrameLog frameLog)
    {
        _config = config;
        _frameLog = frameLog;
    }

    /// <summary>
    /// 日志出口，由调用方决定输出到控制台、文件还是 GUI 面板。
    /// 设置时同步接到引擎上——引擎自己的推进异常也要从同一个出口出去，否则那类异常没人看得见。
    /// </summary>
    public Action<string>? Log
    {
        get => _log;
        set
        {
            _log = value;
            Engine.Log = value;
        }
    }

    public IReadOnlyList<DeviceRuntime> Devices => _devices;

    /// <summary>堆垛机设备。与 Modbus 的站台设备分开存放——两者的数据模型不同。</summary>
    public IReadOnlyList<SrmDeviceRuntime> SrmDevices => _srmDevices;

    /// <summary>驱动所有站台状态机的引擎。配置加载后重建，以便把故障注入器带进去。</summary>
    public SimulationEngine Engine { get; private set; } = new();

    /// <summary>当前生效的故障注入器。默认是零分配的直通实现。</summary>
    public IFaultInjector FaultInjector { get; private set; } = PassThroughFaultInjector.Instance;

    public IReadOnlyList<IPEndPoint> Endpoints => [.. _servers.SelectMany(static s => s.Endpoints)];

    public bool IsRunning => _servers.Count > 0;

    public static SimulatorHost Create(SimulatorConfig config, IFrameLog? frameLog = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var host = new SimulatorHost(config, frameLog ?? NullFrameLog.Instance);
        host.Build();
        return host;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_servers.Count > 0)
        {
            return;
        }

        try
        {
            await StartModbusAsync(cancellationToken).ConfigureAwait(false);
            await StartSocketAsync(cancellationToken).ConfigureAwait(false);
            await StartS7Async(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }

        if (_servers.Count == 0)
        {
            // 两类服务端都起不来才是真错误：只跑堆垛机的配置里 server.listen 本就是空的。
            throw new InvalidOperationException(
                "没有任何启用的监听端点：请检查 server.listen、Socket 的 socketPorts 或 S7 的 s7 配置。");
        }

        foreach (IProtocolServer server in _servers)
        {
            foreach (IPEndPoint endpoint in server.Endpoints)
            {
                Log?.Invoke($"监听 {endpoint.Address}:{endpoint.Port}（{server.ProtocolName}）");
            }
        }

        foreach (DeviceRuntime device in _devices)
        {
            Log?.Invoke(
                $"设备 {device.Config.Id}（{device.Config.Name}）对接协议 {device.ProtocolTemplate.Name}，"
                + $"站台 {device.Stations.Count} 个（物理站台 {device.PhysicalStations.Count} 个），"
                + $"寄存器块 {device.Config.Blocks.Count} 个，"
                + $"IP {device.Config.Ip}:{device.Config.Port} 站号 {device.Config.SlaveId}");
        }

        foreach (SrmDeviceRuntime device in _srmDevices)
        {
            if (device.Config.S7 is S7OptionsConfig s7 && device.Config.Protocol.Equals("S7", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke($"设备 {device.DeviceId}（{device.Name}）S7 {device.Config.Ip}:{s7.Port}，"
                    + $"指令 DB{s7.Command.DbNumber}.{s7.Command.ByteOffset}，"
                    + $"状态 DB{s7.Status.DbNumber}.{s7.Status.ByteOffset}，报警 DB{s7.Alarm.DbNumber}.{s7.Alarm.ByteOffset}");
                continue;
            }
            Log?.Invoke(
                $"设备 {device.DeviceId}（{device.Name}）对接协议 {device.Config.ProtocolType}，"
                + $"货叉 {device.Options.ForkCount} 个，"
                + $"IP {device.Config.Ip} 端口 指令{device.Ports.Command}"
                + $"/状态{device.Ports.Status}/报警{device.Ports.Alarm}");
        }

        Engine.Start();
        Log?.Invoke(
            $"状态机引擎已启动（tick {Engine.TickInterval.TotalMilliseconds:F0} ms，"
            + $"{Engine.Machines.Count} 个站台，{Engine.DeviceMachines.Count} 台设备）。");
    }

    public async Task StopAsync()
    {
        await Engine.StopAsync().ConfigureAwait(false);

        if (_servers.Count == 0)
        {
            return;
        }

        foreach (IProtocolServer server in _servers)
        {
            await server.StopAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
        }

        _servers.Clear();
        Log?.Invoke("服务已停止。");
    }

    private async Task StartModbusAsync(CancellationToken cancellationToken)
    {
        List<ModbusEndpointOptions> endpoints = BuildEndpoints();
        if (endpoints.Count == 0)
        {
            return;
        }

        var server = new ModbusTcpServer(
            endpoints,
            new ModbusServerOptions
            {
                MaxConnectionsPerIp = _config.Server.MaxConnectionsPerIp,
                RecordRawFrames = _config.Server.RecordRawFrames,
            },
            _frameLog,
            FaultInjector);

        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _servers.Add(server);
    }

    private async Task StartSocketAsync(CancellationToken cancellationToken)
    {
        List<SocketDeviceEndpoint> endpoints = BuildSocketEndpoints();
        if (endpoints.Count == 0)
        {
            return;
        }

        var server = new SocketTcpServer(
            endpoints,
            new SocketServerOptions
            {
                MaxConnectionsPerIp = _config.Server.MaxConnectionsPerIp,
                RecordRawFrames = _config.Server.RecordRawFrames,
            },
            _frameLog);

        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _servers.Add(server);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task StartS7Async(CancellationToken cancellationToken)
    {
        var endpoints = _srmDevices
            .Where(d => d.Config.Protocol.Equals("S7", StringComparison.OrdinalIgnoreCase))
            .Select(d => new S7DeviceEndpoint(IPAddress.Parse(d.Config.Ip), d.Config.S7!.Port, d, d.Config.S7.MaxPduLength))
            .ToArray();
        if (endpoints.Length == 0)
        {
            return;
        }
        var server = new S7TcpServer(endpoints, _config.Server.MaxConnectionsPerIp,
            _config.Server.RecordRawFrames, _frameLog);
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        _servers.Add(server);
    }

    /// <summary>
    /// 把所有站台复位成配置初值。
    /// <para>
    /// 走引擎线程逐台复位：直接写寄存器会与同一时刻的 tick 撞车，
    /// 而且只搬寄存器、不搬状态机（在途货物、已完成任务号都还留着），
    /// 与单站「复位」的语义对不上。
    /// </para>
    /// </summary>
    public void ResetAll()
    {
        foreach (ConveyorStationMachine machine in Engine.Machines)
        {
            Engine.Enqueue(machine.Reset);
        }

        foreach (IDeviceMachine machine in Engine.DeviceMachines)
        {
            Engine.Enqueue(machine.Reset);
        }
    }

    private void Build()
    {
        BuildFaultInjector();
        Engine = new SimulationEngine(faultInjector: FaultInjector);

        // 状态机流转也输出到日志，便于不带 GUI 时观察流程走到哪一步。
        // 订阅放在 Build 里（而不是 StartAsync），避免反复启停时重复叠加。
        Engine.StationChanged += (_, snapshot) => Log?.Invoke(
            $"[{snapshot.StationNo}] {snapshot.StateText} - {snapshot.LastEvent}");

        BuildPolicies();

        foreach (DeviceConfig deviceConfig in _config.Devices)
        {
            if (!deviceConfig.Enabled)
            {
                Log?.Invoke($"设备 {deviceConfig.Id} 已禁用，跳过。");
                continue;
            }

            if (string.Equals(deviceConfig.Protocol, "Socket", StringComparison.OrdinalIgnoreCase)
                || string.Equals(deviceConfig.Protocol, "S7", StringComparison.OrdinalIgnoreCase))
            {
                SrmDeviceRuntime srmRuntime = SrmDeviceRuntime.Create(deviceConfig);
                var machine = new SrmMachine(srmRuntime);

                machine.StateChanged += (_, snapshot) => Log?.Invoke(
                    $"[{snapshot.DeviceId}] {snapshot.LastEvent}");

                Engine.AddDeviceMachine(machine);
                _srmDevices.Add(srmRuntime);
                continue;
            }

            if (!string.Equals(deviceConfig.Protocol, "Modbus", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke($"设备 {deviceConfig.Id} 的协议 {deviceConfig.Protocol} 本期未实现，跳过。");
                continue;
            }

            IProtocolTemplate protocol = ProtocolRegistry.Get(deviceConfig.ProtocolType);

            var runtime = new DeviceRuntime(
                deviceConfig,
                protocol,
                Resolve(deviceConfig.ReadByteOrderPolicy),
                Resolve(deviceConfig.SingleFieldWriteByteOrderPolicy));

            StationInitializer.Apply(runtime);
            Engine.RegisterDevice(runtime);
            _devices.Add(runtime);
        }
    }

    private void BuildFaultInjector()
    {
        FaultInjectionConfig faultConfig = _config.FaultInjection;

        // 默认走零分配的直通实现；只有开启且确实有启用规则时才替换。
        if (!faultConfig.Enabled || !faultConfig.Rules.Any(static r => r.Enabled))
        {
            FaultInjector = PassThroughFaultInjector.Instance;
            return;
        }

        FaultInjector = new ConfiguredFaultInjector(faultConfig);
        Log?.Invoke($"故障注入已启用，共 {FaultInjector.Rules.Count} 条规则。");
    }

    private void BuildPolicies()
    {
        foreach ((string name, ByteOrderPolicyConfig policyConfig) in _config.ByteOrder.Policies)
        {
            _policies[name] = new ByteOrderPolicy
            {
                Name = name,
                WordByteOrder = Enum.TryParse(policyConfig.WordByteOrder, ignoreCase: true, out WordByteOrder order)
                    ? order
                    : WordByteOrder.BigEndian,
                SwapFromByteOffset = policyConfig.SwapFromByteOffset,
                ExtraPairUnswapRanges = policyConfig.ExtraPairUnswapRanges,
            };
        }
    }

    private ByteOrderPolicy Resolve(string? policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return ByteOrderPolicy.Identity;
        }

        if (_policies.TryGetValue(policyName, out ByteOrderPolicy? policy) && policy is not null)
        {
            return policy;
        }

        throw new InvalidDataException($"引用了未定义的字节序策略 \"{policyName}\"。");
    }

    /// <summary>
    /// 把寄存器地址翻译成「站台.字段」，并标出该字段是否落在「约定需要高低位转换」的区间内。
    /// 排查「写进去的值不对」时，这一条能直接指出字节序约定与实际写入是否一致。
    /// </summary>
    private static AddressHint? DescribeAddress(DeviceRuntime device, ushort registerAddress)
    {
        int byteStart = registerAddress * 2;

        foreach (StationRuntime station in device.Stations)
        {
            int stationEnd = station.ByteOffset + station.Config.LengthBytes;
            if (byteStart < station.ByteOffset || byteStart >= stationEnd)
            {
                continue;
            }

            int relative = byteStart - station.ByteOffset;

            foreach (FieldDescriptor field in station.EnumerateFields())
            {
                if (relative >= field.ByteOffset && relative < field.EndByteOffset)
                {
                    // 只在写入侧约定本身含字节交换时才提示释义——默认（恒等）不产生提示。
                    return new AddressHint(
                        $"{station.StationNo}.{field.Name}",
                        device.IncomingPolicy.CoversField(field));
                }
            }

            return new AddressHint($"{station.StationNo}@+{relative}", false);
        }

        return null;
    }

    private List<SocketDeviceEndpoint> BuildSocketEndpoints()
    {
        List<SocketDeviceEndpoint> endpoints = [];

        foreach (SrmDeviceRuntime device in _srmDevices)
        {
            if (!device.Config.Protocol.Equals("Socket", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!IPAddress.TryParse(device.Config.Ip, out IPAddress? address) || address is null)
            {
                throw new InvalidDataException($"设备 {device.DeviceId} 的 ip \"{device.Config.Ip}\" 不是合法 IP。");
            }

            endpoints.Add(new SocketDeviceEndpoint(
                address,
                device.Ports.Command,
                device.Ports.Status,
                device.Ports.Alarm,
                device));
        }

        return endpoints;
    }

    /// <summary>WCS 显式写零任务号表示清空站台，不能靠当前寄存器为零判断：离站也会自行清零。</summary>
    private void OnRegistersWritten(DeviceRuntime device, ushort address, byte[] payload)
    {
        if (!device.ProtocolTemplate.TryGetWriteField("tasknum", out FieldDescriptor taskField))
        {
            return;
        }

        int byteStart = address * 2;
        foreach (StationRuntime station in device.PhysicalStations)
        {
            int offset = station.ByteOffset + taskField.ByteOffset - byteStart;
            if (offset < 0 || offset + taskField.SizeInBytes > payload.Length
                || payload[offset] != 0 || payload[offset + 1] != 0)
            {
                continue;
            }

            // 通信线程只投递操作，状态机及在途缓存由引擎线程统一清除。
            Engine.Enqueue(() =>
            {
                // 若 WCS 随后已下发新任务，不能让排队的清空删除新任务。
                if (station.ReadIncomingU16("tasknum") == 0)
                {
                    Engine.Find(station.StationNo)?.ClearFromWcs();
                }
            });
        }
    }

    private List<ModbusEndpointOptions> BuildEndpoints()
    {
        List<ModbusEndpointOptions> endpoints = [];

        foreach (ListenEndpointConfig listen in _config.Server.Listen)
        {
            if (!listen.Enabled)
            {
                continue;
            }

            if (!IPAddress.TryParse(listen.Ip, out IPAddress? parsedAddress) || parsedAddress is null)
            {
                throw new InvalidDataException($"监听地址 \"{listen.Ip}\" 不是合法 IP。");
            }

            IPAddress address = parsedAddress;

            bool anyAddress = address.Equals(IPAddress.Any);
            List<ModbusDeviceBinding> bindings = [];

            foreach (DeviceRuntime device in _devices)
            {
                if (anyAddress || device.Config.Ip == listen.Ip)
                {
                    bindings.Add(new ModbusDeviceBinding(
                        device.Config.Id,
                        device.Config.SlaveId,
                        device.Space,
                        address => DescribeAddress(device, address),
                        (address, payload) => OnRegistersWritten(device, address, payload)));
                }
            }

            if (bindings.Count == 0)
            {
                Log?.Invoke($"监听 {listen.Ip}:{listen.Port} 没有匹配的设备，跳过。");
                continue;
            }

            endpoints.Add(new ModbusEndpointOptions(
                address,
                listen.Port,
                new StaticDeviceResolver(bindings, _config.Server.AcceptAnySlaveId)));
        }

        return endpoints;
    }
}
