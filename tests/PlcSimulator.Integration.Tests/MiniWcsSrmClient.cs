using System.Net.Sockets;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Integration.Tests;

/// <summary>
/// 扮演 WCS 的堆垛机客户端：三个端口各开一条真 TCP 连接。
/// 行为对齐 WCS —— 指令口只发不收；状态口与报警口发一个 0xFF 收一帧。
/// </summary>
internal sealed class MiniWcsSrmClient : IDisposable
{
    private const byte PollTriggerByte = 0xFF;

    private readonly TcpClient _commandClient;
    private readonly TcpClient _statusClient;
    private readonly TcpClient _alarmClient;
    private readonly NetworkStream _command;
    private readonly NetworkStream _status;
    private readonly NetworkStream _alarm;
    private readonly int _statusFrameLength;
    private readonly int _alarmFrameLength;

    public MiniWcsSrmClient(SocketPortsConfig ports)
    {
        _commandClient = Connect(ports.Command);
        _statusClient = Connect(ports.Status);
        _alarmClient = Connect(ports.Alarm);

        _command = _commandClient.GetStream();
        _status = _statusClient.GetStream();
        _alarm = _alarmClient.GetStream();

        _statusFrameLength = ports.StatusFrameLength;
        _alarmFrameLength = ports.AlarmFrameLength;
    }

    public void SendCommand(in SrmCommand command)
    {
        Span<byte> frame = stackalloc byte[SrmLayout.CommandFrameLength];
        SrmFrames.BuildCommandFrame(command, frame);
        _command.Write(frame);
    }

    public Task<byte[]> PollStatusAsync(CancellationToken cancellationToken = default)
        => PollAsync(_status, _statusFrameLength, cancellationToken);

    public Task<byte[]> PollAlarmAsync(CancellationToken cancellationToken = default)
        => PollAsync(_alarm, _alarmFrameLength, cancellationToken);

    /// <summary>轮询状态口直到读到期望的作业状态字，返回那一帧。</summary>
    public async Task<byte[]> WaitForReportAsync(
        byte expectedReport,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        while (true)
        {
            byte[] frame = await PollStatusAsync(timeout.Token);

            if (frame[6] == expectedReport)
            {
                return frame;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>尝试从指令口读一个字节；超时即视为「服务端没有回写」。</summary>
    public async Task<bool> TryReadCommandPortAsync(int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        byte[] buffer = new byte[1];

        try
        {
            return await _command.ReadAsync(buffer, timeout.Token) > 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

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
        byte[] trigger = [PollTriggerByte];
        await stream.WriteAsync(trigger, cancellationToken);

        byte[] frame = new byte[frameLength];
        await stream.ReadExactlyAsync(frame, cancellationToken);
        return frame;
    }

    private static TcpClient Connect(int port)
    {
        var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        return client;
    }
}
