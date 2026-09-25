using System.Net;

namespace PlcSimulator.Core.Protocol;

/// <summary>
/// 传输无关的协议服务端抽象。Modbus 与（未来的）S7 都实现它，
/// 宿主按配置的 protocol 字段实例化，互不影响。
/// </summary>
public interface IProtocolServer : IAsyncDisposable
{
    string ProtocolName { get; }

    IReadOnlyList<IPEndPoint> Endpoints { get; }

    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}
