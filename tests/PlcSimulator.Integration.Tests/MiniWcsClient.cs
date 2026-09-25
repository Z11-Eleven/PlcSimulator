using System.Net.Sockets;
using PlcSimulator.Protocol.Modbus;

namespace PlcSimulator.Integration.Tests;

/// <summary>
/// 按 WCS 的实际行为收发的最小客户端：每个设备建两条连接（读一条、写一条），
/// 读用 FC3、写用 FC16，地址与寄存器数据均为大端。
/// 它让端到端闭环可以在没有 WCS 参与的情况下自动化验证。
/// </summary>
public sealed class MiniWcsClient : IDisposable
{
    private readonly TcpClient _readClient;
    private readonly TcpClient _writeClient;
    private readonly NetworkStream _readStream;
    private readonly NetworkStream _writeStream;
    private ushort _transactionId;

    public MiniWcsClient(string host, int port, byte unitId = 1, int readTimeoutMs = 0)
    {
        _readClient = new TcpClient(host, port) { NoDelay = true };
        _writeClient = new TcpClient(host, port) { NoDelay = true };

        if (readTimeoutMs > 0)
        {
            _readClient.ReceiveTimeout = readTimeoutMs;
            _writeClient.ReceiveTimeout = readTimeoutMs;
        }

        _readStream = _readClient.GetStream();
        _writeStream = _writeClient.GetStream();
        UnitId = unitId;
    }

    public byte UnitId { get; }

    /// <summary>对应 WCS 的 readService.ReadByte(address, byteCount, ...)。</summary>
    public byte[] ReadBytes(ushort address, int byteCount)
    {
        if (byteCount % 2 != 0)
        {
            throw new ArgumentException("读取字节数必须是偶数。", nameof(byteCount));
        }

        byte[] pdu = new byte[5];
        pdu[0] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
        ModbusFrames.WriteUInt16BigEndian(pdu, 1, address);
        ModbusFrames.WriteUInt16BigEndian(pdu, 3, (ushort)(byteCount / 2));

        byte[] response = Exchange(_readStream, pdu);
        ThrowIfException(response);
        return response[2..];
    }

    /// <summary>对应 WCS 的 writeService.WriteByte(address, value, ...)。</summary>
    public void WriteBytes(ushort address, byte[] data)
    {
        if (data.Length % 2 != 0)
        {
            throw new ArgumentException("写入字节数必须是偶数。", nameof(data));
        }

        byte[] pdu = new byte[6 + data.Length];
        pdu[0] = (byte)ModbusFunctionCode.WriteMultipleRegisters;
        ModbusFrames.WriteUInt16BigEndian(pdu, 1, address);
        ModbusFrames.WriteUInt16BigEndian(pdu, 3, (ushort)(data.Length / 2));
        pdu[5] = (byte)data.Length;
        data.CopyTo(pdu.AsSpan(6));

        byte[] response = Exchange(_writeStream, pdu);
        ThrowIfException(response);
    }

    /// <summary>单寄存器写，对应 WCS 的单字段写路径（心跳清零）。</summary>
    public void WriteSingleRegister(ushort address, ushort value)
    {
        byte[] pdu = new byte[5];
        pdu[0] = (byte)ModbusFunctionCode.WriteSingleRegister;
        ModbusFrames.WriteUInt16BigEndian(pdu, 1, address);
        ModbusFrames.WriteUInt16BigEndian(pdu, 3, value);

        byte[] response = Exchange(_writeStream, pdu);
        ThrowIfException(response);
    }

    public void Dispose()
    {
        _readStream.Dispose();
        _writeStream.Dispose();
        _readClient.Dispose();
        _writeClient.Dispose();
    }

    private byte[] Exchange(NetworkStream stream, byte[] pdu)
    {
        ushort transactionId = unchecked(++_transactionId);
        byte[] frame = ModbusFrames.BuildFrame(transactionId, UnitId, pdu);

        stream.Write(frame);
        stream.Flush();

        byte[] header = new byte[ModbusFrames.MbapHeaderLength];
        stream.ReadExactly(header);

        if (!ModbusFrames.TryReadHeader(header, out _, out _, out ushort length))
        {
            throw new InvalidOperationException("收到无法解析的 MBAP 头。");
        }

        byte[] responsePdu = new byte[length - 1];
        stream.ReadExactly(responsePdu);
        return responsePdu;
    }

    private static void ThrowIfException(byte[] response)
    {
        if (response.Length >= 2 && (response[0] & 0x80) != 0)
        {
            throw new ModbusException($"服务端返回异常响应 0x{response[1]:X2}（功能码 0x{response[0]:X2}）。");
        }
    }
}

public sealed class ModbusException(string message) : Exception(message);
