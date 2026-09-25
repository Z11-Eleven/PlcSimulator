namespace PlcSimulator.Core.Registers;

/// <summary>
/// 单台设备的寄存器数据区。块内稠密、块间稀疏，跨块请求逐块拷贝、空隙填零。
/// </summary>
public sealed class DeviceRegisterSpace : IRegisterSpace
{
    private readonly object _sync = new();
    private readonly RegisterBlock[] _blocks;
    private readonly DirtyRangeTracker _dirty = new();
    private long _version;

    public DeviceRegisterSpace(string deviceId, IEnumerable<RegisterBlock> blocks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(blocks);

        DeviceId = deviceId;
        _blocks = [.. blocks.OrderBy(static b => b.BaseByteOffset)];
        ValidateNoOverlap(_blocks);
        ConfiguredByteRanges = [.. _blocks.Select(static b => (b.BaseByteOffset, b.EndByteOffset))];
    }

    public string DeviceId { get; }

    public long Version => Interlocked.Read(ref _version);

    public IReadOnlyList<RegisterBlock> Blocks => _blocks;

    public IReadOnlyList<(int Start, int End)> ConfiguredByteRanges { get; }

    public event EventHandler<RangeChangedEventArgs>? RangeChanged;

    /// <summary>取走自上次调用以来变更过的字节区间，供 GUI 增量刷新。</summary>
    public List<(int Start, int End)> TakeDirtyRanges() => _dirty.TakeMerged();

    public bool TryReadRegisters(int registerAddress, int registerCount, Span<byte> destination)
    {
        if (registerAddress < 0 || registerCount < 0
            || registerAddress > ushort.MaxValue || registerCount > ushort.MaxValue)
        {
            return false;
        }

        int start = registerAddress * 2;
        int length = registerCount * 2;
        if (destination.Length < length)
        {
            return false;
        }

        ReadBytesCore(start, destination[..length]);
        return true;
    }

    public bool TryWriteRegisters(int registerAddress, ReadOnlySpan<byte> source)
    {
        if (registerAddress < 0 || registerAddress > ushort.MaxValue)
        {
            return false;
        }

        // 寄存器写入必须是整字，否则无法落到对齐的寄存器上。
        if (source.Length == 0 || source.Length % 2 != 0)
        {
            return false;
        }

        return TryWriteBytesCore(registerAddress * 2, source, requireFullCoverage: true);
    }

    /// <summary>按字节偏移读取（业务层/GUI 用）。块外以零填充。</summary>
    public bool TryReadBytes(int byteOffset, Span<byte> destination)
    {
        if (byteOffset < 0)
        {
            return false;
        }

        ReadBytesCore(byteOffset, destination);
        return true;
    }

    /// <summary>按字节偏移写入（业务层/GUI 用）。允许块外部分被忽略，只要有一段落在块内。</summary>
    public bool TryWriteBytes(int byteOffset, ReadOnlySpan<byte> source)
        => byteOffset >= 0 && TryWriteBytesCore(byteOffset, source, requireFullCoverage: false);

    /// <summary>把整块清零（业务状态机复位站台时用）。</summary>
    public bool TryClearRange(int byteOffset, int lengthBytes)
    {
        if (byteOffset < 0 || lengthBytes <= 0)
        {
            return false;
        }

        byte[] zeros = new byte[lengthBytes];
        return TryWriteBytes(byteOffset, zeros);
    }

    /// <summary>
    /// 读-改-写一个位。整段在锁内完成，跨连接并发写相邻位时不会丢更新。
    /// </summary>
    public bool TryWriteBit(int bitAddress, bool value)
    {
        if (bitAddress < 0)
        {
            return false;
        }

        int byteOffset = bitAddress / 8;
        byte mask = (byte)(1 << (bitAddress % 8));

        lock (_sync)
        {
            if (!TryLocateBlock(byteOffset, out RegisterBlock block))
            {
                return false;
            }

            int offset = byteOffset - block.BaseByteOffset;
            block.Data[offset] = value
                ? (byte)(block.Data[offset] | mask)
                : (byte)(block.Data[offset] & ~mask);

            _dirty.Mark(byteOffset, 1);
            Interlocked.Increment(ref _version);
        }

        RangeChanged?.Invoke(this, new RangeChangedEventArgs(byteOffset, 1));
        return true;
    }

    /// <summary>位地址是否落在已配置块内。</summary>
    public bool CanWriteBit(int bitAddress)
    {
        if (bitAddress < 0)
        {
            return false;
        }

        lock (_sync)
        {
            return TryLocateBlock(bitAddress / 8, out _);
        }
    }

    /// <summary>定位某个字节所属的块。调用方必须已持锁。</summary>
    private bool TryLocateBlock(int byteOffset, out RegisterBlock block)
    {
        int index = FindFirstOverlapping(byteOffset);

        if (index < _blocks.Length && byteOffset < _blocks[index].EndByteOffset)
        {
            block = _blocks[index];
            return true;
        }

        block = null!;
        return false;
    }

    private void ReadBytesCore(int start, Span<byte> destination)
    {
        int length = destination.Length;
        if (length == 0)
        {
            return;
        }

        int end = start + length;
        lock (_sync)
        {
            destination.Clear();

            for (int i = FindFirstOverlapping(start); i < _blocks.Length; i++)
            {
                RegisterBlock block = _blocks[i];
                if (block.BaseByteOffset >= end)
                {
                    break;
                }

                int copyStart = Math.Max(block.BaseByteOffset, start);
                int copyEnd = Math.Min(block.EndByteOffset, end);
                if (copyEnd <= copyStart)
                {
                    continue;
                }

                block.Data
                    .AsSpan(copyStart - block.BaseByteOffset, copyEnd - copyStart)
                    .CopyTo(destination[(copyStart - start)..]);
            }
        }
    }

    private bool TryWriteBytesCore(int start, ReadOnlySpan<byte> source, bool requireFullCoverage)
    {
        int length = source.Length;
        if (length == 0)
        {
            return true;
        }

        int end = start + length;
        int first = FindFirstOverlapping(start);

        lock (_sync)
        {
            int covered = 0;
            for (int i = first; i < _blocks.Length; i++)
            {
                RegisterBlock block = _blocks[i];
                if (block.BaseByteOffset >= end)
                {
                    break;
                }

                int overlapStart = Math.Max(block.BaseByteOffset, start);
                int overlapEnd = Math.Min(block.EndByteOffset, end);
                if (overlapEnd > overlapStart)
                {
                    covered += overlapEnd - overlapStart;
                }
            }

            if (requireFullCoverage && covered != length)
            {
                return false;
            }

            if (covered == 0)
            {
                return false;
            }

            for (int i = first; i < _blocks.Length; i++)
            {
                RegisterBlock block = _blocks[i];
                if (block.BaseByteOffset >= end)
                {
                    break;
                }

                int overlapStart = Math.Max(block.BaseByteOffset, start);
                int overlapEnd = Math.Min(block.EndByteOffset, end);
                if (overlapEnd <= overlapStart)
                {
                    continue;
                }

                source[(overlapStart - start)..(overlapEnd - start)]
                    .CopyTo(block.Data.AsSpan(overlapStart - block.BaseByteOffset, overlapEnd - overlapStart));
            }

            _dirty.Mark(start, length);
            Interlocked.Increment(ref _version);
        }

        RangeChanged?.Invoke(this, new RangeChangedEventArgs(start, length));
        return true;
    }

    /// <summary>二分找到第一个可能与 [start, ...) 相交的块。</summary>
    private int FindFirstOverlapping(int start)
    {
        int low = 0;
        int high = _blocks.Length - 1;
        int result = _blocks.Length;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (_blocks[mid].EndByteOffset > start)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return result;
    }

    private static void ValidateNoOverlap(RegisterBlock[] blocks)
    {
        for (int i = 1; i < blocks.Length; i++)
        {
            if (blocks[i].BaseByteOffset < blocks[i - 1].EndByteOffset)
            {
                throw new ArgumentException(
                    $"寄存器块区间重叠：[{blocks[i - 1].BaseByteOffset},{blocks[i - 1].EndByteOffset}) "
                    + $"与 [{blocks[i].BaseByteOffset},{blocks[i].EndByteOffset})。");
            }
        }
    }
}
