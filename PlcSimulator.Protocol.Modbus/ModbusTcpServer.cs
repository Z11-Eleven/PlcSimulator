using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PlcSimulator.Core;
using PlcSimulator.Core.Faults;
using PlcSimulator.Core.Frames;
using PlcSimulator.Core.Protocol;

namespace PlcSimulator.Protocol.Modbus;

/// <summary>一个监听端点及其承载的设备。WCS 把端口硬编码为 502，同机多设备靠不同回环 IP 区分。</summary>
public sealed record ModbusEndpointOptions(IPAddress Address, int Port, IModbusDeviceResolver Resolver);

public sealed class ModbusServerOptions
{
    public int MaxConnectionsPerIp { get; init; } = 8;

    public bool NoDelay { get; init; } = true;

    public bool RecordRawFrames { get; init; } = true;

    /// <summary>
    /// 一条连接多少毫秒没收到任何一帧就关掉。WCS 的闭源 DLL 若卡住不关旧 socket，
    /// 僵尸连接会占满 <see cref="MaxConnectionsPerIp"/>，之后 WCS 的重连会被拒，
    /// 表象就成了"模拟器不服务了"。WCS 正常 1 秒轮询一次，5 分钟足够宽松。
    /// </summary>
    public int IdleTimeoutMs { get; init; } = 300_000;
}

/// <summary>
/// 自实现的 Modbus TCP 服务端。之所以不用现成库，是因为本模拟器的核心价值在于
/// 精确控制异常响应、静默不响应、延时与断连，以及在最底层拿到原始字节做字节序校准
/// —— 这些恰好是通用库封装掉的部分。
/// </summary>
public sealed class ModbusTcpServer : IProtocolServer
{
    private readonly IReadOnlyList<ModbusEndpointOptions> _endpoints;
    private readonly ModbusServerOptions _options;
    private readonly IFrameLog _frameLog;
    private readonly IFaultInjector _faultInjector;
    private readonly List<ListenerContext> _listeners = [];
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new(StringComparer.Ordinal);
    private readonly List<Task> _backgroundTasks = [];
    private CancellationTokenSource? _cts;
    private long _connectionCounter;

    public ModbusTcpServer(
        IEnumerable<ModbusEndpointOptions> endpoints,
        ModbusServerOptions? options = null,
        IFrameLog? frameLog = null,
        IFaultInjector? faultInjector = null)
    {
        _endpoints = [.. endpoints];
        _options = options ?? new ModbusServerOptions();
        _frameLog = frameLog ?? NullFrameLog.Instance;
        _faultInjector = faultInjector ?? PassThroughFaultInjector.Instance;
        Endpoints = [.. _endpoints.Select(static e => new IPEndPoint(e.Address, e.Port))];
    }

    public string ProtocolName => "Modbus";

    public IReadOnlyList<IPEndPoint> Endpoints { get; }

    public bool IsRunning => _cts is not null;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("服务端已经启动。");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (ModbusEndpointOptions endpoint in _endpoints)
        {
            // 独占绑定，不设 SO_REUSEADDR：Windows 下那个选项允许别的进程绑同一地址，
            // 两个实例会分摊 TCP 流量，表象是"改了配置/代码却不生效、报文时有时无"。
            // 端口被占时让 Start() 直接抛 SocketException，比默默双绑好查得多。
            var listener = new TcpListener(endpoint.Address, endpoint.Port);
            listener.Start();

            var context = new ListenerContext(
                listener,
                endpoint.Resolver,
                new ModbusRequestProcessor(endpoint.Resolver),
                (IPEndPoint)listener.LocalEndpoint);

            _listeners.Add(context);
            _backgroundTasks.Add(Task.Run(() => AcceptLoopAsync(context, _cts.Token), CancellationToken.None));
        }

        return Task.CompletedTask;
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

        foreach (ListenerContext context in _listeners)
        {
            try
            {
                context.Listener.Stop();
            }
            catch (SocketException)
            {
                // 监听器已关闭。
            }
        }

        try
        {
            await Task.WhenAll(_backgroundTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期的取消。
        }

        _backgroundTasks.Clear();
        _listeners.Clear();
        _connectionsPerIp.Clear();
        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(ListenerContext context, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await context.Listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            string remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
            int active = _connectionsPerIp.AddOrUpdate(remoteIp, 1, static (_, current) => current + 1);

            if (active > _options.MaxConnectionsPerIp)
            {
                // 接受后立即关闭而不是拒绝 accept，否则客户端的重连逻辑拿不到明确的失败信号。
                // 这条路径要留日志：否则"连不上"会表现为模拟器无声无息地不服务。
                LogControl(context, null, $"{remoteIp} 的连接数已达上限 {_options.MaxConnectionsPerIp}，关闭新连接");
                _connectionsPerIp.AddOrUpdate(remoteIp, 0, static (_, current) => Math.Max(0, current - 1));
                client.Dispose();
                continue;
            }

            string connectionId = $"#{Interlocked.Increment(ref _connectionCounter)}";
            _ = Task.Run(
                () => HandleConnectionAsync(client, context, connectionId, remoteIp, cancellationToken),
                CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(
        TcpClient client,
        ListenerContext context,
        string connectionId,
        string remoteIp,
        CancellationToken cancellationToken)
    {
        // 每条连接自己的空闲令牌：每收到一帧就重置，卡死不发的连接会自己让出配额。
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ResetIdleTimeout(idleCts);

        try
        {
            if (_options.NoDelay)
            {
                client.NoDelay = true;
            }

            using (client)
            {
                NetworkStream stream = client.GetStream();
                byte[] header = new byte[ModbusFrames.MbapHeaderLength];
                var connectionState = new ConnectionState();

                while (!idleCts.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, idleCts.Token).ConfigureAwait(false);
                    ResetIdleTimeout(idleCts);

                    if (!ModbusFrames.TryReadHeader(header, out ushort transactionId, out ushort protocolId, out ushort length))
                    {
                        break;
                    }

                    if (protocolId != 0 || length < ModbusFrames.MinLengthField)
                    {
                        LogControl(context, connectionId, $"非法 MBAP 头：protocolId={protocolId} length={length}，关闭连接");
                        break;
                    }

                    int pduLength = length - 1;
                    byte[] pdu = new byte[pduLength];
                    await stream.ReadExactlyAsync(pdu, idleCts.Token).ConfigureAwait(false);

                    byte unitId = header[6];
                    FrameOutcome outcome = await ProcessFrameAsync(
                        context,
                        connectionId,
                        remoteIp,
                        connectionState,
                        transactionId,
                        unitId,
                        pdu,
                        cancellationToken).ConfigureAwait(false);

                    if (outcome.CloseConnection)
                    {
                        break;
                    }

                    if (outcome.Response is null)
                    {
                        // 故障注入的静默丢弃：不回帧，让客户端自己超时，连接保持。
                        continue;
                    }

                    await stream.WriteAsync(outcome.Response, idleCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // 对端正常关闭。
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogControl(context, connectionId, $"空闲超过 {_options.IdleTimeoutMs / 1000} 秒，关闭连接");
        }
        catch (OperationCanceledException)
        {
            // 服务停止。
        }
        catch (IOException)
        {
            // 连接被重置。
        }
        catch (SocketException)
        {
            // 连接被重置。
        }
        catch (Exception ex)
        {
            // 兜底：寄存器层或故障注入器抛出的意外异常不能让连接无声消失、无人知晓。
            LogControl(context, connectionId, $"连接处理异常：{ex.Message}");
        }
        finally
        {
            _connectionsPerIp.AddOrUpdate(remoteIp, 0, static (_, current) => Math.Max(0, current - 1));
        }
    }

    /// <summary>把空闲计时重新开始。CancelAfter 可以反复调用，等于重置。</summary>
    private void ResetIdleTimeout(CancellationTokenSource idleCts)
    {
        if (_options.IdleTimeoutMs > 0)
        {
            idleCts.CancelAfter(_options.IdleTimeoutMs);
        }
    }

    private async Task<FrameOutcome> ProcessFrameAsync(
        ListenerContext context,
        string connectionId,
        string remoteIp,
        ConnectionState connectionState,
        ushort transactionId,
        byte unitId,
        byte[] pdu,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        if (!ModbusFrames.TryDecodePdu(transactionId, unitId, pdu, out ModbusRequest request, out ModbusExceptionCode error))
        {
            var rejectedFunction = pdu.Length > 0
                ? (ModbusFunctionCode)pdu[0]
                : ModbusFunctionCode.ReadHoldingRegisters;

            Log(context, connectionId, null, FrameDirection.Received,
                $"FC=0x{(byte)rejectedFunction:X2} 解码失败 → 异常 0x{(byte)error:X2}", pdu, startedAt);

            return new FrameOutcome(
                ModbusFrames.BuildException(transactionId, unitId, rejectedFunction, error),
                false);
        }

        ModbusDeviceBinding? device = context.Resolver.Find(unitId);
        connectionState.Observe(request);

        // 故障门位于「解码之后、访问数据区之前」：既能做精细匹配，
        // 又保证故障永远不会污染寄存器状态。
        FaultDecision decision = EvaluateFault(device, connectionId, remoteIp, connectionState.Role, request);

        switch (decision.Action)
        {
            case FaultAction.SilentDrop:
                Log(context, connectionId, device?.DeviceId, FrameDirection.Received,
                    $"{Describe(request, device)} → 故障注入「{decision.RuleName}」：静默不响应", pdu, startedAt);
                return new FrameOutcome(null, false);

            case FaultAction.CloseConnection:
                Log(context, connectionId, device?.DeviceId, FrameDirection.Received,
                    $"{Describe(request, device)} → 故障注入「{decision.RuleName}」：断开连接", pdu, startedAt);
                return new FrameOutcome(null, true);

            case FaultAction.ExceptionResponse:
                Log(context, connectionId, device?.DeviceId, FrameDirection.Received,
                    $"{Describe(request, device)} → 故障注入「{decision.RuleName}」：异常 0x{decision.ExceptionCode:X2}", pdu, startedAt);
                return new FrameOutcome(
                    ModbusFrames.BuildException(
                        transactionId, unitId, request.FunctionCode, (ModbusExceptionCode)decision.ExceptionCode),
                    false);

            case FaultAction.DelayThenException:
                await Task.Delay(decision.DelayMs, cancellationToken).ConfigureAwait(false);
                Log(context, connectionId, device?.DeviceId, FrameDirection.Received,
                    $"{Describe(request, device)} → 故障注入「{decision.RuleName}」：延时 {decision.DelayMs} ms 后返回异常", pdu, startedAt);
                return new FrameOutcome(
                    ModbusFrames.BuildException(
                        transactionId, unitId, request.FunctionCode, (ModbusExceptionCode)decision.ExceptionCode),
                    false);

            case FaultAction.DelayThenPass:
                await Task.Delay(decision.DelayMs, cancellationToken).ConfigureAwait(false);
                Log(context, connectionId, device?.DeviceId, FrameDirection.Received,
                    $"{Describe(request, device)} → 故障注入「{decision.RuleName}」：延时 {decision.DelayMs} ms", pdu, startedAt);
                break;

            default:
                Log(context, connectionId, device?.DeviceId, FrameDirection.Received,
                    Describe(request, device), pdu, startedAt);
                break;
        }

        byte[] response = context.Processor.Process(request);

        Log(context, connectionId, device?.DeviceId, FrameDirection.Sent,
            DescribeResponse(response, request.FunctionCode, device, request), response, startedAt);

        return new FrameOutcome(response, false);
    }

    private FaultDecision EvaluateFault(
        ModbusDeviceBinding? device,
        string connectionId,
        string remoteIp,
        ConnectionRole role,
        ModbusRequest request)
    {
        if (!_faultInjector.Enabled)
        {
            return FaultDecision.Pass;
        }

        var faultContext = new FaultRequestContext(
            device?.DeviceId ?? string.Empty,
            connectionId,
            remoteIp,
            role,
            (byte)request.FunctionCode,
            request.Address,
            request.Quantity);

        return _faultInjector.Decide(in faultContext);
    }

    private static string Describe(ModbusRequest request, ModbusDeviceBinding? device)
    {
        string head = $"FC=0x{(byte)request.FunctionCode:X2} addr={request.Address} qty={request.Quantity}";
        AddressHint? hint = device?.AddressDescriber?.Invoke(request.Address);

        if (hint is not null)
        {
            head += $" → {hint.Target}";
        }

        if (request.FunctionCode == ModbusFunctionCode.ReadWriteMultipleRegisters)
        {
            head += $" writeAddr={request.WriteAddress} writeQty={request.WriteQuantity}";
        }

        if (request.Payload.Length > 0)
        {
            head += $" payload=[{HexFormat.ToHex(request.Payload)}]";

            // 两字节写入按大端解析出业务值；若该字段约定要交换，同时给出交换后的值，
            // 这样「发送端漏了高低位转换」这类问题不用人工比对就能看出来。
            if (request.Payload.Length == 2)
            {
                ushort raw = ModbusFrames.ReadUInt16BigEndian(request.Payload, 0);
                head += $" 值={raw}";

                if (hint?.ExpectsByteSwap == true)
                {
                    ushort interpretation = (ushort)((raw >> 8) | (raw << 8));
                    head += $"｜写入约定含高低位转换，释义={interpretation}";
                }
            }
        }

        return head;
    }

    private static string DescribeResponse(
        byte[] response,
        ModbusFunctionCode functionCode,
        ModbusDeviceBinding? device,
        ModbusRequest request)
    {
        if (response.Length >= ModbusFrames.MbapHeaderLength
            && (response[ModbusFrames.MbapHeaderLength] & 0x80) != 0)
        {
            byte exceptionCode = response[ModbusFrames.MbapHeaderLength + 1];
            string text = $"FC=0x{(byte)functionCode:X2} 异常响应 0x{exceptionCode:X2}";

            if (exceptionCode == 0x02 && device is not null)
            {
                // 写越界时把「请求范围 vs 配置范围」都打出来，省得靠猜。
                int requestedStart = request.Address * 2;
                int requestedLength = request.Payload.Length > 0
                    ? request.Payload.Length
                    : request.Quantity * 2;

                string ranges = string.Join(
                    "、",
                    device.Space.ConfiguredByteRanges.Select(static r => $"[{r.Start},{r.End})"));

                text += $"：写请求覆盖字节 [{requestedStart},{requestedStart + requestedLength})，"
                    + $"已配置的寄存器块为 {ranges}";
            }

            return text;
        }

        int pduLength = response.Length - ModbusFrames.MbapHeaderLength;
        return $"FC=0x{(byte)functionCode:X2} 响应 PDU {pduLength} 字节";
    }

    private void Log(
        ListenerContext context,
        string connectionId,
        string? deviceId,
        FrameDirection direction,
        string summary,
        byte[] raw,
        long startedAt)
    {
        double latencyMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        _frameLog.Add(new FrameLogEntry(
            DateTime.Now,
            $"{context.EndPoint.Address}:{context.EndPoint.Port} {connectionId}",
            deviceId ?? context.EndPoint.Address.ToString(),
            direction,
            summary,
            _options.RecordRawFrames ? raw : null,
            latencyMs));
    }

    private void LogControl(ListenerContext context, string? connectionId, string message)
        => _frameLog.Add(new FrameLogEntry(
            DateTime.Now,
            $"{context.EndPoint.Address}:{context.EndPoint.Port} {connectionId}",
            context.EndPoint.Address.ToString(),
            FrameDirection.Received,
            message,
            null,
            0));

    private sealed record ListenerContext(
        TcpListener Listener,
        IModbusDeviceResolver Resolver,
        ModbusRequestProcessor Processor,
        IPEndPoint EndPoint);

    private readonly record struct FrameOutcome(byte[]? Response, bool CloseConnection);

    /// <summary>
    /// 连接级的可变状态。读/写角色无法从报文内容区分（DLL 对两条连接发同样的 PDU），
    /// 但 WCS 的读连接只发读、写连接只发写，因此用首个请求判定即可。
    /// </summary>
    private sealed class ConnectionState
    {
        public ConnectionRole Role { get; private set; } = ConnectionRole.Unknown;

        public void Observe(ModbusRequest request)
        {
            if (Role != ConnectionRole.Unknown)
            {
                return;
            }

            Role = request.IsWrite ? ConnectionRole.Write : ConnectionRole.Read;
        }
    }
}
