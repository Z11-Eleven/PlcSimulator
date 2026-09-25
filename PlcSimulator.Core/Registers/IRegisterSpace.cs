namespace PlcSimulator.Core.Registers;

/// <summary>
/// 设备寄存器数据区的统一字节视图。
/// Modbus 侧以「寄存器地址 × 2 = 字节偏移」访问，业务侧直接按字节偏移访问。
/// </summary>
public interface IRegisterSpace
{
    string DeviceId { get; }

    /// <summary>每发生一次写入递增，供 GUI 判断是否需要刷新。</summary>
    long Version { get; }

    /// <summary>已配置的字节范围 [起, 止)，用于诊断「写越界」到底差了多少。</summary>
    IReadOnlyList<(int Start, int End)> ConfiguredByteRanges { get; }

    event EventHandler<RangeChangedEventArgs>? RangeChanged;

    /// <summary>
    /// 按寄存器地址读取。落在已配置块之外的部分以零填充（WCS 会做过量读，这是正常行为）。
    /// 仅在参数非法时返回 false。
    /// </summary>
    bool TryReadRegisters(int registerAddress, int registerCount, Span<byte> destination);

    /// <summary>
    /// 按寄存器地址写入。目标范围必须被已配置块完整覆盖，否则返回 false（对应 Modbus 异常码 0x02）。
    /// </summary>
    bool TryWriteRegisters(int registerAddress, ReadOnlySpan<byte> source);

    /// <summary>按字节偏移读取（业务层与位操作使用）。块外以零填充。</summary>
    bool TryReadBytes(int byteOffset, Span<byte> destination);

    /// <summary>按字节偏移写入（业务层与位操作使用）。</summary>
    bool TryWriteBytes(int byteOffset, ReadOnlySpan<byte> source);

    /// <summary>
    /// 读-改-写一个位（线圈）。必须在实现内部一次完成：分成「读字节 + 改 + 写字节」两步的话，
    /// 相邻位被另一条连接并发写时会丢掉更新。
    /// </summary>
    bool TryWriteBit(int bitAddress, bool value);

    /// <summary>位地址是否落在已配置块内。整批写位前先整体校验，避免写了一半才报越界。</summary>
    bool CanWriteBit(int bitAddress);
}

public sealed class RangeChangedEventArgs(int byteOffset, int lengthBytes) : EventArgs
{
    public int ByteOffset { get; } = byteOffset;
    public int LengthBytes { get; } = lengthBytes;
}
