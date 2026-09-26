using System.Text;
using System.Text.Json;
using PlcSimulator.App.Shared;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Import;

namespace PlcSimulator.App.Views;

/// <summary>
/// 「配置生成」页：把 <c>wcs_opcitem</c> 的导出（.xlsx 或 CSV）直接生成模拟器配置。
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

    public ConfigImportView()
    {
        Dock = DockStyle.Fill;

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

        foreach (Control control in new Control[]
        {
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
        LayoutForm();
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

            PlacePathRow(_csvLabel, _csvPath, _browseCsv, y, inputX, textWidth, browseX);
            y += RowHeight;

            PlacePathRow(_outputLabel, _outputPath, _browseOutput, y, inputX, textWidth, browseX);
            y += RowHeight + 6;

            // IP 覆盖 / 端口 / 对接协议
            int x = Pad;
            MoveLabel(_ipLabel, ref x, y);
            _ipOverride.SetBounds(x, y, 150, 24);
            x += 150 + 18;
            MoveLabel(_portLabel, ref x, y);
            _port.SetBounds(x, y, 70, 24);
            x += 70 + 18;
            MoveLabel(_protocolTypeLabel, ref x, y);
            _protocolType.SetBounds(x, y, 90, 24);
            y += RowHeight;

            // 动作延时 / 抖动 / value 单位
            x = Pad;
            MoveLabel(_delayLabel, ref x, y);
            _delay.SetBounds(x, y, 80, 24);
            x += 80 + 18;
            MoveLabel(_jitterLabel, ref x, y);
            _jitter.SetBounds(x, y, 70, 24);
            x += 70 + 18;
            MoveLabel(_unitLabel, ref x, y);
            _valueUnit.SetBounds(x, y, 210, 24);
            y += RowHeight + 4;

            _generate.SetBounds(Pad + LabelWidth, y, 110, 26);
            y += 34;

            _hint.Location = new Point(Pad + LabelWidth, y);
            y += 26;

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
            "选择 wcs_opcitem 的导出（.xlsx 或 .csv）",
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
                _outputPath.Text = Path.Combine("config", "simulator.generated.json");
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
            Log("请先选择 wcs_opcitem 的导出文件（.xlsx 或 .csv）。");
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
            outputPath = Path.Combine("config", "simulator.generated.json");
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
        };

        _generate.Enabled = false;
        Log($"读取 {Path.GetFullPath(csvPath)} …");

        try
        {
            ImportTable table = ImportTable.Load(csvPath);
            Log($"已读取 {table.Rows.Count} 行。");

            SimulatorConfig config = ConfigBuilder.Build(table, options, out List<string> notes);

            string json = JsonSerializer.Serialize(config, ConfigLoader.SerializerOptions);

            // 不带 BOM：ConfigLoader 能读，但别的 JSON 工具对 BOM 支持不一。
            File.WriteAllText(outputPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (string note in notes)
            {
                Log("  " + note);
            }

            Log("");
            Log($"已生成 {Path.GetFullPath(outputPath)}");
            Log($"value/signaltype 单位：{(options.ValueIsRegister ? "寄存器（字节偏移 = value × 2）" : "字节")}");

            foreach (DeviceConfig device in config.Devices)
            {
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
}
