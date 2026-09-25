using PlcSimulator.App.Views;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Frames;
using PlcSimulator.Devices;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Hosting;

namespace PlcSimulator.App;

/// <summary>
/// 调试台主窗体。按工程约束不使用窗体设计器，所有控件在代码里创建。
/// <para>
/// 后台线程（协议层、状态机引擎）从不触碰控件：它们只写数据区与环形缓冲，
/// UI 通过 300ms 定时器主动拉取，并只刷新发生变化的部分。
/// </para>
/// </summary>
internal sealed class MainForm : Form
{
    private const int UiRefreshMs = 300;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly RingBufferFrameLog _frameLog = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _startStopButton = new("启动");
    private readonly ToolStripButton _openConfigButton = new("打开配置…");
    private readonly ToolStripButton _reloadButton = new("重新加载配置");
    private readonly ToolStripButton _resetButton = new("复位全部站台");
    private readonly ToolStripLabel _statusLabel = new("未启动");

    private readonly TreeView _tree = new();
    private readonly TabControl _tabs = new();
    private readonly PointMonitorView _pointView = new();
    private readonly FrameLogView _frameView = new();
    private readonly StationStateView _stateView = new();
    private readonly PathDiagnosticView _pathView = new();
    private readonly FaultInjectionView _faultView = new();
    private readonly ConfigImportView _importView = new();

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _frameCounterLabel = new("报文 0");
    private readonly ToolStripStatusLabel _engineLabel = new("引擎未启动");
    private readonly ToolStripStatusLabel _configLabel = new();

    private string _configPath;
    private SimulatorHost? _host;
    private long _frameVersion;
    private bool _busy;

    /// <summary>
    /// <paramref name="configPath"/> 为空时用上次打开过的配置，再退回默认路径。
    /// </summary>
    public MainForm(string? configPath)
    {
        _configPath = ResolveConfigPath(configPath);

        Text = "潜江太蓝 PLC 模拟器（Modbus TCP 服务端）";
        MinimumSize = new Size(1100, 700);
        Size = new Size(1440, 880);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadConfiguration();

        _uiTimer.Interval = UiRefreshMs;
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();
    }

    private string ResolveConfigPath(string? commandLinePath)
    {
        // 命令行给了就用命令行的；没给就用上次在界面上打开过的，都没有才用默认路径。
        if (!string.IsNullOrWhiteSpace(commandLinePath))
        {
            return commandLinePath;
        }

        if (_settings.LastConfigPath is { Length: > 0 } remembered && File.Exists(remembered))
        {
            return remembered;
        }

        return Path.Combine("config", "simulator.json");
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();

        if (_host is not null && _host.IsRunning)
        {
            e.Cancel = true;
            await _host.DisposeAsync();
            _host = null;

            // 这一次关闭已经被取消掉了，停完服务得再关一次——否则窗口留着，用户要按两次 X。
            Close();
            return;
        }

        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.Items.Add(_startStopButton);
        _toolStrip.Items.Add(_reloadButton);
        _toolStrip.Items.Add(_openConfigButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_resetButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_statusLabel);

        _startStopButton.Click += OnStartStopClick;
        _reloadButton.Click += OnReloadClick;
        _openConfigButton.Click += OnOpenConfigClick;
        _resetButton.Click += (_, _) => _host?.ResetAll();

        // 「配置生成」页的上次选择只在启动时回填一次，之后由用户自己改，
        // 免得每次重新加载配置都把用户刚填的路径冲掉。
        _importView.PrefillPaths(
            _settings.LastCsvPath ?? string.Empty,
            _settings.LastOutputPath ?? Path.Combine("config", "simulator.generated.json"));
        _importView.ConfigGenerated += OnConfigGenerated;

        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(CreateTab("点位监视", _pointView));
        _tabs.TabPages.Add(CreateTab("报文日志", _frameView));
        _tabs.TabPages.Add(CreateTab("流程状态机", _stateView));
        _tabs.TabPages.Add(CreateTab("路径诊断", _pathView));
        _tabs.TabPages.Add(CreateTab("故障注入", _faultView));
        _tabs.TabPages.Add(CreateTab("配置生成", _importView));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 260,
            FixedPanel = FixedPanel.Panel1,
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_tabs);

        var splitPanel = new Panel { Dock = DockStyle.Fill };
        splitPanel.Controls.Add(split);

        _statusStrip.Items.Add(_frameCounterLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel { Spring = false, Text = "  |  " });
        _statusStrip.Items.Add(_engineLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true, Text = string.Empty });
        _statusStrip.Items.Add(_configLabel);
        _configLabel.Text = _configPath;

        Controls.Add(splitPanel);
        Controls.Add(_toolStrip);
        Controls.Add(_statusStrip);
    }

    private static TabPage CreateTab(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(2) };
        page.Controls.Add(content);
        return page;
    }

    private void LoadConfiguration()
    {
        if (TryLoadConfig(_configPath, out ConfigLoadResult result))
        {
            ApplyConfiguration(result);
        }
    }

    /// <summary>读盘并校验。失败时弹框说明并返回 false，**不动当前已经加载的配置**。</summary>
    private bool TryLoadConfig(string configPath, out ConfigLoadResult result)
    {
        result = null!;

        try
        {
            result = ConfigLoader.LoadFromFile(configPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"加载配置失败：\n{ex.Message}\n\n路径：{Path.GetFullPath(configPath)}",
                "配置错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            SetStatus("配置加载失败");
            return false;
        }

        if (!result.IsValid)
        {
            MessageBox.Show(
                this,
                "配置校验未通过：\n\n" + string.Join("\n", result.Errors.Select(static e => "· " + e)),
                "配置错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            SetStatus("配置校验未通过");
            return false;
        }

        return true;
    }

    private void ApplyConfiguration(ConfigLoadResult result)
    {
        if (result.Warnings.Count > 0)
        {
            SetStatus($"配置警告：{result.Warnings[0]}");
        }

        _host = SimulatorHost.Create(result.Config, _frameLog);

        // Log 是引擎线程／协议线程调的，直接写控件属跨线程操作。
        // 封送回 UI 线程再显示；窗口还没句柄、或正在销毁时丢掉这条日志。
        _host.Log = message =>
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(() => _statusLabel.Text = message);
                }
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁，丢一条状态日志无妨。
            }
        };

        BuildTree(_host);
        _pointView.Bind(_host);
        _pointView.Reset();
        _stateView.Bind(_host);
        _pathView.Bind(_host);
        _faultView.Bind(_host.FaultInjector);
        _frameView.Clear();
        _frameVersion = 0;

        _configLabel.Text = _configPath;

        SetStatus($"已加载配置：{result.Config.Devices.Count} 台设备，{result.Config.Devices.Sum(static d => d.Stations.Count)} 个站台");
    }

    private void BuildTree(SimulatorHost host)
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        foreach (DeviceRuntime device in host.Devices)
        {
            var deviceNode = new TreeNode($"{device.Config.Id}  ({device.Config.Ip}:{device.Config.Port})")
            {
                Tag = device,
            };

            foreach (StationRuntime station in device.Stations)
            {
                deviceNode.Nodes.Add(new TreeNode($"站台 {station.StationNo}") { Tag = station });
            }

            _tree.Nodes.Add(deviceNode);
        }

        _tree.ExpandAll();
        _tree.EndUpdate();
    }

    private async void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_busy || _host is null)
        {
            return;
        }

        _busy = true;

        try
        {
            if (_host.IsRunning)
            {
                await _host.StopAsync();
                _startStopButton.Text = "启动";
                SetStatus("已停止");
                _engineLabel.Text = "引擎未启动";
            }
            else
            {
                await _host.StartAsync();
                _startStopButton.Text = "停止";
                SetStatus("运行中");
                _engineLabel.Text = $"引擎运行中（tick {_host.Engine.TickInterval.TotalMilliseconds:F0} ms）";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"操作失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnReloadClick(object? sender, EventArgs e) => await ReloadAsync(_configPath);

    private async void OnOpenConfigClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择模拟器配置",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        string fullPath = Path.GetFullPath(_configPath);
        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        dialog.FileName = Path.GetFileName(fullPath);

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await ReloadAsync(dialog.FileName);
        }
    }

    private async void OnConfigGenerated(object? sender, string configPath)
    {
        (string csvPath, string outputPath) = _importView.ReadPaths();
        _settings.LastCsvPath = csvPath;
        _settings.LastOutputPath = outputPath;
        _settings.Save();

        DialogResult answer = MessageBox.Show(
            this,
            $"配置已生成：\n{Path.GetFullPath(configPath)}\n\n是否立即加载这份配置？",
            "配置生成完成",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Yes)
        {
            await ReloadAsync(configPath);
        }
    }

    /// <summary>换一份配置重新加载；之前在运行的话重新启动。</summary>
    private async Task ReloadAsync(string configPath)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            // 先按新路径试加载并校验，通过了才动当前这份——
            // 选错文件不至于把手上的配置也一起丢掉。
            if (!TryLoadConfig(configPath, out ConfigLoadResult result))
            {
                return;
            }

            bool wasRunning = _host?.IsRunning ?? false;

            if (_host is not null)
            {
                await _host.DisposeAsync();
                _host = null;
            }

            _configPath = configPath;
            _settings.LastConfigPath = configPath;
            _settings.Save();

            ApplyConfiguration(result);

            if (wasRunning && _host is not null)
            {
                await _host.StartAsync();
                _startStopButton.Text = "停止";
                _engineLabel.Text = $"引擎运行中（tick {_host.Engine.TickInterval.TotalMilliseconds:F0} ms）";
                SetStatus("配置已重新加载并启动");
            }
            else
            {
                _startStopButton.Text = "启动";
                _engineLabel.Text = "引擎未启动";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"切换配置失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            _pointView.RefreshDirty();

            FrameLogEntry[] batch = _frameLog.TakeSince(_frameVersion, maxItems: 2000, out long nextVersion);
            _frameVersion = nextVersion;

            if (batch.Length > 0)
            {
                _frameView.Append(batch);
                _frameCounterLabel.Text = $"报文 {_frameLog.TotalWritten}";
            }

            IReadOnlyList<StationSnapshot> snapshots = _host.Engine.Snapshots();
            _stateView.Update(snapshots);
            _faultView.RefreshHits();
        }
        catch (Exception ex)
        {
            SetStatus($"刷新失败：{ex.Message}");
        }
    }

    private void SetStatus(string message) => _statusLabel.Text = message;
}
