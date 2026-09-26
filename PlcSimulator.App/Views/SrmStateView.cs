using PlcSimulator.App.Controls;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices;
using PlcSimulator.Devices.Srm;
using PlcSimulator.Hosting;

namespace PlcSimulator.App.Views;

/// <summary>
/// 堆垛机面板：每台一行，显示作业状态、工作模式、当前位置与两个货叉，
/// 并提供打手动 / 置火警 / 复位按钮。
/// </summary>
internal sealed class SrmStateView : UserControl
{
    private const int ColumnDevice = 0;
    private const int ColumnReport = 1;
    private const int ColumnMode = 2;
    private const int ColumnPosition = 3;
    private const int ColumnFork1 = 4;
    private const int ColumnFork2 = 5;
    private const int ColumnEvent = 6;
    private const int ColumnManual = 7;
    private const int ColumnFire = 8;
    private const int ColumnReset = 9;

    private static readonly Color ManualRowColor = Color.FromArgb(255, 243, 224);

    private readonly BufferedDataGridView _grid = new();
    private SimulationEngine? _engine;
    private List<SrmMachine> _machines = [];

    public SrmStateView()
    {
        Dock = DockStyle.Fill;
        BuildGrid();
    }

    public void Bind(SimulatorHost host)
    {
        _engine = host.Engine;
        _machines = [.. host.Engine.DeviceMachines.OfType<SrmMachine>()];

        _grid.Rows.Clear();

        foreach (SrmMachine machine in _machines)
        {
            _grid.Rows.Add(
                machine.DeviceId,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                ManualButtonText(machine.Manual),
                FireButtonText(machine.FireAlarm),
                "复位");
        }
    }

    /// <summary>按当前状态刷新各行。名字避开 <see cref="Control.Update"/>——那是强制重绘，语义完全不同。</summary>
    public void UpdateMachines()
    {
        for (int i = 0; i < _machines.Count && i < _grid.Rows.Count; i++)
        {
            SrmMachine machine = _machines[i];
            SrmSnapshot snapshot = machine.Snapshot();
            DataGridViewRow row = _grid.Rows[i];

            SetCell(row, ColumnReport, ReportText(snapshot.FunctionReport));
            SetCell(row, ColumnMode, ModeText(snapshot.FunctionMode));
            SetCell(row, ColumnPosition, snapshot.Position.ToChineseText());
            SetCell(row, ColumnFork1, ForkText(snapshot.Forks, 1));
            SetCell(row, ColumnFork2, ForkText(snapshot.Forks, 2));
            SetCell(row, ColumnEvent, snapshot.LastEvent);
            SetCell(row, ColumnManual, ManualButtonText(machine.Manual));
            SetCell(row, ColumnFire, FireButtonText(machine.FireAlarm));

            row.Cells[ColumnReport].Style.BackColor = ReportColor(snapshot.FunctionReport);
            row.Cells[ColumnReport].Style.ForeColor = Color.White;

            // 打手动的整行铺底色，与站台面板的观感一致。
            Color rowColor = machine.Manual ? ManualRowColor : Color.White;
            if (row.DefaultCellStyle.BackColor != rowColor)
            {
                row.DefaultCellStyle.BackColor = rowColor;
            }
        }
    }

    private static string ManualButtonText(bool manual) => manual ? "恢复自动" : "打手动";

    private static string FireButtonText(bool fire) => fire ? "清火警" : "置火警";

    private static string ReportText(byte report) => report switch
    {
        SrmFunctionReport.Idle => $"{report} 空闲",
        SrmFunctionReport.GetPosRunning => $"{report} 取货定位中",
        SrmFunctionReport.GetDone => $"{report} 取货完成",
        SrmFunctionReport.PutPosRunning => $"{report} 放货定位中",
        SrmFunctionReport.PutDone => $"{report} 放货完成",
        SrmFunctionReport.StockRunning => $"{report} 盘点中",
        SrmFunctionReport.StockDone => $"{report} 盘点完成",
        SrmFunctionReport.PosGetRunning => $"{report} 移动中",
        SrmFunctionReport.PosGetDone => $"{report} 移动完成",
        _ => report.ToString(),
    };

    private static string ModeText(byte mode)
    {
        if ((mode & SrmFunctionMode.Fault) != 0)
        {
            return "故障";
        }

        if ((mode & SrmFunctionMode.Manual) != 0)
        {
            return "手动";
        }

        return (mode & SrmFunctionMode.Ready) != 0 ? "自动·就绪" : "自动";
    }

    private static string ForkText(IReadOnlyList<SrmForkSnapshot> forks, byte forkNo)
    {
        foreach (SrmForkSnapshot fork in forks)
        {
            if (fork.ForkNo == forkNo)
            {
                string task = fork.TaskNum == 0 ? "-" : fork.TaskNum.ToString();
                return $"{fork.StateText} / {task}";
            }
        }

        return "—";   // 单货叉配置下没有第 2 个货叉
    }

    private static Color ReportColor(byte report) => report switch
    {
        SrmFunctionReport.Idle => Color.FromArgb(76, 175, 80),
        SrmFunctionReport.GetDone or SrmFunctionReport.PutDone
            or SrmFunctionReport.StockDone or SrmFunctionReport.PosGetDone => Color.FromArgb(0, 188, 212),
        _ => Color.FromArgb(255, 152, 0),
    };

    private static void SetCell(DataGridViewRow row, int columnIndex, string value)
    {
        if (!string.Equals(row.Cells[columnIndex].Value as string, value, StringComparison.Ordinal))
        {
            row.Cells[columnIndex].Value = value;
        }
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersWidth = 40;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "设备", FillWeight = 70, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "作业状态", FillWeight = 110, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "模式", FillWeight = 80, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前位置", FillWeight = 200, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "货叉1（状态/任务号）", FillWeight = 160, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "货叉2（状态/任务号）", FillWeight = 160, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "最近事件", FillWeight = 280, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "手动", FillWeight = 80, UseColumnTextForButtonValue = false });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "火警", FillWeight = 80, UseColumnTextForButtonValue = false });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "复位", FillWeight = 60, UseColumnTextForButtonValue = false });

        _grid.CellContentClick += OnCellContentClick;
        _grid.DataError += (_, e) => e.ThrowException = false;

        Controls.Add(_grid);
    }

    private void OnCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _machines.Count)
        {
            return;
        }

        SrmMachine machine = _machines[e.RowIndex];

        switch (e.ColumnIndex)
        {
            case ColumnManual:
                Post(() => machine.Manual = !machine.Manual);
                break;

            case ColumnFire:
                Post(() => machine.FireAlarm = !machine.FireAlarm);
                break;

            case ColumnReset:
                Post(machine.Reset);
                break;

            default:
                break;
        }
    }

    /// <summary>交回引擎线程执行，避免与同一时刻的 tick 抢状态机字段。</summary>
    private void Post(Action action) => _engine?.Enqueue(action);
}
