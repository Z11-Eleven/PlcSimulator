namespace PlcSimulator.Core.Registers;

/// <summary>
/// 记录自上次取走以来被改动过的字节区间，供 GUI 只刷新变化的行而不是全表。
/// 区间按到达顺序追加，取走时做一次合并。
/// </summary>
public sealed class DirtyRangeTracker
{
    /// <summary>
    /// 攒到这个条数就地合并一次。只有点位监视表会来取，CLI 模式（长期跑回归）下没人取，
    /// 不设上限的话会一直涨。
    /// </summary>
    private const int CompactThreshold = 4096;

    private readonly object _sync = new();
    private readonly List<(int Start, int End)> _ranges = [];

    public void Mark(int byteOffset, int lengthBytes)
    {
        if (lengthBytes <= 0)
        {
            return;
        }

        lock (_sync)
        {
            _ranges.Add((byteOffset, byteOffset + lengthBytes));

            if (_ranges.Count >= CompactThreshold)
            {
                Compact();
            }
        }
    }

    /// <summary>取走并清空已记录的区间，返回合并后的结果。</summary>
    public List<(int Start, int End)> TakeMerged()
    {
        lock (_sync)
        {
            if (_ranges.Count == 0)
            {
                return [];
            }

            Compact();

            List<(int Start, int End)> merged = [.. _ranges];
            _ranges.Clear();
            return merged;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _ranges.Clear();
        }
    }

    /// <summary>就地排序并合并相邻/重叠的区间。调用方必须已持有锁。</summary>
    private void Compact()
    {
        if (_ranges.Count < 2)
        {
            return;
        }

        _ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        int write = 0;
        for (int read = 1; read < _ranges.Count; read++)
        {
            if (_ranges[read].Start <= _ranges[write].End)
            {
                _ranges[write] = (_ranges[write].Start, Math.Max(_ranges[write].End, _ranges[read].End));
            }
            else
            {
                _ranges[++write] = _ranges[read];
            }
        }

        _ranges.RemoveRange(write + 1, _ranges.Count - write - 1);
    }
}
