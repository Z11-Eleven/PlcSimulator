using PlcSimulator.App.Shared;
using PlcSimulator.Devices;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Hosting;

namespace PlcSimulator.App.Conveyor;

/// <summary>
/// 流程状态机面板：每个站台一行，显示它当前卡在哪一步，并提供手动注入按钮。
/// 站台数量级不大，用普通模式按需更新单元格即可。
/// </summary>
public sealed class StationStateView : UserControl
{
    private const int ColumnDevice = 0;
    private const int ColumnStation = 1;
    private const int ColumnState = 2;
    private const int ColumnStep = 3;
    private const int ColumnDwell = 4;
    private const int ColumnTaskNum = 5;
    private const int ColumnBarcode = 6;
    private const int ColumnInject = 7;
    private const int ColumnManual = 8;
    private const int ColumnReset = 9;

    private static readonly Color ManualRowColor = Color.FromArgb(255, 243, 224);

    private readonly BufferedDataGridView _grid = new();
    private readonly DeviceSearchBar _search = new();
    private SimulationEngine? _engine;
    private List<ConveyorStationMachine> _machines = [];

    public StationStateView()
    {
        Dock = DockStyle.Fill;
        BuildGrid();
        _search.QueryChanged += (_, _) => ApplyFilter();
        Controls.Add(_search);
    }

    public void Bind(SimulatorHost host)
    {
        _engine = host.Engine;
        _machines = [.. host.Engine.Machines];

        _grid.Rows.Clear();

        foreach (ConveyorStationMachine machine in _machines)
        {
            _grid.Rows.Add(
                machine.Station.Device.Config.Id,
                machine.Station.StationNo,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                machine.State == StationState.Idle ? "有货" : "清空",
                ManualButtonText(machine.IsManual),
                "复位");
        }
        Update(host.Engine.Snapshots());
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        // 保留原始行索引，过滤后按钮和实时快照仍对应同一台状态机。
        _grid.CurrentCell = null;
        int visible = 0;
        for (int i = 0; i < _machines.Count; i++)
        {
            var station = _machines[i].Station;
            bool match = station.Device.Stations.Any(candidate =>
                candidate.PrimaryStationNo == station.PrimaryStationNo && _search.Matches(candidate));
            _grid.Rows[i].Visible = match;
            if (match)
            {
                visible++;
            }
        }

        DataGridViewRow? first = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(static row => row.Visible);
        if (first is not null)
        {
            _grid.FirstDisplayedScrollingRowIndex = first.Index;
            _grid.CurrentCell = first.Cells[ColumnStation];
        }

        _search.ShowResult(visible, _machines.Count, "个站台");
    }

    public void Update(IReadOnlyList<StationSnapshot> snapshots)
    {
        int count = Math.Min(snapshots.Count, _grid.Rows.Count);

        for (int i = 0; i < count; i++)
        {
            StationSnapshot snapshot = snapshots[i];
            DataGridViewRow row = _grid.Rows[i];

            SetCell(row, ColumnState, snapshot.StateText);
            SetCell(row, ColumnStep, snapshot.LastEvent);
            SetCell(row, ColumnDwell, $"{snapshot.TimeInState.TotalSeconds:F1} s");
            SetCell(row, ColumnTaskNum, snapshot.TaskNum == 0 ? string.Empty : snapshot.TaskNum.ToString());
            SetCell(row, ColumnBarcode, snapshot.Barcode);
            SetCell(row, ColumnInject, snapshot.State == StationState.Idle ? "有货" : "清空");
            SetCell(row, ColumnManual, ManualButtonText(snapshot.Manual));

            row.Cells[ColumnState].Style.BackColor = StateColor(snapshot.State);
            row.Cells[ColumnState].Style.ForeColor = Color.White;

            // 手动站台整行铺底色，堵塞时一眼能看出卡在哪个站台。
            Color rowColor = snapshot.Manual ? ManualRowColor : Color.White;
            if (row.DefaultCellStyle.BackColor != rowColor)
            {
                row.DefaultCellStyle.BackColor = rowColor;
            }
        }
    }

    private static string ManualButtonText(bool manual) => manual ? "恢复自动" : "打手动";

    private static Color StateColor(StationState state) => state switch
    {
        StationState.Idle => Color.FromArgb(76, 175, 80),
        StationState.Loaded => Color.FromArgb(33, 150, 243),
        StationState.Executing => Color.FromArgb(255, 152, 0),
        StationState.WaitingDownstream => Color.FromArgb(156, 39, 176),
        StationState.Done => Color.FromArgb(0, 188, 212),
        StationState.Fault => Color.FromArgb(244, 67, 54),
        _ => Color.FromArgb(158, 158, 158),
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

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "设备", FillWeight = 100, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "站台", FillWeight = 60, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 70, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前步骤", FillWeight = 260, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "停留", FillWeight = 60, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "任务号", FillWeight = 60, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "条码", FillWeight = 130, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "注入", FillWeight = 80, UseColumnTextForButtonValue = false });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "手动", FillWeight = 80, UseColumnTextForButtonValue = false });
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

        ConveyorStationMachine machine = _machines[e.RowIndex];

        switch (e.ColumnIndex)
        {
            case ColumnInject:
                Post(() => machine.SetLoaded(machine.State == StationState.Idle));
                break;

            case ColumnManual:
                Post(() => machine.SetManual(!machine.IsManual));
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
