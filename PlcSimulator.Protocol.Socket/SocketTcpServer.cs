using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PlcSimulator.Core.Frames;
using PlcSimulator.Core.Protocol;

namespace PlcSimulator.Protocol.Socket;

/// <summary>
/// 自定义 Socket 协议的服务端：每台设备监听三个端口。
/// <para>
/// 与 Modbus 侧的关键差别在「谁主动」——指令口由 WCS 单向下发，模拟器只收不回
/// （WCS 在 2000 端口没有接收处理分支，回写只会污染对端缓冲）；
/// 状态口与报警口由 WCS 每次发一个轮询字节驱动，收到几个字节就回几帧。
/// 这正对应 WCS 的行为（<c>ScSocket.cs</c> 每周期在两个口各发一个 0xFF）。
/// </para>
/// </summary>
public sealed class SocketTcpServer : IProtocolServer
{
    /// <summary>轮询触发字节。WCS 在状态口与报警口每周期发一个它。</summary>
    public const byte PollTriggerByte = 0xFF;

    private readonly IReadOnlyList<SocketDeviceEndpoint> _endpoints;
    private readonly SocketServerOptions _options;
    private readonly IFrameLog _frameLog;
    private readonly List<ListenerContext> _listeners = [];
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new(StringComparer.Ordinal);
    private readonly List<Task> _backgroundTasks = [];
    private CancellationTokenSource? _cts;
    private long _connectionCounter;

    public SocketTcpServer(
        IEnumerable<SocketDeviceEndpoint> endpoints,
        SocketServerOptions? options = null,
        IFrameLog? frameLog = null)
    {
        _endpoints = [.. endpoints];
        _options = options ?? new SocketServerOptions();
        _frameLog = frameLog ?? NullFrameLog.Instance;

        Endpoints =
        [
            .. _endpoints.SelectMany(static e => e.Ports().Select(p => new IPEndPoint(e.Address, p.Port)))
        ];
    }

    public string ProtocolName => "Socket";

    public IReadOnlyList<IPEndPoint> Endpoints { get; }

    public bool IsRunning => _cts is not null;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("服务端已经启动。");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (SocketDeviceEndpoint endpoint in _endpoints)
        {
            foreach ((SocketPortRole role, int port) in endpoint.Ports())
            {
                // 独占绑定，不设 SO_REUSEADDR：端口被占时让 Start() 直接抛 SocketException，
                // 比两个实例分摊流量、表现为"改了配置却不生效"好查得多。
                var listener = new TcpListener(endpoint.Address, port);
                listener.Start();

                var context = new ListenerContext(
                    listener,
                    endpoint,
                    role,
                    (IPEndPoint)listener.LocalEndpoint);

                _listeners.Add(context);
                _backgroundTasks.Add(Task.Run(() => AcceptLoopAsync(context, _cts.Token), CancellationToken.None));
            }
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
        // 每条连接自己的空闲令牌：每处理一次就重置，卡死不发数据的一端会自己让出配额。
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ResetIdleTimeout(idleCts);

        string deviceId = context.Endpoint.Session.DeviceId;

        try
        {
            if (_options.NoDelay)
            {
                client.NoDelay = true;
            }

            using (client)
            {
                NetworkStream stream = client.GetStream();

                if (context.Role == SocketPortRole.Command)
                {
                    await PumpCommandPortAsync(stream, context, connectionId, deviceId, idleCts).ConfigureAwait(false);
                }
                else
                {
                    await PumpPolledPortAsync(stream, context, connectionId, deviceId, idleCts).ConfigureAwait(false);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // 对端正常关闭。
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogControl(context, connectionId, $"空闲超过 {_options.IdleTimeoutMs / 1000} 秒，关闭连接", deviceId);
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
            // 兜底：状态机或数据区抛出的意外异常不能让连接无声消失、无人知晓。
            LogControl(context, connectionId, $"连接处理异常：{ex.Message}", deviceId);
        }
        finally
        {
            _connectionsPerIp.AddOrUpdate(remoteIp, 0, static (_, current) => Math.Max(0, current - 1));
        }
    }

    /// <summary>
    /// 指令口：按定长读帧，交设备处理，**不回任何字节**。
    /// </summary>
    private async Task PumpCommandPortAsync(
        NetworkStream stream,
        ListenerContext context,
        string connectionId,
        string deviceId,
        CancellationTokenSource idleCts)
    {
        ISocketDeviceSession session = context.Endpoint.Session;
        byte[] frame = new byte[session.CommandFrameLength];

        while (!idleCts.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(frame, idleCts.Token).ConfigureAwait(false);
            ResetIdleTimeout(idleCts);

            long startedAt = Stopwatch.GetTimestamp();
            LogFrame(context, connectionId, deviceId, FrameDirection.Received, $"指令帧 {frame.Length} 字节", frame, startedAt);

            session.OnCommandFrame(frame);
        }
    }

    /// <summary>
    /// 状态口与报警口：每读到一个字节就回一帧。
    /// 逐字节应答天然支持对端连发，也匹配 WCS「每周期一个 0xFF」的节奏。
    /// </summary>
    private async Task PumpPolledPortAsync(
        NetworkStream stream,
        ListenerContext context,
        string connectionId,
        string deviceId,
        CancellationTokenSource idleCts)
    {
        ISocketDeviceSession session = context.Endpoint.Session;
        bool isStatus = context.Role == SocketPortRole.Status;

        byte[] trigger = new byte[1];
        byte[] response = new byte[isStatus ? session.StatusFrameLength : session.AlarmFrameLength];

        while (!idleCts.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(trigger, idleCts.Token).ConfigureAwait(false);
            ResetIdleTimeout(idleCts);

            if (trigger[0] != PollTriggerByte)
            {
                // 不是约定的轮询字节仍然应答：对端若换了触发约定，这条日志能立刻说明问题，
                // 而死等只会表现为"堆垛机状态不刷新"。
                LogControl(
                    context,
                    connectionId,
                    $"收到非轮询字节 0x{trigger[0]:X2}，仍按一次轮询应答",
                    deviceId);
            }

            long startedAt = Stopwatch.GetTimestamp();
            int length = isStatus ? session.WriteStatusFrame(response) : session.WriteAlarmFrame(response);

            await stream.WriteAsync(response.AsMemory(0, length), idleCts.Token).ConfigureAwait(false);

            string summary = isStatus ? $"状态帧 {length} 字节" : $"报警帧 {length} 字节";
            LogFrame(context, connectionId, deviceId, FrameDirection.Sent, summary, response, startedAt, length);
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

    private void LogFrame(
        ListenerContext context,
        string connectionId,
        string deviceId,
        FrameDirection direction,
        string summary,
        byte[] raw,
        long startedAt,
        int? length = null)
    {
        double latencyMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        _frameLog.Add(new FrameLogEntry(
            DateTime.Now,
            $"{context.LocalEndPoint.Address}:{context.LocalEndPoint.Port} {connectionId}",
            deviceId,
            direction,
            summary,
            _options.RecordRawFrames ? (length is int l ? raw[..l] : raw) : null,
            latencyMs));
    }

    private void LogControl(ListenerContext context, string? connectionId, string message, string? deviceId = null)
        => _frameLog.Add(new FrameLogEntry(
            DateTime.Now,
            $"{context.LocalEndPoint.Address}:{context.LocalEndPoint.Port} {connectionId}",
            deviceId ?? context.Endpoint.Session.DeviceId,
            FrameDirection.Received,
            message,
            null,
            0));

    private sealed record ListenerContext(
        TcpListener Listener,
        SocketDeviceEndpoint Endpoint,
        SocketPortRole Role,
        IPEndPoint LocalEndPoint);
}
