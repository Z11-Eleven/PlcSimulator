using PlcSimulator.Devices;

namespace PlcSimulator.App.Conveyor;

/// <summary>两张站台表共用的搜索入口。</summary>
internal sealed class DeviceSearchBar : ToolStrip
{
    private readonly ToolStripTextBox _input = new() { AutoSize = false, Width = 280 };
    private readonly ToolStripLabel _result = new();
    private string _query = string.Empty;

    public DeviceSearchBar()
    {
        Dock = DockStyle.Top;
        GripStyle = ToolStripGripStyle.Hidden;
        _input.ToolTipText = "输入设备编号、设备名称、站台号或站台名称，支持部分匹配";
        _input.TextBox.PlaceholderText = "设备编号 / 站台号 / 名称";
        var clear = new ToolStripButton("清除搜索");
        clear.Click += (_, _) => _input.Clear();
        _input.TextChanged += (_, _) =>
        {
            _query = _input.Text.Trim();
            QueryChanged?.Invoke(this, EventArgs.Empty);
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                _input.Clear();
                e.SuppressKeyPress = true;
            }
        };
        Items.AddRange([new ToolStripLabel("搜索设备"), _input, clear, new ToolStripSeparator(), _result]);
    }

    public event EventHandler? QueryChanged;

    public bool Matches(StationRuntime station)
        => _query.Length == 0
            || Contains(station.Device.Config.Id)
            || Contains(station.Device.Config.Name)
            || Contains(station.StationNo)
            || Contains(station.Name)
            || Contains(station.PrimaryStationNo);

    public void ShowResult(int visible, int total, string unit)
        => _result.Text = visible == 0 && total > 0
            ? $"未找到匹配设备（共 {total} {unit}）"
            : $"显示 {visible} / {total} {unit}";

    private bool Contains(string value) => value.Contains(_query, StringComparison.OrdinalIgnoreCase);
}
