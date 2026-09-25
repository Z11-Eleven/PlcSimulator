namespace PlcSimulator.Core.Registers;

/// <summary>
/// 一段连续的寄存器字节区。WCS 按组批量读，组内地址连续，因此块内用稠密数组。
/// </summary>
public sealed class RegisterBlock
{
    public RegisterBlock(string group, int baseByteOffset, int lengthBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseByteOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);

        Group = group;
        BaseByteOffset = baseByteOffset;
        LengthBytes = lengthBytes;
        Data = new byte[lengthBytes];
    }

    public string Group { get; }

    /// <summary>字节偏移，对应数据库 wcs_opcitem.value 的语义。</summary>
    public int BaseByteOffset { get; }

    public int LengthBytes { get; }

    public int EndByteOffset => BaseByteOffset + LengthBytes;

    internal byte[] Data { get; }
}
