using System.Buffers.Binary;
using PlcSimulator.Core.Protocol;

namespace PlcSimulator.Protocol.S7;

/// <summary>每条连接独立的 S7comm 会话：协商 PDU、Read Var、Write Var。处理原始 DB 字节，不转换业务字段。</summary>
public sealed class S7RequestProcessor(IS7DeviceSession session, int maxPduLength = 960)
{
    private readonly IS7DeviceSession _session = session;
    private readonly int _maxPduLength = maxPduLength;

    public int PduLength { get; private set; } = maxPduLength;

    public bool IsNegotiated { get; private set; }

    public byte[] Process(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 10 || pdu[0] != 0x32 || pdu[1] != 1)
        {
            throw new InvalidDataException("不是有效的 S7 Job 报文。");
        }

        ushort reference = ReadU16(pdu, 4);
        int parameterLength = ReadU16(pdu, 6);
        int dataLength = ReadU16(pdu, 8);
        if (pdu.Length > PduLength || parameterLength < 1 || pdu.Length != 10 + parameterLength + dataLength)
        {
            return Reply(reference, [], [], 0x8500);
        }

        ReadOnlySpan<byte> parameters = pdu.Slice(10, parameterLength);
        ReadOnlySpan<byte> data = pdu[(10 + parameterLength)..];
        if (parameters[0] == 0xF0)
        {
            if (parameterLength != 8 || dataLength != 0 || ReadU16(parameters, 6) < 240)
            {
                return Reply(reference, [], [], 0x8500);
            }
            PduLength = Math.Min(_maxPduLength, ReadU16(parameters, 6));
            IsNegotiated = true;
            byte[] setup = [0xF0, 0, 0, 1, 0, 1, 0, 0];
            WriteU16(setup, 6, PduLength);
            return Reply(reference, setup, []);
        }

        if (!IsNegotiated || parameters[0] is not (4 or 5))
        {
            return Reply(reference, [], [], 0x8104);
        }
        if (parameterLength < 2 || parameters[1] is 0 or > 20 || parameterLength != 2 + parameters[1] * 12)
        {
            return Reply(reference, [], [], 0x8500);
        }

        var items = new Item[parameters[1]];
        for (int i = 0; i < items.Length; i++)
        {
            ReadOnlySpan<byte> specification = parameters.Slice(2 + i * 12, 12);
            if (specification[0] != 0x12 || specification[1] != 0x0A || specification[2] != 0x10)
            {
                return Reply(reference, [], [], 0x8500);
            }
            int bitAddress = (specification[9] << 16) | (specification[10] << 8) | specification[11];
            int width = specification[3] switch { 2 or 3 => 1, 4 or 5 => 2, 6 or 7 or 8 => 4, _ => 0 };
            int count = ReadU16(specification, 4);
            S7AccessResult error = specification[8] != 0x84 ? S7AccessResult.ObjectNotFound
                : width == 0 ? S7AccessResult.UnsupportedType
                : count == 0 || bitAddress % 8 != 0 ? S7AccessResult.InvalidAddress : S7AccessResult.Success;
            items[i] = new Item(ReadU16(specification, 6), bitAddress / 8, count * width, error);
        }

        return parameters[0] == 4
            ? ReadItems(reference, items, data)
            : WriteItems(reference, items, data);
    }

    private byte[] ReadItems(ushort reference, Item[] items, ReadOnlySpan<byte> requestData)
    {
        if (requestData.Length != 0)
        {
            return Reply(reference, [], [], 0x8500);
        }
        int length = 0;
        for (int i = 0; i < items.Length; i++)
        {
            int count = items[i].Error == S7AccessResult.Success ? items[i].Length : 0;
            length += 4 + count + (i < items.Length - 1 ? count % 2 : 0);
        }
        if (14 + length > PduLength)
        {
            return Reply(reference, [], [], 0x8500);
        }

        var data = new List<byte>(length);
        for (int i = 0; i < items.Length; i++)
        {
            Item item = items[i];
            byte[] payload = new byte[item.Error == S7AccessResult.Success ? item.Length : 0];
            S7AccessResult result = item.Error == S7AccessResult.Success
                ? _session.ReadDb(item.DbNumber, item.Offset, payload) : item.Error;
            if (result == S7AccessResult.Success)
            {
                data.AddRange([(byte)result, 4, (byte)(payload.Length * 8 >> 8), (byte)(payload.Length * 8)]);
                data.AddRange(payload);
                if (i < items.Length - 1 && payload.Length % 2 != 0)
                {
                    data.Add(0);
                }
            }
            else
            {
                data.AddRange([(byte)result, 0, 0, 0]);
            }
        }
        return Reply(reference, [4, (byte)items.Length], data.ToArray());
    }

    private byte[] WriteItems(ushort reference, Item[] items, ReadOnlySpan<byte> data)
    {
        // 校验整帧后才落写，截断或长度不一致的报文不能部分改动指令区。
        var payloads = new (int Offset, int Length, bool ValidType)[items.Length];
        int position = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (position + 4 > data.Length)
            {
                return Reply(reference, [], [], 0x8500);
            }
            int wireLength = ReadU16(data, position + 2);
            byte transport = data[position + 1];
            int byteLength = transport == 9 ? wireLength : (wireLength + 7) / 8;
            bool valid = transport == 4 && wireLength == items[i].Length * 8;
            position += 4;
            if (position + byteLength > data.Length)
            {
                return Reply(reference, [], [], 0x8500);
            }
            payloads[i] = (position, byteLength, valid);
            position += byteLength;
            if (i < items.Length - 1 && byteLength % 2 != 0)
            {
                position++;
            }
        }
        // 少数客户端也给最后一个奇数长度项加一个对齐零字节。
        if (position != data.Length && !(position + 1 == data.Length && data[^1] == 0 && payloads[^1].Length % 2 != 0))
        {
            return Reply(reference, [], [], 0x8500);
        }

        byte[] results = new byte[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            Item item = items[i];
            var payload = payloads[i];
            results[i] = (byte)(item.Error != S7AccessResult.Success ? item.Error
                : !payload.ValidType ? S7AccessResult.UnsupportedType
                : _session.WriteDb(item.DbNumber, item.Offset, data.Slice(payload.Offset, payload.Length)));
        }
        return Reply(reference, [5, (byte)items.Length], results);
    }

    private static byte[] Reply(ushort reference, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> data, ushort error = 0)
    {
        byte[] response = new byte[12 + parameters.Length + data.Length];
        response[0] = 0x32;
        response[1] = 3;
        WriteU16(response, 4, reference);
        WriteU16(response, 6, parameters.Length);
        WriteU16(response, 8, data.Length);
        WriteU16(response, 10, error);
        parameters.CopyTo(response.AsSpan(12));
        data.CopyTo(response.AsSpan(12 + parameters.Length));
        return response;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);

    private static void WriteU16(Span<byte> buffer, int offset, int value)
        => BinaryPrimitives.WriteUInt16BigEndian(buffer[offset..], (ushort)value);

    private readonly record struct Item(ushort DbNumber, int Offset, int Length, S7AccessResult Error);
}
