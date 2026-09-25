namespace PlcSimulator.Core.ByteOrder;

using PlcSimulator.Core.Protocols;

public enum WordByteOrder
{
    BigEndian,
    LittleEndian,
}

/// <summary>
/// 线路字节与内部字节之间的变换规则。
/// <para>
/// WCS 对字节序的处理**逐调用点各不相同**，没有任何全局统一规则：
/// 输送线读只对条码区间做字交换、输送线整块写从第 6 字节起交换、
/// 输送线单字段写完全不交换、堆垛机从第 4 字节起交换、机械手完全不交换。
/// 因此把它做成配置项而不是代码分支。
/// </para>
/// <para>
/// 内部字节 = 业务含义上的字节（大端整数、正序 ASCII）；
/// 线路字节 = 实际在 Modbus 上收发的字节。
/// </para>
/// </summary>
public sealed class ByteOrderPolicy
{
    public static readonly ByteOrderPolicy Identity = new() { Name = "Identity" };

    public string Name { get; init; } = "Unnamed";

    /// <summary>寄存器内 2 字节的顺序。默认大端。</summary>
    public WordByteOrder WordByteOrder { get; init; } = WordByteOrder.BigEndian;

    /// <summary>从该字节偏移起，每 2 字节反序。null 表示不做整体反序。</summary>
    public int? SwapFromByteOffset { get; init; }

    /// <summary>额外需要反序的区间 [start, end)，用于像条码这种与相邻字段规则不同的字段。</summary>
    public IReadOnlyList<int[]> ExtraPairUnswapRanges { get; init; } = [];

    public bool IsIdentity => WordByteOrder == WordByteOrder.BigEndian
        && SwapFromByteOffset is null
        && ExtraPairUnswapRanges.Count == 0;

    /// <summary>
    /// 对缓冲区施加变换。该变换是自逆的（施加两次等于不变），
    /// 因此读方向与写方向共用同一实现；若将来出现非自逆策略，
    /// <see cref="EncodeForWire"/> 与 <see cref="DecodeFromWire"/> 可分别覆盖。
    /// </summary>
    public void Transform(Span<byte> buffer)
    {
        if (buffer.Length < 2)
        {
            return;
        }

        if (SwapFromByteOffset is int from && from >= 0 && from < buffer.Length)
        {
            PairReverse(buffer[from..]);
        }

        foreach (int[] range in ExtraPairUnswapRanges)
        {
            if (range.Length < 2)
            {
                continue;
            }

            int start = Math.Max(0, range[0]);
            int end = Math.Min(range[1], buffer.Length);
            if (end - start >= 2)
            {
                PairReverse(buffer[start..end]);
            }
        }
    }

    /// <summary>内部表示 → 线路字节（读方向：模拟器回帧给 WCS）。</summary>
    public void EncodeForWire(Span<byte> buffer) => Transform(buffer);

    /// <summary>线路字节 → 内部表示（写方向：模拟器收到 WCS 的写入）。</summary>
    public void DecodeFromWire(Span<byte> buffer) => Transform(buffer);

    /// <summary>
    /// 判断某个字段是否落在「需要字交换」的区域内。
    /// 字段边界与变换对齐一致（偶数偏移、偶数宽度）时，变换可以按字段独立判断。
    /// </summary>
    public bool CoversField(FieldDescriptor field)
    {
        if (SwapFromByteOffset is int from
            && field.ByteOffset >= from
            && (field.ByteOffset - from) % 2 == 0)
        {
            return true;
        }

        foreach (int[] range in ExtraPairUnswapRanges)
        {
            if (range.Length >= 2
                && field.ByteOffset >= range[0]
                && field.EndByteOffset <= range[1])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从 <paramref name="from"/> 起把每 2 字节互换，等价于 WCS 里的
    /// <c>GroupBy(i / 2).SelectMany([Last, First])</c>。
    /// </summary>
    private static void PairReverse(Span<byte> buffer)
    {
        for (int i = 0; i + 1 < buffer.Length; i += 2)
        {
            (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
        }
    }
}
