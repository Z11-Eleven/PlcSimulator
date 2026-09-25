using PlcSimulator.Core.Registers;

namespace PlcSimulator.Protocol.Modbus;

/// <summary>地址反查结果，供日志与诊断使用。</summary>
/// <param name="Target">形如 "1001.tasknum" 的目标描述。</param>
/// <param name="ExpectsByteSwap">该字段是否落在「约定需要高低位转换」的区间内。</param>
public sealed record AddressHint(string Target, bool ExpectsByteSwap);

/// <summary>一个监听端点上可供访问的一台从站设备。</summary>
public sealed record ModbusDeviceBinding(
    string DeviceId,
    byte SlaveId,
    IRegisterSpace Space,
    /// <summary>把寄存器地址翻译成站台与字段，仅用于日志与诊断。</summary>
    Func<ushort, AddressHint?>? AddressDescriber = null);

/// <summary>
/// 把一个监听端点映射到它承载的设备。因为 WCS 把端口硬编码为 502，
/// 同机多设备靠不同的回环 IP（127.0.0.x）区分，所以解析的粒度是「监听端点」。
/// </summary>
public interface IModbusDeviceResolver
{
    IReadOnlyList<ModbusDeviceBinding> Devices { get; }

    /// <summary>
    /// 是否接受任意站号。WCS 的闭源 DLL 可能把站台号当作单元标识发出来，
    /// 因此单设备端点通常需要容忍不匹配的站号。
    /// </summary>
    bool AcceptAnySlaveId { get; }

    ModbusDeviceBinding? Find(byte unitId);
}

public sealed class StaticDeviceResolver(IEnumerable<ModbusDeviceBinding> devices, bool acceptAnySlaveId)
    : IModbusDeviceResolver
{
    public IReadOnlyList<ModbusDeviceBinding> Devices { get; } = [.. devices];

    public bool AcceptAnySlaveId { get; } = acceptAnySlaveId;

    public ModbusDeviceBinding? Find(byte unitId)
    {
        foreach (ModbusDeviceBinding device in Devices)
        {
            if (device.SlaveId == unitId)
            {
                return device;
            }
        }

        return AcceptAnySlaveId && Devices.Count > 0 ? Devices[0] : null;
    }
}
