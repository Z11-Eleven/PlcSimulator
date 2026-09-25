using System.Net;
using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Faults;
using PlcSimulator.Core.Frames;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Protocol.Modbus;

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
    private readonly Dictionary<string, ByteOrderPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private ModbusTcpServer? _server;
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

    /// <summary>驱动所有站台状态机的引擎。配置加载后重建，以便把故障注入器带进去。</summary>
    public SimulationEngine Engine { get; private set; } = new();

    /// <summary>当前生效的故障注入器。默认是零分配的直通实现。</summary>
    public IFaultInjector FaultInjector { get; private set; } = PassThroughFaultInjector.Instance;

    public IReadOnlyList<IPEndPoint> Endpoints => _server?.Endpoints ?? [];

    public bool IsRunning => _server?.IsRunning ?? false;

    public static SimulatorHost Create(SimulatorConfig config, IFrameLog? frameLog = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var host = new SimulatorHost(config, frameLog ?? NullFrameLog.Instance);
        host.Build();
        return host;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            return;
        }

        List<ModbusEndpointOptions> endpoints = BuildEndpoints();

        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException("没有任何启用的监听端点，请检查 server.listen 配置。");
        }

        _server = new ModbusTcpServer(
            endpoints,
            new ModbusServerOptions
            {
                MaxConnectionsPerIp = _config.Server.MaxConnectionsPerIp,
                RecordRawFrames = _config.Server.RecordRawFrames,
            },
            _frameLog,
            FaultInjector);

        await _server.StartAsync(cancellationToken).ConfigureAwait(false);

        foreach (IPEndPoint endpoint in _server.Endpoints)
        {
            Log?.Invoke($"监听 {endpoint.Address}:{endpoint.Port}");
        }

        foreach (DeviceRuntime device in _devices)
        {
            Log?.Invoke(
                $"设备 {device.Config.Id}（{device.Config.Name}）对接协议 {device.ProtocolTemplate.Name}，"
                + $"站台 {device.Stations.Count} 个（物理站台 {device.PhysicalStations.Count} 个），"
                + $"寄存器块 {device.Config.Blocks.Count} 个，"
                + $"IP {device.Config.Ip}:{device.Config.Port} 站号 {device.Config.SlaveId}");
        }

        Engine.Start();
        Log?.Invoke($"状态机引擎已启动（tick {Engine.TickInterval.TotalMilliseconds:F0} ms，{Engine.Machines.Count} 个站台）。");
    }

    public async Task StopAsync()
    {
        await Engine.StopAsync().ConfigureAwait(false);

        if (_server is null)
        {
            return;
        }

        await _server.StopAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
        _server = null;
        Log?.Invoke("服务已停止。");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

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

            if (!string.Equals(deviceConfig.Protocol, "Modbus", StringComparison.OrdinalIgnoreCase))
            {
                // S7 为预留扩展位，本期不实现。
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
                        address => DescribeAddress(device, address)));
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
