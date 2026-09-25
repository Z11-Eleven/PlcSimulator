namespace PlcSimulator.Protocol.Modbus;

/// <summary>
/// 解析后的 Modbus 请求。对于读写组合功能码（FC23），<see cref="Address"/>/<see cref="Quantity"/>
/// 描述读部分，<see cref="WriteAddress"/>/<see cref="WriteQuantity"/> 描述写部分。
/// </summary>
public readonly record struct ModbusRequest(
    ushort TransactionId,
    byte UnitId,
    ModbusFunctionCode FunctionCode,
    ushort Address,
    ushort Quantity,
    ushort WriteAddress,
    ushort WriteQuantity,
    byte[] Payload)
{
    /// <summary>本请求是否含写操作。FC23 是读+写组合，同样算写——否则首帧为它时连接会被判成读角色。</summary>
    public bool IsWrite => FunctionCode is ModbusFunctionCode.WriteSingleCoil
        or ModbusFunctionCode.WriteSingleRegister
        or ModbusFunctionCode.WriteMultipleCoils
        or ModbusFunctionCode.WriteMultipleRegisters
        or ModbusFunctionCode.ReadWriteMultipleRegisters;
}
