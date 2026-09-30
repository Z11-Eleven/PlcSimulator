using PlcSimulator.App.Conveyor;
using PlcSimulator.App.Shared;
using PlcSimulator.App.Srm;

namespace PlcSimulator.App;

/// <summary>
/// 调试台主窗口——**只做会话总览**：列出已加载的配置与运行状态，双击开或切到它的窗口。
/// <para>
/// 设备明细不在这一层：输送线的站台在「输送机模拟器」窗口的点位监视表里，
/// 堆垛机在「堆垛机模拟器」窗口的面板里。两类功能各自成窗，互不参杂。
/// </para>
/// </summary>
internal sealed class MainForm : Form
{
    private const int UiRefreshMs = 300;

    private const int ColumnConfig = 0;
    private const int ColumnComposition = 1;
    private const int ColumnState = 2;
    private const int ColumnFrames = 3;
    private const int ColumnPath = 4;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _openConfigButton = new("打开配置…");
    private readonly ToolStripButton _closeConfigButton = new("关闭配置");
    private readonly ToolStripButton _startAllButton = new("全部启动");
    private readonly ToolStripButton _stopAllButton = new("全部停止");
    private readonly ToolStripButton _generateConfigButton = new("配置生成…");
    private readonly ToolStripLabel _statusLabel = new("未加载配置");

    private readonly ListView _overview = new();

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _summaryLabel = new();

    private readonly List<SimulatorSession> _configs = [];

    /// <summary>已经开出来的子窗口，按配置路径索引（不区分大小写，与判重口径一致）。</summary>
    private readonly Dictionary<string, SimulatorWindowBase> _windows = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<SimulatorSession> _pendingPresent = [];
    /// <summary>新开窗口的层叠序号，让多个子窗口在屏幕上错开而不是完全重叠。</summary>
    private int _windowCascade;

    private bool _busy;

    /// <summary>
    /// <paramref name="configPaths"/> 为空时把上次打开的几份加载到总览；给了就只加载这几份。
    /// </summary>
    public MainForm(IReadOnlyList<string> configPaths)
    {
        Text = "PLC 模拟器";
        MinimumSize = new Size(900, 500);
        Size = new Size(1180, 620);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadInitialConfigurations(configPaths);

        _uiTimer.Interval = UiRefreshMs;
        _uiTimer.Tick += (_, _) => RefreshOverview();
        _uiTimer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_pendingPresent.Count == 0)
        {
            return;
        }

        // 呈现在这里而不是构造函数里：那时本窗口还没有句柄与位置，
        // 子窗口以它为 owner 时定位与显示都没有可依据的坐标。
        SimulatorSession[] pending = [.. _pendingPresent];
        _pendingPresent.Clear();

        foreach (SimulatorSession session in pending)
        {
            OpenSessionWindow(session);
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();

        if (_configs.Count > 0)
        {
            e.Cancel = true;

            // 先显式关掉各个子窗口（只是收界面，不停服务），再统一停掉所有引擎——
            // 子窗口关闭不停服务，停引擎这件事只在这里做一次。不依赖「owner 关闭时
            // 连带关闭 owned form」与 e.Cancel 的隐晦交互。
            foreach (SimulatorWindowBase window in _windows.Values.ToList())
            {
                window.Close();
            }

            _windows.Clear();

            foreach (SimulatorSession session in _configs)
            {
                await session.DisposeAsync().ConfigureAwait(true);
            }

            _configs.Clear();

            // 这一次关闭已经被取消掉了，停完服务得再关一次——否则窗口留着，用户要按两次 X。
            Close();
            return;
        }

        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.Items.Add(_startAllButton);
        _toolStrip.Items.Add(_stopAllButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_openConfigButton);
        _toolStrip.Items.Add(_closeConfigButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_generateConfigButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_statusLabel);

        _startAllButton.Click += OnStartAllClick;
        _stopAllButton.Click += OnStopAllClick;
        _openConfigButton.Click += OnOpenConfigClick;
        _closeConfigButton.Click += OnCloseConfigClick;
        _generateConfigButton.Click += OnGenerateConfigClick;

        _overview.Dock = DockStyle.Fill;
        _overview.View = View.Details;
        _overview.FullRowSelect = true;
        _overview.MultiSelect = false;
        _overview.HideSelection = false;
        _overview.GridLines = true;

        _overview.Columns.Add("配置", 220);
        _overview.Columns.Add("构成", 200);
        _overview.Columns.Add("状态", 90);
        _overview.Columns.Add("报文", 80);
        _overview.Columns.Add("路径", 560);

        _overview.DoubleClick += OnOverviewDoubleClick;

        _statusStrip.Items.Add(_summaryLabel);

        Controls.Add(_overview);
        Controls.Add(_toolStrip);
        Controls.Add(_statusStrip);
    }

    // ---- 加载与呈现 ----

    private void LoadInitialConfigurations(IReadOnlyList<string> commandLinePaths)
    {
        List<string> paths = commandLinePaths.Count > 0
            ? [.. commandLinePaths]
            : [.. _settings.OpenConfigPaths.Where(File.Exists)];

        if (paths.Count == 0)
        {
            paths = [Path.Combine("config", "simulator.json")];
        }

        int failures = 0;

        foreach (string path in paths)
        {
            if (!TryAddConfiguration(path, out _))
            {
                failures++;
            }
        }

        RebuildOverview();

        if (commandLinePaths.Count > 0 && _configs.Count > 0)
        {
            // 命令行明确指定配置时按请求打开对应子窗口；从设置恢复的配置只加载到总览，
            // 由用户双击后再打开子窗口。
            _pendingPresent.AddRange(_configs);
        }

        SetStatus(_configs.Count switch
        {
            0 => "没有加载任何配置，请用「打开配置…」选一份",
            _ when failures > 0 => $"已加载 {_configs.Count} 份配置，另有 {failures} 份没加载成功",
            _ => $"已加载 {_configs.Count} 份配置，双击一行打开它的模拟器窗口",
        });
    }

    /// <summary>
    /// 读盘、校验、建会话并挂上。失败时弹框说明并返回 false，
    /// **不影响已经挂着的其它配置**。
    /// </summary>
    private bool TryAddConfiguration(string configPath, out SimulatorSession? added)
    {
        added = null;

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

        // 会话接住宿主日志（这样窗口后开也不会错过 Build 阶段那几条），
        // 这里只把「这份配置出事了」这类消息显示到主窗口状态栏。
        session.Logged += (_, message) => PostStatus($"[{session.DisplayName}] {message}");

        _configs.Add(session);
        added = session;

        return true;
    }

    private void OpenSessionWindow(SimulatorSession session)
    {
        if (_windows.TryGetValue(session.Path, out SimulatorWindowBase? existing))
        {
            if (existing.WindowState == FormWindowState.Minimized)
            {
                existing.WindowState = FormWindowState.Normal;
            }

            existing.Activate();
            SetStatus($"已切到：{session.DisplayName}");
            return;
        }

        // 纯堆垛机开堆垛机窗口，其余（输送线、空配置）开输送机窗口——
        // 空配置时那个窗口会带一条横幅说明。
        SimulatorWindowBase window = session.Composition == DeviceComposition.SrmOnly
            ? new SrmSimulatorForm(session)
            : new ConveyorSimulatorForm(session);

        window.FormClosed += (_, _) => OnSessionWindowClosed(session);

        _windows[session.Path] = window;

        // 不用 Show(this)：给了 owner 之后子窗口会**永远**浮在主窗口之上，
        // 用户想让主窗口或别的窗口盖住它就做不到，开两个状态机时互相挡着。
        // 落点在屏幕上依次错开，免得新窗口正好压在已有的那个上面。
        window.Location = NextWindowLocation();
        window.Show();

        SetStatus($"已打开：{session.DisplayName}（{session.DeviceSummary}）");
    }

    /// <summary>新窗口的落点：沿屏幕左上方向右下层叠错开。</summary>
    private Point NextWindowLocation()
    {
        Rectangle area = Screen.FromControl(this).WorkingArea;

        int step = _windowCascade++ % 5 * 32;

        return new Point(
            Math.Min(area.Left + 80 + step, Math.Max(area.Left, area.Right - 420)),
            Math.Min(area.Top + 60 + step, Math.Max(area.Top, area.Bottom - 320)));
    }

    private void OnSessionWindowClosed(SimulatorSession session)
    {
        _windows.Remove(session.Path);

        // 关窗口只是把界面收起来，**不卸载配置、也不停服务**——引擎继续在后台跑，
        // 主窗口里双击那一行就能把窗口再开出来。真想停下用工具栏的「全部停止」。
        SetStatus(session.IsRunning
            ? $"已关闭窗口：{session.DisplayName}（引擎仍在后台运行，双击可重新打开）"
            : $"已关闭窗口：{session.DisplayName}（配置仍在列表里，双击可重新打开）");
    }

    // ---- 总览 ----

    private void RebuildOverview()
    {
        _overview.BeginUpdate();
        _overview.Items.Clear();

        foreach (SimulatorSession session in _configs)
        {
            var item = new ListViewItem(session.DisplayName) { Tag = session };
            item.SubItems.Add(session.DeviceSummary);
            item.SubItems.Add(session.IsRunning ? "运行中" : "已停止");
            item.SubItems.Add(session.LogRevision.ToString());
            item.SubItems.Add(Path.GetFullPath(session.Path));

            _overview.Items.Add(item);
        }

        _overview.EndUpdate();
        RefreshOverview();
    }

    /// <summary>只更新会变的两列，不重建——重建会丢选中项。</summary>
    private void RefreshOverview()
    {
        foreach (ListViewItem item in _overview.Items)
        {
            if (item.Tag is not SimulatorSession session)
            {
                continue;
            }

            SetSubItem(item, ColumnState, session.IsRunning ? "运行中" : "已停止");
            SetSubItem(item, ColumnFrames, session.LogRevision.ToString());
        }

        bool hasAny = _configs.Count > 0;
        _closeConfigButton.Enabled = hasAny;
        _startAllButton.Enabled = hasAny && _configs.Exists(static c => !c.IsRunning);
        _stopAllButton.Enabled = hasAny && _configs.Exists(static c => c.IsRunning);

        int running = _configs.Count(static c => c.IsRunning);
        _summaryLabel.Text = hasAny
            ? $"{_configs.Count} 份配置，{running} 份运行中"
            : "未加载配置";
    }

    private static void SetSubItem(ListViewItem item, int index, string value)
    {
        if (!string.Equals(item.SubItems[index].Text, value, StringComparison.Ordinal))
        {
            item.SubItems[index].Text = value;
        }
    }

    private void OnOverviewDoubleClick(object? sender, EventArgs e)
    {
        if (_overview.SelectedItems.Count > 0
            && _overview.SelectedItems[0].Tag is SimulatorSession session)
        {
            OpenSessionWindow(session);
        }
    }

    // ---- 工具栏 ----

    private async void OnStartAllClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            foreach (SimulatorSession session in _configs.Where(static c => !c.IsRunning).ToList())
            {
                // 一份起不来（多半是端口被占）不该拦着其它的。
                await SessionCommands.StartAsync(session, this, SetStatus).ConfigureAwait(true);
            }

            RefreshOverview();
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
            foreach (SimulatorSession session in _configs.Where(static c => c.IsRunning).ToList())
            {
                await SessionCommands.StopAsync(session, SetStatus).ConfigureAwait(true);
            }

            RefreshOverview();
            SetStatus("已停止全部配置");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnOpenConfigClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        using OpenFileDialog dialog = FileDialogs.Open(
            "选择模拟器配置（可多份并存）",
            "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*");

        string startFrom = _configs.FirstOrDefault()?.Path
            ?? _settings.OpenConfigPaths.FirstOrDefault()
            ?? Path.Combine("config", "simulator.json");

        string fullPath = Path.GetFullPath(startFrom);
        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!TryAddConfiguration(dialog.FileName, out SimulatorSession? added) || added is null)
        {
            return;
        }

        SaveOpenConfigs();
        RebuildOverview();
        OpenSessionWindow(added);
    }

    /// <summary>
    /// 卸载一份配置：关掉它的窗口、停掉服务、从总览里移除。
    /// 与「关窗口」不同——关窗口只是收起界面，配置还留着。
    /// </summary>
    private async void OnCloseConfigClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_overview.SelectedItems.Count == 0)
        {
            SetStatus("先在列表里选中一份配置");
            return;
        }

        if (_overview.SelectedItems[0].Tag is not SimulatorSession session)
        {
            return;
        }

        _busy = true;

        try
        {
            // 窗口开着就先关窗口（只是收界面，不停服务）；停服务统一由下面的 DisposeAsync 做。
            if (_windows.TryGetValue(session.Path, out SimulatorWindowBase? window))
            {
                window.Close();
            }

            await session.DisposeAsync().ConfigureAwait(true);

            _windows.Remove(session.Path);
            _configs.Remove(session);
            SaveOpenConfigs();
            RebuildOverview();

            SetStatus($"已卸载：{session.DisplayName}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnGenerateConfigClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new ConfigGeneratorForm(
            _settings.LastCsvPath ?? string.Empty,
            _settings.LastOutputPath ?? Path.Combine("config", "simulator.generated.json"));

        dialog.ConfigGenerated += OnConfigGenerated;
        dialog.ShowDialog(this);
    }

    private void OnConfigGenerated(object? sender, string configPath)
    {
        if (sender is ConfigGeneratorForm form)
        {
            // 记住这次的输入输出路径，下次打开直接回填。
            (string csvPath, string outputPath) = form.ReadPaths();
            _settings.LastCsvPath = csvPath;
            _settings.LastOutputPath = outputPath;
            _settings.Save();
        }

        DialogResult answer = MessageBox.Show(
            this,
            $"配置已生成：\n{Path.GetFullPath(configPath)}\n\n是否立即加载这份配置？",
            "配置生成完成",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        if (!TryAddConfiguration(configPath, out SimulatorSession? added) || added is null)
        {
            return;
        }

        SaveOpenConfigs();
        RebuildOverview();
        OpenSessionWindow(added);
    }

    private void SaveOpenConfigs()
    {
        _settings.OpenConfigPaths = [.. _configs.Select(static c => c.Path)];
        _settings.Save();
    }

    /// <summary>把后台线程来的状态消息封送回 UI 线程。</summary>
    private void PostStatus(string message)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(() => SetStatus(message));
            }
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁，丢一条状态日志无妨。
        }
    }

    private void SetStatus(string message) => _statusLabel.Text = message;
}
