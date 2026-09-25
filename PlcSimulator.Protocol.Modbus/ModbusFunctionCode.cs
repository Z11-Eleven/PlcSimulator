namespace PlcSimulator.Protocol.Modbus;

/// <summary>
/// Modbus 功能码。WCS 实际只用 FC3（读）与 FC6/FC16（写），
/// 其余一并实现，以便闭源 DLL 的行为与预期不符时无需返工。
/// </summary>
public enum ModbusFunctionCode : byte
{
    ReadCoils = 0x01,
    ReadDiscreteInputs = 0x02,
    ReadHoldingRegisters = 0x03,
    ReadInputRegisters = 0x04,
    WriteSingleCoil = 0x05,
    WriteSingleRegister = 0x06,
    WriteMultipleCoils = 0x0F,
    WriteMultipleRegisters = 0x10,
    ReadWriteMultipleRegisters = 0x17,
}
