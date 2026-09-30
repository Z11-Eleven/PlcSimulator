using System.Text;
using System.Text.Json;
using PlcSimulator.App.Shared;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Import;

namespace PlcSimulator.App.Views;

/// <summary>
/// 「配置生成」页：按设备类型将输送机点位表或堆垛机设备表导出生成模拟器配置。
/// <para>
/// 与命令行工具 <c>PlcSimulator.Import</c> 共用同一套解析与生成代码
/// （<see cref="ImportTable"/> + <see cref="ConfigBuilder"/>），只是把参数摆到界面上、
/// 把提示信息回显到页内的日志区，生成完还能一键切过去。
/// </para>
/// </summary>
internal sealed class ConfigImportView : UserControl
{
    private const int Pad = 12;
    private const int LabelWidth = 96;
    private const int BrowseWidth = 84;
    private const int Gap = 8;
    private const int RowHeight = 32;

    private readonly Panel _form = new() { Dock = DockStyle.Top };
    private readonly TextBox _log = new();
    private readonly Label _kindLabel = new() { Text = "设备类型", AutoSize = true };
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _transportLabel = new() { Text = "通讯方式", AutoSize = true };
    private readonly ComboBox _transport = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _loopback = new() { Text = "每台使用独立回环 IP", AutoSize = true, Checked = true };
    private readonly Label _loopbackStartLabel = new() { Text = "起始回环 IP", AutoSize = true };
    private readonly TextBox _loopbackStart = new() { Text = "127.0.0.1" };
    private readonly Label _travelLabel = new() { Text = "行走延时 (ms)", AutoSize = true };
    private readonly NumericUpDown _travel = new() { Minimum = 0, Maximum = 600_000, Value = 1000 };
    private readonly Label _commandDbLabel = new() { Text = "指令 DB", AutoSize = true };
    private readonly NumericUpDown _commandDb = new() { Minimum = 1, Maximum = 65535, Value = 60 };
    private readonly Label _statusDbLabel = new() { Text = "状态 DB", AutoSize = true };
    private readonly NumericUpDown _statusDb = new() { Minimum = 1, Maximum = 65535, Value = 61 };
    private readonly Label _alarmDbLabel = new() { Text = "报警 DB", AutoSize = true };
    private readonly NumericUpDown _alarmDb = new() { Minimum = 1, Maximum = 65535, Value = 70 };
    private readonly Label _payloadLabel = new() { Text = "指令布局", AutoSize = true };
    private readonly ComboBox _payload = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _statusLengthLabel = new() { Text = "状态长度", AutoSize = true };
    private readonly NumericUpDown _statusLength = new() { Minimum = 23, Maximum = 171, Value = 74 };
    private readonly Label _alarmLengthLabel = new() { Text = "报警长度", AutoSize = true };
    private readonly NumericUpDown _alarmLength = new() { Minimum = 1, Maximum = 100, Value = 100 };

    private bool IsSrm => _kind.SelectedIndex == 1;
    private string SourceTable => IsSrm ? "wcs_equipmentinfo" : "wcs_opcitem";

    private readonly Label _csvLabel = new() { Text = "导出文件", AutoSize = true };
    private readonly TextBox _csvPath = new();
    private readonly Button _browseCsv = new() { Text = "浏览…" };

    private readonly Label _outputLabel = new() { Text = "输出配置", AutoSize = true };
    private readonly TextBox _outputPath = new();
    private readonly Button _browseOutput = new() { Text = "浏览…" };

    private readonly Label _ipLabel = new() { Text = "设备 IP 覆盖", AutoSize = true };
    private readonly TextBox _ipOverride = new();
    private readonly Label _portLabel = new() { Text = "端口", AutoSize = true };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 502 };
    private readonly Label _protocolTypeLabel = new() { Text = "对接协议", AutoSize = true };
    private readonly ComboBox _protocolType = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    private readonly Label _delayLabel = new() { Text = "动作延时 (ms)", AutoSize = true };
    private readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 600_000, Value = 3000 };
    private readonly Label _jitterLabel = new() { Text = "抖动 (ms)", AutoSize = true };
    private readonly NumericUpDown _jitter = new() { Minimum = 0, Maximum = 60_000, Value = 200 };
    private readonly Label _unitLabel = new() { Text = "value 单位", AutoSize = true };
    private readonly ComboBox _valueUnit = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    private readonly Label _hint = new()
    {
        Text = "「设备 IP 覆盖」留空表示用数据库 userid 字段里的地址",
        AutoSize = true,
        ForeColor = Color.FromArgb(130, 130, 130),
    };

    private readonly Button _generate = new() { Text = "生成配置" };

    private bool _layingOut;
    private bool _previousSrm;
    private string _previousTransport = "Socket";

    public ConfigImportView()
    {
        Dock = DockStyle.Fill;
        _kind.Items.AddRange(["输送机", "堆垛机"]);
        _kind.SelectedIndex = 0;
        _transport.Items.AddRange(["Socket", "S7"]);
        _transport.SelectedIndex = 0;
        _payload.Items.AddRange(["26 字节（负载偏移 2）", "23 字节（负载偏移 0）"]);
        _payload.SelectedIndex = 0;

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.BackColor = Color.White;
        _log.Dock = DockStyle.Fill;

        _protocolType.Items.AddRange(["NTI", "CATL"]);
        _protocolType.SelectedIndex = 0;

        _valueUnit.Items.AddRange(["寄存器（默认）", "字节"]);
        _valueUnit.SelectedIndex = 0;

        _browseCsv.Click += OnBrowseCsvClick;
        _browseOutput.Click += OnBrowseOutputClick;
        _generate.Click += OnGenerateClick;
        _kind.SelectedIndexChanged += (_, _) => UpdateDeviceKind();
        _transport.SelectedIndexChanged += (_, _) => UpdateDeviceKind();
        _loopback.CheckedChanged += (_, _) => UpdateDeviceKind();

        foreach (Control control in new Control[]
        {
            _kindLabel, _kind, _transportLabel, _transport, _loopback, _loopbackStartLabel, _loopbackStart, _travelLabel, _travel,
            _commandDbLabel, _commandDb, _statusDbLabel, _statusDb, _alarmDbLabel, _alarmDb,
            _payloadLabel, _payload, _statusLengthLabel, _statusLength, _alarmLengthLabel, _alarmLength,
            _csvLabel, _csvPath, _browseCsv,
            _outputLabel, _outputPath, _browseOutput,
            _ipLabel, _ipOverride, _portLabel, _port, _protocolTypeLabel, _protocolType,
            _delayLabel, _delay, _jitterLabel, _jitter, _unitLabel, _valueUnit,
            _hint, _generate,
        })
        {
            _form.Controls.Add(control);
        }

        // Dock 布局按 z 序从后往前处理：先加 Fill 的日志区，再加 Dock=Top 的表单区。
        Controls.Add(_log);
        Controls.Add(_form);

        _form.Resize += (_, _) => LayoutForm();
        UpdateDeviceKind();
    }

    /// <summary>
    /// 除了表单区自己的 Resize，还跟着整个控件的布局走一遍：
    /// TabPage 里的控件在切换标签、窗口尺寸变化时不一定各自触发 Resize，
    /// 多挂一个布局事件能保证输入框与「浏览…」按钮始终贴着当前宽度排。
    /// </summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutForm();
    }

    /// <summary>生成成功后触发，参数是生成的配置文件路径。</summary>
    public event EventHandler<string>? ConfigGenerated;

    /// <summary>启动时把上次用过的路径填进来（之后由用户自己改，不被重新加载冲掉）。</summary>
    public void PrefillPaths(string csvPath, string outputPath)
    {
        _csvPath.Text = csvPath;
        _outputPath.Text = outputPath;
    }

    /// <summary>读回当前填的路径，供退出/生成后记录到设置里。</summary>
    public (string Csv, string Output) ReadPaths() => (_csvPath.Text, _outputPath.Text);

    private void UpdateDeviceKind()
    {
        bool s7 = IsSrm && _transport.SelectedItem?.ToString() == "S7";
        foreach (Control control in new Control[] { _transportLabel, _transport, _loopback, _travelLabel, _travel })
        {
            control.Visible = IsSrm;
        }
        foreach (Control control in new Control[] { _commandDbLabel, _commandDb, _statusDbLabel, _statusDb,
            _alarmDbLabel, _alarmDb, _payloadLabel, _payload, _statusLengthLabel, _statusLength, _alarmLengthLabel, _alarmLength })
        {
            control.Visible = s7;
        }
        _protocolTypeLabel.Visible = _protocolType.Visible = !IsSrm;
        _unitLabel.Visible = _valueUnit.Visible = !IsSrm;
        _portLabel.Visible = _port.Visible = !IsSrm || s7;
        _ipOverride.Enabled = !IsSrm || !_loopback.Checked;
        _ipLabel.Visible = _ipOverride.Visible = !IsSrm || !_loopback.Checked;
        _loopbackStartLabel.Visible = _loopbackStart.Visible = IsSrm && _loopback.Checked;
        string transport = _transport.SelectedItem?.ToString() ?? "Socket";
        if (IsSrm != _previousSrm || transport != _previousTransport)
        {
            _port.Value = IsSrm ? 102 : 502;
        }
        _previousSrm = IsSrm;
        _previousTransport = transport;
        _hint.Text = IsSrm
            ? "导入 wcs_equipmentinfo；S7 原数据沿用指令/状态 DB 和长度，报警 DB 使用填写值。"
            : "导入 wcs_opcitem；设备 IP 覆盖留空时使用 userid。";
        LayoutForm();
    }

    /// <summary>
    /// 按当前宽度摆放表单区的控件：路径输入框跟着窗口拉伸，其余按自然宽度依次排。
    /// 不用 Anchor 是因为构造期控件宽度还没定，锚定出来的位置不可靠。
    /// </summary>
    private void LayoutForm()
    {
        if (_layingOut)
        {
            return;
        }

        int width = _form.ClientSize.Width;
        if (width <= 0)
        {
            return;
        }

        _layingOut = true;

        try
        {
            int inputX = Pad + LabelWidth;
            int textWidth = Math.Max(160, width - inputX - BrowseWidth - (Gap * 2));
            int browseX = inputX + textWidth + Gap;
            int y = Pad;
            int kindX = Pad;
            MoveLabel(_kindLabel, ref kindX, y);
            _kind.SetBounds(kindX, y, 120, 24);
            kindX += 140;
            MoveLabel(_transportLabel, ref kindX, y);
            _transport.SetBounds(kindX, y, 100, 24);
            _loopback.Location = new Point(kindX + 120, y + 3);
            y += RowHeight;

            PlacePathRow(_csvLabel, _csvPath, _browseCsv, y, inputX, textWidth, browseX);
            y += RowHeight;

            PlacePathRow(_outputLabel, _outputPath, _browseOutput, y, inputX, textWidth, browseX);
            y += RowHeight + 6;

            // IP 覆盖 / 端口 / 对接协议
            int x = Pad;
            bool useLoopback = IsSrm && _loopback.Checked;
            MoveLabel(useLoopback ? _loopbackStartLabel : _ipLabel, ref x, y);
            (useLoopback ? _loopbackStart : _ipOverride).SetBounds(x, y, 150, 24);
            x += 150 + 18;
            MoveLabel(_portLabel, ref x, y);
            _port.SetBounds(x, y, 70, 24);
            x += 70 + 18;
            if (!IsSrm)
            {
                MoveLabel(_protocolTypeLabel, ref x, y);
                _protocolType.SetBounds(x, y, 90, 24);
            }
            y += RowHeight;

            // 动作延时 / 抖动 / value 单位
            x = Pad;
            MoveLabel(_delayLabel, ref x, y);
            _delay.SetBounds(x, y, 80, 24);
            x += 80 + 18;
            MoveLabel(_jitterLabel, ref x, y);
            _jitter.SetBounds(x, y, 70, 24);
            x += 70 + 18;
            if (IsSrm)
            {
                MoveLabel(_travelLabel, ref x, y);
                _travel.SetBounds(x, y, 80, 24);
            }
            else
            {
                MoveLabel(_unitLabel, ref x, y);
                _valueUnit.SetBounds(x, y, 210, 24);
            }
            y += RowHeight + 4;

            if (IsSrm && _transport.SelectedItem?.ToString() == "S7")
            {
                x = Pad;
                MoveLabel(_commandDbLabel, ref x, y);
                _commandDb.SetBounds(x, y, 70, 24); x += 88;
                MoveLabel(_statusDbLabel, ref x, y);
                _statusDb.SetBounds(x, y, 70, 24); x += 88;
                MoveLabel(_alarmDbLabel, ref x, y);
                _alarmDb.SetBounds(x, y, 70, 24);
                y += RowHeight;
                x = Pad;
                MoveLabel(_payloadLabel, ref x, y);
                _payload.SetBounds(x, y, 200, 24); x += 218;
                MoveLabel(_statusLengthLabel, ref x, y);
                _statusLength.SetBounds(x, y, 70, 24); x += 88;
                MoveLabel(_alarmLengthLabel, ref x, y);
                _alarmLength.SetBounds(x, y, 70, 24);
                y += RowHeight;
            }

            _generate.SetBounds(Pad + LabelWidth, y, 110, 26);
            y += 34;

            _hint.Location = new Point(Pad + LabelWidth, y);
            _hint.MaximumSize = new Size(Math.Max(1, width - Pad - LabelWidth - Pad), 0);
            y += Math.Max(26, _hint.PreferredHeight + 6);

            _form.Height = y;
        }
        finally
        {
            _layingOut = false;
        }
    }

    private static void PlacePathRow(
        Label label, Control input, Control button, int y, int inputX, int textWidth, int browseX)
    {
        label.Location = new Point(Pad, y + 4);
        input.SetBounds(inputX, y, textWidth, 24);
        button.SetBounds(browseX, y, BrowseWidth, 24);
    }

    /// <summary>把标签摆到 <paramref name="x"/>，并把游标推进到下一个控件的位置。</summary>
    private static void MoveLabel(Label label, ref int x, int y)
    {
        label.Location = new Point(x, y + 4);
        x += label.PreferredWidth + 8;
    }

    private void OnBrowseCsvClick(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = FileDialogs.Open(
            $"选择 {SourceTable} 的导出（.xlsx 或 .csv）",
            "点位表 (*.xlsx;*.csv)|*.xlsx;*.csv|Excel 工作簿 (*.xlsx)|*.xlsx|CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*");

        if (!string.IsNullOrWhiteSpace(_csvPath.Text) && File.Exists(_csvPath.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(_csvPath.Text));
            dialog.FileName = Path.GetFileName(_csvPath.Text);
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _csvPath.Text = dialog.FileName;

            if (string.IsNullOrWhiteSpace(_outputPath.Text))
            {
                _outputPath.Text = DefaultOutputPath();
            }
        }
    }

    private void OnBrowseOutputClick(object? sender, EventArgs e)
    {
        using SaveFileDialog dialog = FileDialogs.Save(
            "选择生成的配置文件保存位置",
            "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*");

        dialog.DefaultExt = "json";
        dialog.FileName = string.IsNullOrWhiteSpace(_outputPath.Text)
            ? "simulator.generated.json"
            : Path.GetFileName(_outputPath.Text);

        if (!string.IsNullOrWhiteSpace(_outputPath.Text))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_outputPath.Text));
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputPath.Text = dialog.FileName;
        }
    }

    private void OnGenerateClick(object? sender, EventArgs e)
    {
        string csvPath = _csvPath.Text.Trim();

        if (csvPath.Length == 0)
        {
            Log($"请先选择 {SourceTable} 的导出文件（.xlsx 或 .csv）。");
            return;
        }

        if (!File.Exists(csvPath))
        {
            Log($"找不到文件：{Path.GetFullPath(csvPath)}");
            return;
        }

        string outputPath = _outputPath.Text.Trim();
        if (outputPath.Length == 0)
        {
            outputPath = DefaultOutputPath();
            _outputPath.Text = outputPath;
        }

        if (string.Equals(Path.GetFullPath(csvPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            Log("输出路径不能和输入文件相同，会覆盖掉原始导出数据。");
            return;
        }

        var options = new ImportOptions
        {
            InputPath = csvPath,
            OutputPath = outputPath,
            IpOverride = string.IsNullOrWhiteSpace(_ipOverride.Text) ? null : _ipOverride.Text.Trim(),
            Port = (int)_port.Value,
            ProtocolType = _protocolType.SelectedItem?.ToString() ?? "NTI",
            ActionDelayMs = (int)_delay.Value,
            JitterMs = (int)_jitter.Value,
            ValueIsRegister = _valueUnit.SelectedIndex == 0,
            DeviceKind = IsSrm ? ImportDeviceKind.Srm : ImportDeviceKind.Conveyor,
            SrmTransport = _transport.SelectedItem?.ToString() ?? "Socket",
            UseLoopbackIps = IsSrm && _loopback.Checked,
            LoopbackStartIp = _loopbackStart.Text.Trim(),
            TravelDelayMs = (int)_travel.Value,
            S7 = new()
            {
                Port = (int)_port.Value, CommandPayloadOffset = _payload.SelectedIndex == 0 ? 2 : 0,
                Command = new() { DbNumber = (int)_commandDb.Value },
                Status = new() { DbNumber = (int)_statusDb.Value },
                Alarm = new() { DbNumber = (int)_alarmDb.Value },
                StatusLength = (int)_statusLength.Value, AlarmLength = (int)_alarmLength.Value,
            },
        };
        if (options.UseLoopbackIps)
        {
            options.IpOverride = null;
        }

        _generate.Enabled = false;
        Log($"读取 {Path.GetFullPath(csvPath)} …");

        try
        {
            ImportTable table = ImportTable.Load(csvPath);
            Log($"已读取 {table.Rows.Count} 行。");

            SimulatorConfig config = ConfigBuilder.Build(table, options, out List<string> notes);
            ConfigLoadResult validation = ConfigLoader.Validate(config);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(string.Join("；", validation.Errors));
            }

            string json = JsonSerializer.Serialize(config, ConfigLoader.SerializerOptions);

            // 不带 BOM：ConfigLoader 能读，但别的 JSON 工具对 BOM 支持不一。
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (string note in notes)
            {
                Log("  " + note);
            }

            Log("");
            Log($"已生成 {Path.GetFullPath(outputPath)}");
            if (!IsSrm)
            {
                Log($"value/signaltype 单位：{(options.ValueIsRegister ? "寄存器（字节偏移 = value × 2）" : "字节")}");
            }

            foreach (DeviceConfig device in config.Devices)
            {
                if (device.Srm is not null)
                {
                    Log($"  {device.Id}  {device.Ip}  {device.Protocol}  货叉 {device.Srm.ForkCount} 个  动作点 {device.Srm.StationPoints.Count} 个");
                    continue;
                }
                int maxEnd = device.Stations.Count == 0
                    ? 0
                    : device.Stations.Max(static s => s.ByteOffset + s.LengthBytes);

                Log(
                    $"  {device.Id}  IP {device.Ip}  站台 {device.Stations.Count} 个  "
                    + $"块 [0, {device.Blocks[0].LengthBytes}) 字节（站台最大到 {maxEnd}）");
            }

            Log("");
            ConfigGenerated?.Invoke(this, outputPath);
        }
        catch (Exception ex)
        {
            Log($"生成失败：{ex.Message}");
        }
        finally
        {
            _generate.Enabled = true;
        }
    }

    private void Log(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private string DefaultOutputPath() => Path.Combine("config", IsSrm
        ? $"simulator.srm.{_transport.SelectedItem?.ToString()?.ToLowerInvariant()}.generated.json"
        : "simulator.generated.json");
}
