using System.Net;
using PlcSimulator.Core.Protocol;

namespace PlcSimulator.Protocol.Socket;

/// <summary>一个 Socket 端口在设备侧扮演的角色。</summary>
public enum SocketPortRole
{
    /// <summary>指令口：设备只收不回。</summary>
    Command,

    /// <summary>状态口：收到一个轮询字节就回一帧状态。</summary>
    Status,

    /// <summary>报警口：收到一个轮询字节就回一帧报警。</summary>
    Alarm,
}

/// <summary>一台 Socket 设备的三个端口与它的会话。</summary>
public sealed record SocketDeviceEndpoint(
    IPAddress Address,
    int CommandPort,
    int StatusPort,
    int AlarmPort,
    ISocketDeviceSession Session)
{
    public IEnumerable<(SocketPortRole Role, int Port)> Ports()
    {
        yield return (SocketPortRole.Command, CommandPort);
        yield return (SocketPortRole.Status, StatusPort);
        yield return (SocketPortRole.Alarm, AlarmPort);
    }
}
