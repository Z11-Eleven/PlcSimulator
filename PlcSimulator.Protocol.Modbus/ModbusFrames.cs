using System.Buffers.Binary;

namespace PlcSimulator.Protocol.Modbus;

/// <summary>
/// MBAP 头 + PDU 的编解码。MBAP 头固定 7 字节：
/// TransactionId(2) + ProtocolId(2) + Length(2) + UnitId(1)，
/// 其中 Length 统计 UnitId 与 PDU 的长度之和。
/// </summary>
public static class ModbusFrames
{
    public const int MbapHeaderLength = 7;

    /// <summary>MBAP 的 Length 字段至少要覆盖 UnitId + 功能码。</summary>
    public const int MinLengthField = 2;

    public static bool TryReadHeader(
        ReadOnlySpan<byte> header,
        out ushort transactionId,
        out ushort protocolId,
        out ushort length)
    {
        transactionId = 0;
        protocolId = 0;
        length = 0;

        if (header.Length < MbapHeaderLength)
        {
            return false;
        }

        transactionId = BinaryPrimitives.ReadUInt16BigEndian(header);
        protocolId = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        length = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        return true;
    }

    public static bool TryDecodePdu(
        ushort transactionId,
        byte unitId,
        ReadOnlySpan<byte> pdu,
        out ModbusRequest request,
        out ModbusExceptionCode error)
    {
        request = default;
        error = ModbusExceptionCode.IllegalDataValue;

        if (pdu.Length < 1)
        {
            return false;
        }

        var functionCode = (ModbusFunctionCode)pdu[0];

        switch (functionCode)
        {
            case ModbusFunctionCode.ReadCoils:
            case ModbusFunctionCode.ReadDiscreteInputs:
            case ModbusFunctionCode.ReadHoldingRegisters:
            case ModbusFunctionCode.ReadInputRegisters:
                if (pdu.Length != 5)
                {
                    return false;
                }

                request = new ModbusRequest(
                    transactionId,
                    unitId,
                    functionCode,
                    ReadUInt16BigEndian(pdu, 1),
                    ReadUInt16BigEndian(pdu, 3),
                    0,
                    0,
                    []);

                error = default;
                return true;

            case ModbusFunctionCode.WriteSingleCoil:
            case ModbusFunctionCode.WriteSingleRegister:
                if (pdu.Length != 5)
                {
                    return false;
                }

                request = new ModbusRequest(
                    transactionId,
                    unitId,
                    functionCode,
                    ReadUInt16BigEndian(pdu, 1),
                    1,
                    0,
                    0,
                    pdu.Slice(3, 2).ToArray());

                error = default;
                return true;

            case ModbusFunctionCode.WriteMultipleCoils:
            case ModbusFunctionCode.WriteMultipleRegisters:
                {
                    if (pdu.Length < 6)
                    {
                        return false;
                    }

                    ushort quantity = ReadUInt16BigEndian(pdu, 3);
                    int byteCount = pdu[5];
                    if (pdu.Length != 6 + byteCount)
                    {
                        return false;
                    }

                    request = new ModbusRequest(
                        transactionId,
                        unitId,
                        functionCode,
                        ReadUInt16BigEndian(pdu, 1),
                        quantity,
                        0,
                        0,
                        pdu.Slice(6, byteCount).ToArray());

                    error = default;
                    return true;
                }

            case ModbusFunctionCode.ReadWriteMultipleRegisters:
                {
                    if (pdu.Length < 10)
                    {
                        return false;
                    }

                    ushort writeQuantity = ReadUInt16BigEndian(pdu, 7);
                    int byteCount = pdu[9];
                    if (pdu.Length != 10 + byteCount)
                    {
                        return false;
                    }

                    request = new ModbusRequest(
                        transactionId,
                        unitId,
                        functionCode,
                        ReadUInt16BigEndian(pdu, 1),
                        ReadUInt16BigEndian(pdu, 3),
                        ReadUInt16BigEndian(pdu, 5),
                        writeQuantity,
                        pdu.Slice(10, byteCount).ToArray());

                    error = default;
                    return true;
                }

            default:
                error = ModbusExceptionCode.IllegalFunction;
                return false;
        }
    }

    public static byte[] BuildReadResponse(
        ushort transactionId,
        byte unitId,
        ModbusFunctionCode functionCode,
        ReadOnlySpan<byte> data)
    {
        byte[] pdu = new byte[2 + data.Length];
        pdu[0] = (byte)functionCode;
        pdu[1] = (byte)data.Length;
        data.CopyTo(pdu.AsSpan(2));
        return BuildFrame(transactionId, unitId, pdu);
    }

    public static byte[] BuildWriteResponse(
        ushort transactionId,
        byte unitId,
        ModbusFunctionCode functionCode,
        ushort address,
        ushort value)
    {
        byte[] pdu = new byte[5];
        pdu[0] = (byte)functionCode;
        WriteUInt16BigEndian(pdu, 1, address);
        WriteUInt16BigEndian(pdu, 3, value);
        return BuildFrame(transactionId, unitId, pdu);
    }

    public static byte[] BuildException(
        ushort transactionId,
        byte unitId,
        ModbusFunctionCode functionCode,
        ModbusExceptionCode exceptionCode)
    {
        byte[] pdu = [(byte)((byte)functionCode | 0x80), (byte)exceptionCode];
        return BuildFrame(transactionId, unitId, pdu);
    }

    public static byte[] BuildFrame(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        byte[] frame = new byte[MbapHeaderLength + pdu.Length];
        WriteUInt16BigEndian(frame, 0, transactionId);
        WriteUInt16BigEndian(frame, 2, 0);
        WriteUInt16BigEndian(frame, 4, (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame.AsSpan(MbapHeaderLength));
        return frame;
    }

    public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);

    public static void WriteUInt16BigEndian(Span<byte> buffer, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16BigEndian(buffer[offset..], value);
}
