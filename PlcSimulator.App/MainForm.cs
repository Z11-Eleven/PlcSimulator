using PlcSimulator.App.Shared;
using PlcSimulator.App.Views;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Frames;
using PlcSimulator.Devices;
using PlcSimulator.Devices.Srm;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Hosting;

namespace PlcSimulator.App;

/// <summary>
/// 调试台主窗体。按工程约束不使用窗体设计器，所有控件在代码里创建。
/// <para>
/// 可以**同时挂多份配置**：每份各有一个 <see cref="SimulatorHost"/>，各自监听自己的端口，
/// 互不覆盖（输送线走 Modbus 502，堆垛机走另外三个端口）。左侧树按配置分组，
/// 右侧页签的内容跟随当前选中的配置；「报文日志」与「配置生成」是全局的，不随选中项切换。
/// </para>
/// <para>
/// 后台线程（协议层、状态机引擎）从不触碰控件：它们只写数据区与环形缓冲，
/// UI 通过 300ms 定时器主动拉取，并只刷新发生变化的部分。
/// </para>
/// </summary>
internal sealed class MainForm : Form
{
    private const int UiRefreshMs = 300;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _startStopButton = new("启动");
    private readonly ToolStripButton _startAllButton = new("全部启动");
    private readonly ToolStripButton _stopAllButton = new("全部停止");
    private readonly ToolStripButton _openConfigButton = new("打开配置…");
    private readonly ToolStripButton _closeConfigButton = new("关闭配置");
    private readonly ToolStripButton _reloadButton = new("重新加载");
    private readonly ToolStripButton _resetButton = new("复位");
    private readonly ToolStripLabel _statusLabel = new("未加载配置");

    private readonly TreeView _tree = new();
    private readonly TabControl _tabs = new();
    private readonly PointMonitorView _pointView = new();
    private readonly FrameLogView _frameView = new();
    private readonly StationStateView _stateView = new();
    private readonly SrmStateView _srmView = new();
    private readonly PathDiagnosticView _pathView = new();
    private readonly FaultInjectionView _faultView = new();
    private readonly ConfigImportView _importView = new();

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _frameCounterLabel = new("报文 0");
    private readonly ToolStripStatusLabel _engineLabel = new("引擎未启动");
    private readonly ToolStripStatusLabel _configLabel = new();

    private readonly List<SimulatorSession> _configs = [];
    private SimulatorSession? _current;
    private long _frameVersion;
    private bool _busy;

    /// <summary>
    /// <paramref name="configPath"/> 非空时只开那一份；为空时把上次打开的几份都挂上。
    /// </summary>
    public MainForm(string? configPath)
    {
        Text = "潜江太蓝 PLC 模拟器";
        MinimumSize = new Size(1100, 700);
        Size = new Size(1440, 880);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadInitialConfigurations(configPath);

        _uiTimer.Interval = UiRefreshMs;
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();

        if (_configs.Exists(static c => c.IsRunning))
        {
            e.Cancel = true;

            foreach (SimulatorSession config in _configs)
            {
                await config.Host.DisposeAsync();
            }

            _configs.Clear();
            _current = null;

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
        _toolStrip.Items.Add(_startAllButton);
        _toolStrip.Items.Add(_stopAllButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_openConfigButton);
        _toolStrip.Items.Add(_closeConfigButton);
        _toolStrip.Items.Add(_reloadButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_resetButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_statusLabel);

        _startStopButton.Click += OnStartStopClick;
        _startAllButton.Click += OnStartAllClick;
        _stopAllButton.Click += OnStopAllClick;
        _openConfigButton.Click += OnOpenConfigClick;
        _closeConfigButton.Click += OnCloseConfigClick;
        _reloadButton.Click += OnReloadClick;
        _resetButton.Click += (_, _) => _current?.Host.ResetAll();

        // 「配置生成」页的上次选择只在启动时回填一次，之后由用户自己改，
        // 免得每次重新加载配置都把用户刚填的路径冲掉。
        _importView.PrefillPaths(
            _settings.LastCsvPath ?? string.Empty,
            _settings.LastOutputPath ?? Path.Combine("config", "simulator.generated.json"));
        _importView.ConfigGenerated += OnConfigGenerated;

        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.ShowNodeToolTips = true;
        _tree.AfterSelect += OnTreeSelect;

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(CreateTab("点位监视", _pointView));
        _tabs.TabPages.Add(CreateTab("流程状态机", _stateView));
        _tabs.TabPages.Add(CreateTab("堆垛机", _srmView));
        _tabs.TabPages.Add(CreateTab("路径诊断", _pathView));
        _tabs.TabPages.Add(CreateTab("故障注入", _faultView));
        _tabs.TabPages.Add(CreateTab("报文日志", _frameView));
        _tabs.TabPages.Add(CreateTab("配置生成", _importView));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 300,
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

    // ---- 加载与切换 ----

    private void LoadInitialConfigurations(string? commandLinePath)
    {
        List<string> paths = !string.IsNullOrWhiteSpace(commandLinePath)
            ? [commandLinePath]
            : [.. _settings.OpenConfigPaths.Where(File.Exists)];

        if (paths.Count == 0)
        {
            paths = [Path.Combine("config", "simulator.json")];
        }

        int failures = 0;

        foreach (string path in paths)
        {
            if (TryOpenConfiguration(path, out _))
            {
                continue;
            }

            failures++;
        }

        RefreshTree();

        if (_configs.Count > 0)
        {
            SelectConfiguration(_configs[0]);
        }

        SetStatus(_configs.Count switch
        {
            0 => "没有加载任何配置，请用「打开配置…」选一份",
            _ when failures > 0 => $"已加载 {_configs.Count} 份配置，另有 {failures} 份没加载成功",
            _ => $"已加载 {_configs.Count} 份配置",
        });
    }

    /// <summary>
    /// 读盘、校验、建 host 并挂上。失败时弹框说明并返回 false，
    /// **不影响已经挂着的其它配置**——这是「加载一份就顶掉另一份」的老毛病的解药。
    /// </summary>
    private bool TryOpenConfiguration(string configPath, out SimulatorSession? opened)
    {
        opened = null;

        if (_configs.Exists(c => string.Equals(c.Path, configPath, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"这份配置已经打开了：{configPath}");
            return false;
        }

        if (!SessionFactory.TryLoad(configPath, out SimulatorSession session, out string failure))
        {
            MessageBox.Show(this, failure, "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("配置加载失败");
            return false;
        }

        // 引擎／协议线程的日志由会话接住（这样窗口后开也不会错过开局那几条），
        // 这里只负责封送回 UI 线程显示，并带上来源文件名——多份配置共用一个状态栏。
        session.Logged += (_, message) => PostStatus($"[{session.DisplayName}] {message}");

        _configs.Add(session);
        opened = session;

        if (session.Result.Warnings.Count > 0)
        {
            SetStatus($"配置警告：{session.Result.Warnings[0]}");
        }

        return true;
    }

    /// <summary>把后台线程来的状态消息封送回 UI 线程。</summary>
    private void PostStatus(string message)
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
    }

    /// <summary>加载一份配置；同路径已经挂着就先替换掉它。返回是否成功。</summary>
    private async Task<bool> LoadOrReplaceAsync(string configPath)
    {
        SimulatorSession? existing = _configs.Find(
            c => string.Equals(c.Path, configPath, StringComparison.OrdinalIgnoreCase));

        bool restart = existing?.IsRunning ?? false;

        if (existing is not null)
        {
            await CloseConfigurationAsync(existing);
        }

        if (!TryOpenConfiguration(configPath, out SimulatorSession? opened) || opened is null)
        {
            RefreshTree();
            return false;
        }

        RefreshTree();
        SelectConfiguration(opened);
        SaveOpenConfigs();

        if (restart)
        {
            await opened.Host.StartAsync();
            UpdateRunState();
        }

        SetStatus($"已加载：{opened.DisplayName}（当前 {_configs.Count} 份配置）");
        return true;
    }

    private async Task CloseConfigurationAsync(SimulatorSession config)
    {
        await config.Host.DisposeAsync();

        _configs.Remove(config);

        if (ReferenceEquals(_current, config))
        {
            _current = null;
        }
    }

    /// <summary>把右侧各页签重新绑到选中的这份配置上。</summary>
    private void SelectConfiguration(SimulatorSession session)
    {
        _current = session;

        // 换了配置就换了报文流水，游标与显示都要跟着重来。
        _frameView.Clear();
        _frameVersion = 0;

        _pointView.Bind(session.Host);
        _pointView.Reset();
        _stateView.Bind(session.Host);
        _srmView.Bind(session.Host);
        _pathView.Bind(session.Host);
        _faultView.Bind(session.Host.FaultInjector);

        _configLabel.Text = Path.GetFullPath(session.Path);

        SelectDefaultTabFor(session);
        UpdateRunState();
    }

    /// <summary>
    /// 切到这份配置有内容的那一页。纯堆垛机的配置里「点位监视」（站台字段表）是空白的，
    /// 停在那一页会让人以为没加载成功。
    /// </summary>
    private void SelectDefaultTabFor(SimulatorSession session)
    {
        bool hasStations = session.Host.Devices.Count > 0;
        bool hasSrm = session.Host.SrmDevices.Count > 0;

        string? target = (hasStations, hasSrm) switch
        {
            (false, true) => "堆垛机",
            (true, _) => "点位监视",
            _ => null,
        };

        if (target is null)
        {
            return;
        }

        foreach (TabPage page in _tabs.TabPages)
        {
            if (string.Equals(page.Text, target, StringComparison.Ordinal))
            {
                _tabs.SelectedTab = page;
                return;
            }
        }
    }

    private void UpdateRunState()
    {
        bool hasCurrent = _current is not null;
        bool running = _current?.IsRunning ?? false;

        _startStopButton.Text = running ? "停止" : "启动";
        _startStopButton.Enabled = hasCurrent;
        _closeConfigButton.Enabled = hasCurrent;
        _reloadButton.Enabled = hasCurrent;
        _resetButton.Enabled = hasCurrent;

        _engineLabel.Text = running
            ? $"运行中（tick {_current!.Host.Engine.TickInterval.TotalMilliseconds:F0} ms）"
            : hasCurrent ? "已停止" : "未加载配置";

        UpdateTreeRunState();
    }

    /// <summary>重建整棵树。只在配置增删时调，刷新运行标记走 <see cref="UpdateTreeRunState"/>。</summary>
    private void RefreshTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        foreach (SimulatorSession config in _configs)
        {
            var configNode = new TreeNode(config.DisplayNameWithState)
            {
                Tag = config,
                ToolTipText = config.ToolTip,
            };

            foreach (DeviceRuntime device in config.Host.Devices)
            {
                var deviceNode = new TreeNode($"{device.Config.Id}  ({device.Config.Ip}:{device.Config.Port})")
                {
                    Tag = device,
                };

                foreach (StationRuntime station in device.Stations)
                {
                    deviceNode.Nodes.Add(new TreeNode($"站台 {station.StationNo}") { Tag = station });
                }

                configNode.Nodes.Add(deviceNode);
            }

            // 堆垛机不是站台模型，单独挂一层。
            foreach (SrmDeviceRuntime device in config.Host.SrmDevices)
            {
                configNode.Nodes.Add(new TreeNode(
                    $"{device.DeviceId}  ({device.Config.Ip} 指令{device.Ports.Command})")
                {
                    Tag = device,
                });
            }

            _tree.Nodes.Add(configNode);

            if (ReferenceEquals(config, _current))
            {
                _tree.SelectedNode = configNode;
            }
        }

        _tree.ExpandAll();
        _tree.EndUpdate();
    }

    /// <summary>只改配置节点的运行标记。重建整棵树会丢掉展开状态与选中项，刷不动。</summary>
    private void UpdateTreeRunState()
    {
        foreach (TreeNode node in _tree.Nodes)
        {
            if (node.Tag is not SimulatorSession config)
            {
                continue;
            }

            string text = config.DisplayNameWithState;
            if (!string.Equals(node.Text, text, StringComparison.Ordinal))
            {
                node.Text = text;
            }
        }
    }

    private void SaveOpenConfigs()
    {
        _settings.OpenConfigPaths = [.. _configs.Select(static c => c.Path)];
        _settings.Save();
    }

    // ---- 工具栏 ----

    private async void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_busy || _current is null)
        {
            return;
        }

        SimulatorSession config = _current;
        _busy = true;

        try
        {
            if (config.IsRunning)
            {
                await config.Host.StopAsync();
                SetStatus($"已停止：{config.DisplayName}");
            }
            else
            {
                await config.Host.StartAsync();
                SetStatus($"运行中：{config.DisplayName}");
            }

            UpdateRunState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"操作失败：{ex.Message}\n\n{config.DisplayName}",
                "错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnStartAllClick(object? sender, EventArgs e)
    {
        if (_busy || _configs.Count == 0)
        {
            return;
        }

        _busy = true;

        try
        {
            foreach (SimulatorSession config in _configs.Where(static c => !c.IsRunning).ToList())
            {
                try
                {
                    await config.Host.StartAsync();
                }
                catch (Exception ex)
                {
                    // 一份起不来（多半是端口被占）不该拦着其它的。
                    SetStatus($"{config.DisplayName} 启动失败：{ex.Message}");
                }
            }

            UpdateRunState();
            SetStatus($"已启动 {_configs.Count(static c => c.IsRunning)}/{_configs.Count} 份配置");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnStopAllClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            foreach (SimulatorSession config in _configs.Where(static c => c.IsRunning).ToList())
            {
                await config.Host.StopAsync();
            }

            UpdateRunState();
            SetStatus("已停止全部配置");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnOpenConfigClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择模拟器配置（可重复打开，多份并存）",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        string startFrom = _current?.Path ?? _settings.OpenConfigPaths.FirstOrDefault() ?? Path.Combine("config", "simulator.json");
        string fullPath = Path.GetFullPath(startFrom);
        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await LoadOrReplaceAsync(dialog.FileName);
        }
    }

    private async void OnCloseConfigClick(object? sender, EventArgs e)
    {
        if (_busy || _current is null)
        {
            return;
        }

        SimulatorSession closing = _current;
        _busy = true;

        try
        {
            await CloseConfigurationAsync(closing);
            SaveOpenConfigs();

            if (_configs.Count > 0)
            {
                RefreshTree();
                SelectConfiguration(_configs[0]);
                SetStatus($"已关闭：{closing.DisplayName}");
            }
            else
            {
                RefreshTree();
                UpdateRunState();
                _configLabel.Text = string.Empty;
                SetStatus("已关闭全部配置，请用「打开配置…」再选一份");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnReloadClick(object? sender, EventArgs e)
    {
        if (_busy || _current is null)
        {
            return;
        }

        _busy = true;
        string path = _current.Path;

        try
        {
            if (await LoadOrReplaceAsync(path))
            {
                SetStatus($"已重新加载：{Path.GetFileName(path)}");
            }
        }
        finally
        {
            _busy = false;
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
            await LoadOrReplaceAsync(configPath);
        }
    }

    private void OnTreeSelect(object? sender, TreeViewEventArgs e)
    {
        // 选中设备/站台节点时也切到它所属的那份配置。
        SimulatorSession? config = e.Node?.Tag as SimulatorSession
            ?? e.Node?.Parent?.Tag as SimulatorSession;

        if (config is not null && !ReferenceEquals(config, _current))
        {
            SelectConfiguration(config);
        }
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        try
        {
            UpdateTreeRunState();

            if (_current is null)
            {
                return;
            }

            // 报文日志来自当前这份配置自己的环形缓冲——每份配置一条流水，互不掺和。
            FrameLogEntry[] batch = _current.FrameLog.TakeSince(_frameVersion, maxItems: 2000, out long nextVersion);
            _frameVersion = nextVersion;

            if (batch.Length > 0)
            {
                _frameView.Append(batch);
                _frameCounterLabel.Text = $"报文 {_current.LogRevision}";
            }

            _pointView.RefreshDirty();
            _stateView.Update(_current.Host.Engine.Snapshots());
            _srmView.UpdateMachines();
            _faultView.RefreshHits();
        }
        catch (Exception ex)
        {
            SetStatus($"刷新失败：{ex.Message}");
        }
    }

    private void SetStatus(string message) => _statusLabel.Text = message;
}
