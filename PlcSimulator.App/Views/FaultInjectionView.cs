using PlcSimulator.App.Controls;
using PlcSimulator.Core.Faults;

namespace PlcSimulator.App.Views;

/// <summary>
/// 故障注入面板：列出配置里的规则，可以运行时逐条开关。
/// 规则本身的增删改仍需编辑配置文件后重新加载。
/// </summary>
internal sealed class FaultInjectionView : UserControl
{
    private const int ColumnEnabled = 0;
    private const int ColumnName = 1;
    private const int ColumnEffect = 2;
    private const int ColumnMatch = 3;
    private const int ColumnHits = 4;

    private readonly BufferedDataGridView _grid = new();
    private readonly Label _summary = new();
    private IFaultInjector _injector = PassThroughFaultInjector.Instance;
    private bool _loading;

    public FaultInjectionView()
    {
        Dock = DockStyle.Fill;
        BuildLayout();
    }

    public void Bind(IFaultInjector injector)
    {
        _injector = injector;
        Reload();
    }

    /// <summary>只更新命中次数，避免每帧重建整张表。</summary>
    public void RefreshHits()
    {
        if (_injector.Rules.Count != _grid.Rows.Count)
        {
            Reload();
            return;
        }

        _loading = true;

        try
        {
            for (int i = 0; i < _injector.Rules.Count; i++)
            {
                int hits = _injector.Rules[i].HitCount;
                if (_grid.Rows[i].Cells[ColumnHits].Value is not int current || current != hits)
                {
                    _grid.Rows[i].Cells[ColumnHits].Value = hits;
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void Reload()
    {
        _loading = true;

        try
        {
            _grid.Rows.Clear();

            foreach (FaultRuleStatus rule in _injector.Rules)
            {
                _grid.Rows.Add(rule.RuntimeEnabled, rule.Name, rule.Effect, rule.MatchSummary, rule.HitCount);
            }
        }
        finally
        {
            _loading = false;
        }

        if (_injector.Enabled)
        {
            _summary.Text = $"故障注入已启用，共 {_injector.Rules.Count} 条规则。被故障命中的报文会在「报文日志」里标注规则名。";
            _summary.ForeColor = Color.Firebrick;
        }
        else
        {
            _summary.Text = "故障注入未启用（配置里 faultInjection.enabled 为 false，或没有任何启用的规则）。";
            _summary.ForeColor = Color.DimGray;
        }
    }

    private void BuildLayout()
    {
        _summary.Dock = DockStyle.Top;
        _summary.Height = 34;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Padding = new Padding(8, 0, 8, 0);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersWidth = 40;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "启用", FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "规则", FillWeight = 140, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "效果", FillWeight = 120, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "匹配条件", FillWeight = 300, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "命中次数", FillWeight = 70, ReadOnly = true });

        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // 让复选框的改动立刻提交，否则要切走单元格才生效。
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.DataError += (_, e) => e.ThrowException = false;

        Controls.Add(_grid);
        Controls.Add(_summary);
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.ColumnIndex != ColumnEnabled || e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count)
        {
            return;
        }

        bool enabled = _grid.Rows[e.RowIndex].Cells[ColumnEnabled].Value is true;

        // 按行号对应规则：规则名可以重复或为空，按名字切只会命中第一条。
        if (!_injector.SetRuleEnabledAt(e.RowIndex, enabled))
        {
            _summary.Text = $"未能切换第 {e.RowIndex + 1} 条规则。";
            _summary.ForeColor = Color.Firebrick;
        }
    }
}
