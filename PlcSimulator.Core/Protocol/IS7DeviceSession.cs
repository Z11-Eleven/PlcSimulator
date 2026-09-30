namespace PlcSimulator.Core.Protocol;

public enum S7AccessResult : byte
{
    Success = 0xFF,
    AccessDenied = 0x03,
    InvalidAddress = 0x05,
    UnsupportedType = 0x06,
    ObjectNotFound = 0x0A,
}

/// <summary>S7 DB 的字节读写入口；业务数据的编码由设备负责。</summary>
public interface IS7DeviceSession
{
    string DeviceId { get; }

    S7AccessResult ReadDb(ushort dbNumber, int byteOffset, Span<byte> destination);

    S7AccessResult WriteDb(ushort dbNumber, int byteOffset, ReadOnlySpan<byte> source);
}
