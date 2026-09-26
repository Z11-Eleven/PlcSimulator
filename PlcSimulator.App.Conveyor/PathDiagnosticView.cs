using System.Text;
using PlcSimulator.Devices;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Devices.Topology;
using PlcSimulator.Hosting;

namespace PlcSimulator.App.Conveyor;

/// <summary>
/// 「路径诊断」页：填起点与目标站台号，把寻路过程摊开 —— 逐跳选了哪个邻居、
/// 其它候选为什么没选、按配置要花多久、当前状态下会停在哪、走不通时断在哪一环。
/// <para>
/// 与命令行的 <c>path --detail</c> 共用同一套诊断代码（<see cref="PathDiagnostics"/>），
/// 因此两边结论必然一致。**只读**：不下发任务、不碰数据区，随时可反复查。
/// </para>
/// </summary>
public sealed class PathDiagnosticView : UserControl
{
    private const int Pad = 12;
    private const int RowHeight = 26;
    private const int Gap = 8;
    private const int InputWidth = 110;
    private const int ButtonWidth = 88;

    private readonly Panel _form = new() { Dock = DockStyle.Top };
    private readonly TextBox _output = new();

    private readonly Label _fromLabel = new() { Text = "起点站台", AutoSize = true };
    private readonly TextBox _from = new();
    private readonly Label _toLabel = new() { Text = "目标站台", AutoSize = true };
    private readonly TextBox _to = new();
    private readonly Button _diagnose = new() { Text = "诊断" };

    private readonly Label _hint = new()
    {
        Text = "填站台号后回车或点「诊断」。只读分析，不会下发任务、不改动现场状态。",
        AutoSize = true,
        ForeColor = Color.FromArgb(130, 130, 130),
    };

    private SimulatorHost? _host;
    private bool _layingOut;

    public PathDiagnosticView()
    {
        Dock = DockStyle.Fill;

        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Both;
        _output.WordWrap = false;
        _output.BackColor = Color.White;
        _output.Dock = DockStyle.Fill;

        _from.KeyDown += OnInputKeyDown;
        _to.KeyDown += OnInputKeyDown;
        _diagnose.Click += (_, _) => Diagnose();

        foreach (Control control in new Control[] { _fromLabel, _from, _toLabel, _to, _diagnose, _hint })
        {
            _form.Controls.Add(control);
        }

        // Dock 布局按 z 序从后往前处理：先加 Fill 的输出区，再加 Dock=Top 的输入区。
        Controls.Add(_output);
        Controls.Add(_form);

        _form.Layout += (_, _) => LayoutForm();
        LayoutForm();
    }

    /// <summary>换配置后由主窗体重新调用；诊断本身不再依赖旧的 host。</summary>
    public void Bind(SimulatorHost host)
    {
        _host = host;
        _output.Clear();
    }

    private void LayoutForm()
    {
        if (_layingOut)
        {
            return;
        }

        _layingOut = true;

        try
        {
            int y = Pad;
            int x = Pad;

            _fromLabel.Location = new Point(x, y + 4);
            x += _fromLabel.PreferredWidth + Gap;

            _from.Location = new Point(x, y);
            _from.Width = InputWidth;
            x += _from.Width + Gap * 3;

            _toLabel.Location = new Point(x, y + 4);
            x += _toLabel.PreferredWidth + Gap;

            _to.Location = new Point(x, y);
            _to.Width = InputWidth;
            x += _to.Width + Gap * 3;

            _diagnose.Location = new Point(x, y - 1);
            _diagnose.Width = ButtonWidth;

            _hint.Location = new Point(Pad, y + RowHeight + 6);

            _form.Height = Pad + RowHeight + _hint.PreferredHeight + Pad;
        }
        finally
        {
            _layingOut = false;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        // 否则会响一声系统提示音。
        e.SuppressKeyPress = true;
        Diagnose();
    }

    private void Diagnose()
    {
        if (_host is null)
        {
            _output.Text = "还没有加载配置。先在工具栏「打开配置…」里选一份。"
                + Environment.NewLine;
            return;
        }

        string from = _from.Text.Trim();
        string to = _to.Text.Trim();

        if (from.Length == 0 || to.Length == 0)
        {
            _output.Text = "请先填起点与目标站台号。"
                + Environment.NewLine;
            return;
        }

        var text = new StringBuilder();

        if (!_host.IsRunning)
        {
            text.AppendLine("（服务未启动：实时状态取自初值，不代表真实运行）");
            text.AppendLine();
        }

        IReadOnlyList<StationSnapshot> snapshots = _host.Engine.Snapshots();

        foreach (DeviceRuntime device in _host.Devices)
        {
            Dictionary<string, StationSnapshot> lookup =
                PathDiagnostics.BuildSnapshotLookup(device, snapshots);

            PathDiagnosticReport report = PathDiagnostics.Analyze(
                device.Config,
                device.Topology,
                from,
                to,
                lookup,
                _host.Engine.TickInterval);

            text.Append(report.ToText());
        }

        if (_host.Devices.Count == 0)
        {
            text.AppendLine("当前配置里没有启用的设备。");
        }

        _output.Text = text.ToString();
        _output.SelectionStart = 0;
        _output.ScrollToCaret();
    }
}
