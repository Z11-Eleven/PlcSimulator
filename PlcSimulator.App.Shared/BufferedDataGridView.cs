namespace PlcSimulator.App.Shared;

/// <summary>
/// 打开双缓冲的 DataGridView。
/// <see cref="Control.DoubleBuffered"/> 是 protected，必须派生才能设置；
/// 不打开的话，2035 个点位的高频刷新会明显闪烁。
/// </summary>
public sealed class BufferedDataGridView : DataGridView
{
    public BufferedDataGridView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        EnableHeadersVisualStyles = false;
    }
}
