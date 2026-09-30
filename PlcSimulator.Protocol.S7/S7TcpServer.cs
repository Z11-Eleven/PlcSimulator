using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PlcSimulator.Core.Frames;
using PlcSimulator.Core.Protocol;

namespace PlcSimulator.Protocol.S7;

public sealed record S7DeviceEndpoint(IPAddress Address, int Port, IS7DeviceSession Session, int MaxPduLength = 960);

/// <summary>ISO-on-TCP / COTP / S7comm DB 服务端。每台堆垛机使用一个监听端点。</summary>
public sealed class S7TcpServer : IProtocolServer
{
    private readonly S7DeviceEndpoint[] _endpoints;
    private readonly int _maxConnectionsPerIp;
    private readonly bool _recordRawFrames;
    private readonly IFrameLog _frameLog;
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptTasks = [];
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private readonly ConcurrentDictionary<string, int> _connections = new();
    private CancellationTokenSource? _cts;
    private long _sequence;

    public S7TcpServer(IEnumerable<S7DeviceEndpoint> endpoints, int maxConnectionsPerIp = 8,
        bool recordRawFrames = true, IFrameLog? frameLog = null)
    {
        _endpoints = endpoints.ToArray();
        _maxConnectionsPerIp = maxConnectionsPerIp;
        _recordRawFrames = recordRawFrames;
        _frameLog = frameLog ?? NullFrameLog.Instance;
    }

    public string ProtocolName => "S7";
    public bool IsRunning => _cts is not null;
    public IReadOnlyList<IPEndPoint> Endpoints => _listeners.Select(l => (IPEndPoint)l.LocalEndpoint).ToArray();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // 全部绑定成功后才接收连接；失败时回收已占用的端口。
            foreach (S7DeviceEndpoint endpoint in _endpoints)
            {
                var listener = new TcpListener(endpoint.Address, endpoint.Port);
                listener.Start();
                _listeners.Add(listener);
            }
            for (int i = 0; i < _listeners.Count; i++)
            {
                _acceptTasks.Add(AcceptAsync(_listeners[i], _endpoints[i], _cts.Token));
            }
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        if (cts is null)
        {
            return;
        }
        await cts.CancelAsync().ConfigureAwait(false);
        foreach (TcpListener listener in _listeners)
        {
            listener.Stop();
        }
        await Task.WhenAll(_acceptTasks).ConfigureAwait(false);
        await Task.WhenAll(_clientTasks.Values).ConfigureAwait(false);
        _acceptTasks.Clear();
        _listeners.Clear();
        _clientTasks.Clear();
        _connections.Clear();
        _cts = null;
        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptAsync(TcpListener listener, S7DeviceEndpoint endpoint, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }
            string ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            int count = _connections.AddOrUpdate(ip, 1, (_, value) => value + 1);
            if (count > _maxConnectionsPerIp)
            {
                _connections.AddOrUpdate(ip, 0, (_, value) => value - 1);
                client.Dispose();
                continue;
            }
            long id = Interlocked.Increment(ref _sequence);
            Task task = ServeAsync(client, endpoint, ip, id, token);
            _clientTasks[id] = task;
            _ = task.ContinueWith(_ => _clientTasks.TryRemove(id, out Task? removed),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client, S7DeviceEndpoint endpoint, string ip, long id, CancellationToken token)
    {
        using (client)
        {
            client.NoDelay = true;
            var processor = new S7RequestProcessor(endpoint.Session, endpoint.MaxPduLength);
            bool connected = false;
            try
            {
                NetworkStream stream = client.GetStream();
                while (!token.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    byte[] frame = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
                    string requestSummary = DescribeRequest(frame);
                    Log(frame, FrameDirection.Received, requestSummary, endpoint, id);
                    byte[] response;
                    if (!connected)
                    {
                        if (frame.Length < 11 || frame[4] != frame.Length - 5 || frame[5] != 0xE0)
                        {
                            throw new InvalidDataException("无效的 COTP 连接请求。");
                        }
                        response = frame.ToArray();
                        response[5] = 0xD0;
                        response[6] = frame[8];
                        response[7] = frame[9];
                        response[8] = 0;
                        response[9] = 1;
                        connected = true;
                    }
                    else
                    {
                        if (frame.Length < 17 || frame[4] != 2 || frame[5] != 0xF0 || frame[6] != 0x80)
                        {
                            throw new InvalidDataException("不支持的 COTP 数据报文。");
                        }
                        byte[] pdu = processor.Process(frame.AsSpan(7));
                        response = new byte[7 + pdu.Length];
                        response[0] = 3;
                        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), (ushort)response.Length);
                        response[4] = 2;
                        response[5] = 0xF0;
                        response[6] = 0x80;
                        pdu.CopyTo(response, 7);
                    }
                    await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
                    string replySummary = requestSummary + " 应答";
                    if (response.Length >= 19 && response[7] == 0x32 && response[8] == 3)
                    {
                        ushort error = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(17));
                        replySummary += error == 0 ? "：已应答" : $"：错误 0x{error:X4}";
                        if (requestSummary.StartsWith("写入 ", StringComparison.Ordinal) && response.Length > 21)
                        {
                            replySummary += $"，项返回码 {Convert.ToHexString(response.AsSpan(21))}";
                        }
                    }
                    Log(response, FrameDirection.Sent, replySummary, endpoint, id);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException)
            {
                if (!token.IsCancellationRequested)
                {
                    Log([], FrameDirection.Received, $"连接关闭：{ex.Message}", endpoint, id);
                }
            }
            finally
            {
                _connections.AddOrUpdate(ip, 0, (_, value) => value - 1);
            }
        }
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (header[0] != 3 || header[1] != 0 || length < 7 || length > 967)
        {
            throw new InvalidDataException("无效的 TPKT 头或长度。");
        }
        byte[] frame = new byte[length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4), token).ConfigureAwait(false);
        return frame;
    }

    private void Log(byte[] raw, FrameDirection direction, string summary, S7DeviceEndpoint endpoint, long id)
        => _frameLog.Add(new FrameLogEntry(DateTime.Now, $"s7-{id}", endpoint.Session.DeviceId, direction,
            $"[S7] {summary}（{raw.Length} 字节）", _recordRawFrames ? raw : null, 0));

    private static string DescribeRequest(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 19 || frame[7] != 0x32 || frame[8] != 1)
        {
            return "COTP 连接请求";
        }
        if (frame[17] == 0xF0)
        {
            return "协商 PDU";
        }
        if (frame[17] is not (4 or 5) || frame[18] == 0 || frame.Length < 19 + frame[18] * 12)
        {
            return "收到 S7 请求";
        }
        var addresses = new List<string>();
        for (int i = 0; i < frame[18]; i++)
        {
            var item = frame.Slice(19 + i * 12, 12);
            int db = BinaryPrimitives.ReadUInt16BigEndian(item[6..]);
            int bits = (item[9] << 16) | (item[10] << 8) | item[11];
            int count = BinaryPrimitives.ReadUInt16BigEndian(item[4..]);
            addresses.Add($"DB{db}.{bits / 8}，类型 {item[3]}，数量 {count}");
        }
        return (frame[17] == 5 ? "写入 " : "读取 ") + string.Join(" / ", addresses);
    }
}
