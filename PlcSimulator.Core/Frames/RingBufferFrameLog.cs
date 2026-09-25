namespace PlcSimulator.Core.Frames;

/// <summary>
/// 固定容量的环形缓冲，用于高频报文日志。超出容量时覆盖最旧的记录，不阻塞写入方。
/// </summary>
public sealed class RingBufferFrameLog : IFrameLog
{
    private readonly FrameLogEntry[] _buffer;
    private readonly object _sync = new();
    private int _next;
    private int _count;
    private long _totalWritten;

    public RingBufferFrameLog(int capacity = 200_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = new FrameLogEntry[capacity];
    }

    public event EventHandler<FrameLogEntry>? EntryAdded;

    public int Capacity => _buffer.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>累计写入过的条数（单调递增），配合 <see cref="TakeSince"/> 做增量拉取。</summary>
    public long TotalWritten
    {
        get
        {
            lock (_sync)
            {
                return _totalWritten;
            }
        }
    }

    public void Add(in FrameLogEntry entry)
    {
        lock (_sync)
        {
            _buffer[_next] = entry;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }

            _totalWritten++;
        }

        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>
    /// 取 <paramref name="version"/> 之后的新条目，最多 <paramref name="maxItems"/> 条。
    /// 若 version 已被环形覆盖，则从当前最旧的条目开始，保证调用方不会卡在旧位置上。
    /// </summary>
    public FrameLogEntry[] TakeSince(long version, int maxItems, out long nextVersion)
    {
        lock (_sync)
        {
            long oldest = _totalWritten - _count;
            long from = Math.Max(version, oldest);
            int take = (int)Math.Min(_totalWritten - from, maxItems);

            if (take <= 0)
            {
                nextVersion = _totalWritten;
                return [];
            }

            int start = (int)(from - oldest);
            FrameLogEntry[] result = new FrameLogEntry[take];
            for (int i = 0; i < take; i++)
            {
                result[i] = _buffer[(start + i) % _buffer.Length];
            }

            nextVersion = from + take;
            return result;
        }
    }

    /// <summary>按时间顺序（最旧在前）取一份快照。</summary>
    public FrameLogEntry[] Snapshot()
        => TakeSince(0, int.MaxValue, out _);

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_buffer);
            _next = 0;
            _count = 0;
            _totalWritten = 0;
        }
    }
}
