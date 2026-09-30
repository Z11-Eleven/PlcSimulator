namespace PlcSimulator.Protocol.Modbus;

/// <summary>
/// 功能码分派与寄存器访问。
/// <para>
/// 线圈类功能码（FC01/02/05/15）映射到同一字节区的位视图（位地址 n → 字节 n/8 的第 n%8 位）。
/// WCS 实际不使用这些功能码，这层映射只用于保证「任何功能码都能得到合理响应」，
/// 避免闭源 DLL 的实际行为与预期不符时直接吃到 IllegalFunction。
/// </para>
/// </summary>
public sealed class ModbusRequestProcessor(IModbusDeviceResolver resolver)
{
    public const int MaxReadCoils = 2000;
    public const int MaxReadRegisters = 125;
    public const int MaxWriteCoils = 1968;
    public const int MaxWriteRegisters = 123;
    public const int MaxWriteRegistersInReadWrite = 121;

    private readonly IModbusDeviceResolver _resolver = resolver;

    /// <summary>处理一次请求并返回完整的响应帧。</summary>
    public byte[] Process(ModbusRequest request)
    {
        ModbusDeviceBinding? device = _resolver.Find(request.UnitId);
        if (device is null)
        {
            return Exception(request, ModbusExceptionCode.GatewayTargetDeviceFailedToRespond);
        }

        return request.FunctionCode switch
        {
            ModbusFunctionCode.ReadHoldingRegisters or ModbusFunctionCode.ReadInputRegisters
                => ReadRegisters(request, device),
            ModbusFunctionCode.WriteSingleRegister
                => WriteSingleRegister(request, device),
            ModbusFunctionCode.WriteMultipleRegisters
                => WriteMultipleRegisters(request, device),
            ModbusFunctionCode.ReadWriteMultipleRegisters
                => ReadWriteMultipleRegisters(request, device),
            ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs
                => ReadBits(request, device),
            ModbusFunctionCode.WriteSingleCoil
                => WriteSingleCoil(request, device),
            ModbusFunctionCode.WriteMultipleCoils
                => WriteMultipleCoils(request, device),
            _ => Exception(request, ModbusExceptionCode.IllegalFunction),
        };
    }

    private static byte[] ReadRegisters(ModbusRequest request, ModbusDeviceBinding device)
    {
        if (request.Quantity is 0 or > MaxReadRegisters)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        byte[] data = new byte[request.Quantity * 2];
        if (!device.Space.TryReadRegisters(request.Address, request.Quantity, data))
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        return ModbusFrames.BuildReadResponse(
            request.TransactionId, request.UnitId, request.FunctionCode, data);
    }

    private static byte[] WriteSingleRegister(ModbusRequest request, ModbusDeviceBinding device)
    {
        if (request.Payload.Length != 2)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        if (!device.Space.TryWriteRegisters(request.Address, request.Payload))
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        device.RegistersWritten?.Invoke(request.Address, request.Payload);
        ushort value = ModbusFrames.ReadUInt16BigEndian(request.Payload, 0);
        return ModbusFrames.BuildWriteResponse(
            request.TransactionId, request.UnitId, request.FunctionCode, request.Address, value);
    }

    private static byte[] WriteMultipleRegisters(ModbusRequest request, ModbusDeviceBinding device)
    {
        if (request.Quantity is 0 or > MaxWriteRegisters)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        if (request.Payload.Length != request.Quantity * 2)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        if (!device.Space.TryWriteRegisters(request.Address, request.Payload))
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        device.RegistersWritten?.Invoke(request.Address, request.Payload);
        return ModbusFrames.BuildWriteResponse(
            request.TransactionId, request.UnitId, request.FunctionCode, request.Address, request.Quantity);
    }

    private static byte[] ReadWriteMultipleRegisters(ModbusRequest request, ModbusDeviceBinding device)
    {
        if (request.Quantity is 0 or > MaxReadRegisters)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        if (request.WriteQuantity is 0 or > MaxWriteRegistersInReadWrite)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        if (request.Payload.Length != request.WriteQuantity * 2)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        if (!device.Space.TryWriteRegisters(request.WriteAddress, request.Payload))
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        device.RegistersWritten?.Invoke(request.WriteAddress, request.Payload);
        byte[] data = new byte[request.Quantity * 2];
        if (!device.Space.TryReadRegisters(request.Address, request.Quantity, data))
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        return ModbusFrames.BuildReadResponse(
            request.TransactionId, request.UnitId, request.FunctionCode, data);
    }

    private static byte[] ReadBits(ModbusRequest request, ModbusDeviceBinding device)
    {
        if (request.Quantity is 0 or > MaxReadCoils)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        int firstByte = request.Address / 8;
        int lastBitExclusive = request.Address + request.Quantity;
        int byteCount = ((lastBitExclusive + 7) / 8) - firstByte;

        byte[] raw = new byte[byteCount];
        if (!device.Space.TryReadBytes(firstByte, raw))
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        byte[] packed = new byte[(request.Quantity + 7) / 8];
        for (int i = 0; i < request.Quantity; i++)
        {
            int bitAddress = request.Address + i;
            byte source = raw[(bitAddress / 8) - firstByte];
            if ((source & (1 << (bitAddress % 8))) != 0)
            {
                packed[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return ModbusFrames.BuildReadResponse(
            request.TransactionId, request.UnitId, request.FunctionCode, packed);
    }

    private static byte[] WriteSingleCoil(ModbusRequest request, ModbusDeviceBinding device)
    {
        if (request.Payload.Length != 2)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        ushort value = ModbusFrames.ReadUInt16BigEndian(request.Payload, 0);
        if (value is not (0xFF00 or 0x0000))
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        if (!device.Space.TryWriteBit(request.Address, value == 0xFF00))
        {
            return Exception(request, ModbusExceptionCode.IllegalDataAddress);
        }

        return ModbusFrames.BuildWriteResponse(
            request.TransactionId, request.UnitId, request.FunctionCode, request.Address, value);
    }

    private static byte[] WriteMultipleCoils(ModbusRequest request, ModbusDeviceBinding device)
    {
        if (request.Quantity is 0 or > MaxWriteCoils)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        if (request.Payload.Length != (request.Quantity + 7) / 8)
        {
            return Exception(request, ModbusExceptionCode.IllegalDataValue);
        }

        // 先整批校验地址，再统一落写：否则中途越界时前面的位已经写进去了、却回异常响应。
        // 真实设备是整批校验后整体拒绝。
        for (int i = 0; i < request.Quantity; i++)
        {
            if (!device.Space.CanWriteBit(request.Address + i))
            {
                return Exception(request, ModbusExceptionCode.IllegalDataAddress);
            }
        }

        for (int i = 0; i < request.Quantity; i++)
        {
            bool on = (request.Payload[i / 8] & (1 << (i % 8))) != 0;
            device.Space.TryWriteBit(request.Address + i, on);
        }

        return ModbusFrames.BuildWriteResponse(
            request.TransactionId, request.UnitId, request.FunctionCode, request.Address, request.Quantity);
    }

    private static byte[] Exception(ModbusRequest request, ModbusExceptionCode code)
        => ModbusFrames.BuildException(request.TransactionId, request.UnitId, request.FunctionCode, code);
}
