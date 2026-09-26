using System.Text;
using PlcSimulator.Core;
using PlcSimulator.Core.Frames;

namespace PlcSimulator.App.Shared;

/// <summary>
/// 报文日志。数据来自环形缓冲的**增量拉取**（<see cref="RingBufferFrameLog.TakeSince"/>），
/// 因此 100ms 轮询下每秒几百条也不会拖慢 UI；显示用虚拟列表，只渲染可见行。
/// </summary>
public sealed class FrameLogView : UserControl
{
    private const int MaxRendered = 20_000;

    private readonly ListView _list = new();
    private readonly List<FrameLogEntry> _entries = [];
    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _clearButton = new("清空");
    private readonly ToolStripButton _exportButton = new("导出 CSV…");
    private readonly ToolStripButton _autoScrollButton = new("自动滚动") { CheckOnClick = true, Checked = true };

    public FrameLogView()
    {
        Dock = DockStyle.Fill;
        BuildLayout();
    }

    /// <summary>追加一批新条目，超出上限时丢弃最旧的。</summary>
    public void Append(FrameLogEntry[] batch)
    {
        if (batch.Length == 0)
        {
            return;
        }

        _entries.AddRange(batch);

        if (_entries.Count > MaxRendered)
        {
            _entries.RemoveRange(0, _entries.Count - MaxRendered);
        }

        _list.VirtualListSize = _entries.Count;

        if (_autoScrollButton.Checked && _entries.Count > 0)
        {
            _list.EnsureVisible(_entries.Count - 1);
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _list.VirtualListSize = 0;
    }

    private void BuildLayout()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.Items.Add(_clearButton);
        _toolStrip.Items.Add(_exportButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_autoScrollButton);

        _clearButton.Click += (_, _) => Clear();
        _exportButton.Click += (_, _) => ExportWithDialog();

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.VirtualMode = true;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;

        _list.Columns.Add("时间", 110);
        _list.Columns.Add("方向", 50);
        _list.Columns.Add("连接", 170);
        _list.Columns.Add("设备", 100);
        _list.Columns.Add("摘要", 420);
        _list.Columns.Add("原始字节", 260);
        _list.Columns.Add("耗时ms", 70);

        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;

        Controls.Add(_list);
        Controls.Add(_toolStrip);
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _entries.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        FrameLogEntry entry = _entries[e.ItemIndex];

        e.Item = new ListViewItem(
        [
            entry.Timestamp.ToString("HH:mm:ss.fff"),
            entry.DirectionText,
            entry.ConnectionId,
            entry.DeviceId,
            entry.Summary,
            entry.Raw is null ? string.Empty : HexFormat.ToHex(entry.Raw, 48),
            entry.LatencyMs.ToString("F2"),
        ]);
    }

    private void ExportWithDialog()
    {
        using SaveFileDialog dialog = FileDialogs.Save(
            "导出报文日志",
            "CSV 文件|*.csv|文本文件|*.txt");

        dialog.FileName = $"frames-{DateTime.Now:yyyyMMdd-HHmmss}.csv";

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ExportTo(dialog.FileName);
            MessageBox.Show(this, $"已导出 {_entries.Count} 条到：\n{dialog.FileName}", "导出完成");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportTo(string path)
    {
        // 带 BOM：不带的话 Excel 会按本地代码页解释，中文摘要列全是乱码。
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("时间,方向,连接,设备,摘要,原始字节,耗时ms");

        foreach (FrameLogEntry entry in _entries)
        {
            writer.Write(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            writer.Write(',');
            writer.Write(entry.DirectionText);
            writer.Write(',');
            writer.Write(Escape(entry.ConnectionId));
            writer.Write(',');
            writer.Write(Escape(entry.DeviceId));
            writer.Write(',');
            writer.Write(Escape(entry.Summary));
            writer.Write(',');
            writer.Write(entry.Raw is null ? string.Empty : HexFormat.ToHex(entry.Raw));
            writer.Write(',');
            writer.Write(entry.LatencyMs.ToString("F2"));
            writer.WriteLine();
        }
    }

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
