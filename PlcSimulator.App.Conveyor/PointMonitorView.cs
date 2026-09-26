using PlcSimulator.App.Shared;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices;
using PlcSimulator.Hosting;

namespace PlcSimulator.App.Conveyor;

/// <summary>
/// 点位监视表：逐字段显示每个站台的当前值，可直接改值（相当于代替 WCS 下发）。
/// <para>
/// 行数随点位数增长（全量导入时可达上万行），因此使用虚拟模式：
/// 只对可见行取值，并且只刷新被写入过的行（依赖数据区的脏区追踪），而不是遍历全表。
/// </para>
/// </summary>
public sealed class PointMonitorView : UserControl
{
    private const int ColumnDevice = 0;
    private const int ColumnStation = 1;
    private const int ColumnField = 2;
    private const int ColumnOffset = 3;
    private const int ColumnRegister = 4;
    private const int ColumnKind = 5;
    private const int ColumnValue = 6;
    private const int ColumnIncoming = 7;
    private const int ColumnChanged = 8;

    private static readonly Dictionary<string, string> FieldDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tasknum"] = "任务号",
        ["goodstype"] = "货物类型",
        ["from"] = "起始站台",
        ["to"] = "目标站台",
        ["heartbeat"] = "心跳",
        ["barcode"] = "条码",
        ["status"] = "设备状态",
        ["ack"] = "回执",
        ["autoManual"] = "手自动",
        ["fault"] = "故障",
    };

    private readonly BufferedDataGridView _grid = new();
    private readonly List<PointRow> _rows = [];
    private readonly Dictionary<int, DateTime> _lastChanged = [];
    private readonly List<DeviceRange> _deviceRanges = [];
    private SimulatorHost? _host;

    public PointMonitorView()
    {
        Dock = DockStyle.Fill;
        BuildGrid();
    }

    public void Bind(SimulatorHost host)
    {
        _host = host;
        _rows.Clear();
        _lastChanged.Clear();
        _deviceRanges.Clear();

        foreach (DeviceRuntime device in host.Devices)
        {
            int rowStart = _rows.Count;

            foreach (StationRuntime station in device.Stations)
            {
                foreach (FieldDescriptor field in station.EnumerateFields())
                {
                    _rows.Add(new PointRow(device, station, field, IsReadField(device, field)));
                }
            }

            _deviceRanges.Add(new DeviceRange(device, rowStart, _rows.Count - rowStart));
        }

        _grid.RowCount = _rows.Count;
        _grid.Invalidate();
    }

    /// <summary>按数据区的脏区刷新受影响的行，其余行不动。</summary>
    public void RefreshDirty()
    {
        if (_host is null || _grid.IsCurrentCellInEditMode)
        {
            return;
        }

        foreach (DeviceRange range in _deviceRanges)
        {
            List<(int Start, int End)> dirty = range.Device.Space.TakeDirtyRanges();
            if (dirty.Count == 0)
            {
                continue;
            }

            DateTime now = DateTime.Now;
            int end = range.RowStart + range.RowCount;

            for (int index = range.RowStart; index < end; index++)
            {
                PointRow row = _rows[index];
                int fieldStart = row.Station.ByteOffset + row.Field.ByteOffset;
                int fieldEnd = fieldStart + Math.Max(1, row.Field.SizeInBytes);

                if (Overlaps(dirty, fieldStart, fieldEnd))
                {
                    _lastChanged[index] = now;
                    _grid.InvalidateRow(index);
                }
            }
        }
    }

    public void Reset()
    {
        _lastChanged.Clear();
        _grid.Invalidate();
    }

    private static bool Overlaps(List<(int Start, int End)> ranges, int start, int end)
    {
        foreach ((int rangeStart, int rangeEnd) in ranges)
        {
            if (rangeStart < end && rangeEnd > start)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReadField(DeviceRuntime device, FieldDescriptor field)
        => device.ProtocolTemplate.TryGetReadField(field.Name, out _);

    private static string DisplayName(FieldDescriptor field)
        => FieldDisplayNames.TryGetValue(field.Name, out string? name) ? $"{name}（{field.Name}）" : field.Name;

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.VirtualMode = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersWidth = 64;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "设备", FillWeight = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "站台", FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", FillWeight = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字节偏移", FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "寄存器", FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前值（WCS 读到）", FillWeight = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "写入侧释义（状态机读到）",
            FillWeight = 150,
            ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "最后变更", FillWeight = 100, ReadOnly = true });

        _grid.Columns[ColumnValue].ReadOnly = false;
        _grid.Columns[ColumnChanged].ReadOnly = true;

        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.CellEndEdit += OnCellEndEdit;
        _grid.CellFormatting += OnCellFormatting;
        _grid.DataError += (_, e) => e.ThrowException = false;

        Controls.Add(_grid);
    }

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count)
        {
            return;
        }

        PointRow row = _rows[e.RowIndex];

        e.Value = e.ColumnIndex switch
        {
            ColumnDevice => row.Device.Config.Id,
            ColumnStation => row.Station.IsAlias
                ? $"{row.Station.StationNo}（共用 {row.Station.PrimaryStationNo}）"
                : row.Station.StationNo,
            ColumnField => DisplayName(row.Field),
            ColumnOffset => row.Station.ByteOffset + row.Field.ByteOffset,
            ColumnRegister => (row.Station.ByteOffset + row.Field.ByteOffset) / 2,
            ColumnKind => row.Field.Kind.ToString(),
            ColumnValue => FormatValue(row),
            ColumnIncoming => FormatIncomingValue(row),
            ColumnChanged => _lastChanged.TryGetValue(e.RowIndex, out DateTime changed)
                ? changed.ToString("HH:mm:ss.fff")
                : string.Empty,
            _ => null,
        };
    }

    private void OnCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex != ColumnValue || e.RowIndex < 0 || e.RowIndex >= _rows.Count)
        {
            return;
        }

        PointRow row = _rows[e.RowIndex];
        string text = _grid.Rows[e.RowIndex].Cells[ColumnValue].Value?.ToString() ?? string.Empty;

        try
        {
            ApplyValue(row, text);
            _lastChanged[e.RowIndex] = DateTime.Now;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"无法写入该字段：{ex.Message}",
                "写入失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        _grid.InvalidateRow(e.RowIndex);
    }

    private static string FormatValue(PointRow row)
    {
        FieldDescriptor field = row.Field;

        try
        {
            if (row.IsReadField)
            {
                return field.Kind switch
                {
                    FieldKind.Ascii => row.Station.ReadOutgoingAscii(field.Name),
                    FieldKind.U8 => row.Station.ReadOutgoingU8(field.Name).ToString(),
                    FieldKind.U16 => row.Station.ReadOutgoingU16(field.Name).ToString(),
                    FieldKind.I16 => ((short)row.Station.ReadOutgoingU16(field.Name)).ToString(),
                    _ => "-",
                };
            }

            return field.Kind switch
            {
                FieldKind.Ascii => row.Station.ReadIncomingAscii(field.Name),
                FieldKind.U8 => row.Station.ReadIncomingU8(field.Name).ToString(),
                FieldKind.U16 => row.Station.ReadIncomingU16(field.Name).ToString(),
                _ => "-",
            };
        }
        catch (InvalidOperationException)
        {
            return "-";
        }
    }

    /// <summary>
    /// 状态机实际读到的值（写入侧口径）。与「当前值」（WCS 读到的值）不一致时会被高亮，
    /// 提示两个口径对不上——字节序错位时就表现为这种差异。
    /// </summary>
    private static string FormatIncomingValue(PointRow row)
    {
        if (!row.Device.ProtocolTemplate.TryGetWriteField(row.Field.Name, out FieldDescriptor field))
        {
            return string.Empty;
        }

        try
        {
            return field.Kind switch
            {
                FieldKind.Ascii => row.Station.ReadIncomingAscii(field.Name),
                FieldKind.U8 => row.Station.ReadIncomingU8(field.Name).ToString(),
                FieldKind.U16 => row.Station.ReadIncomingU16(field.Name).ToString(),
                FieldKind.I16 => ((short)row.Station.ReadIncomingU16(field.Name)).ToString(),
                _ => string.Empty,
            };
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static void ApplyValue(PointRow row, string text)
    {
        FieldDescriptor field = row.Field;

        if (!row.IsReadField)
        {
            throw new InvalidOperationException("该字段属于 WCS 写入侧，请通过站台状态面板手动注入。");
        }

        switch (field.Kind)
        {
            case FieldKind.Ascii:
                row.Station.WriteOutgoingAscii(field.Name, text);
                break;

            case FieldKind.U8:
                row.Station.WriteOutgoingU8(field.Name, byte.Parse(text));
                break;

            case FieldKind.U16:
                row.Station.WriteOutgoingU16(field.Name, ushort.Parse(text));
                break;

            case FieldKind.I16:
                row.Station.WriteOutgoingU16(field.Name, unchecked((ushort)short.Parse(text)));
                break;

            default:
                throw new InvalidOperationException($"暂不支持写入 {field.Kind} 类型。");
        }
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count)
        {
            return;
        }

        // 两个口径对不上时优先标出来：这是最需要人注意的情况。
        if (e.ColumnIndex is ColumnValue or ColumnIncoming)
        {
            PointRow row = _rows[e.RowIndex];
            string readSide = FormatValue(row);
            string writeSide = FormatIncomingValue(row);

            if (writeSide.Length > 0 && !string.Equals(readSide, writeSide, StringComparison.Ordinal))
            {
                e.CellStyle.BackColor = Color.FromArgb(255, 224, 178);
                e.CellStyle.ForeColor = Color.FromArgb(150, 40, 0);
                return;
            }
        }

        if (!_lastChanged.TryGetValue(e.RowIndex, out DateTime changed)
            || (DateTime.Now - changed).TotalMilliseconds > 800)
        {
            return;
        }

        e.CellStyle.BackColor = Color.FromArgb(255, 245, 200);
    }

    private sealed record DeviceRange(DeviceRuntime Device, int RowStart, int RowCount);

    private sealed record PointRow(
        DeviceRuntime Device,
        StationRuntime Station,
        FieldDescriptor Field,
        bool IsReadField);
}
