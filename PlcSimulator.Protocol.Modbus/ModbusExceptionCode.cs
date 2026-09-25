namespace PlcSimulator.Protocol.Modbus;

/// <summary>
/// Modbus 异常码。取值与 WCS 所用闭源 DLL 内部枚举的语义对齐
/// （FunctionCodeNotSupport / DataAddressNotSupport / DataValueNotSupport / DeviceBusy / GatewayDeviceResponseTimeout）。
/// </summary>
public enum ModbusExceptionCode : byte
{
    /// <summary>功能码不支持。</summary>
    IllegalFunction = 0x01,

    /// <summary>数据地址不支持。</summary>
    IllegalDataAddress = 0x02,

    /// <summary>数据值不支持（数量越界、字节数与数量不匹配等）。</summary>
    IllegalDataValue = 0x03,

    /// <summary>从站设备故障。</summary>
    ServerDeviceFailure = 0x04,

    /// <summary>已接受，正在处理。</summary>
    Acknowledge = 0x05,

    /// <summary>从站设备忙。</summary>
    ServerDeviceBusy = 0x06,

    /// <summary>网关路径不可用。</summary>
    GatewayPathUnavailable = 0x0A,

    /// <summary>网关目标设备无响应。</summary>
    GatewayTargetDeviceFailedToRespond = 0x0B,
}
