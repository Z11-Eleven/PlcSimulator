using PlcSimulator.Core.Frames;

namespace PlcSimulator.App.Shared;

/// <summary>
/// 子窗口骨架：一份配置一个窗口。
/// <para>
/// 基类只管**生命周期**——定时器、异步关闭、重载换宿主、报文游标、状态栏、启停工具栏；
/// 不管布局，页签由子类在构造体里用 <see cref="AddTab"/> 自己加。这样重复的不是布局，
/// 而是那几段「一个窗口停干净了、另一个没停」最难靠评审发现的生命周期代码。
/// </para>
/// <para>
/// 判据：基类里一旦出现 <c>this is XxxForm</c> 这类分支，就该把它拆掉。
/// </para>
/// </summary>
public abstract class SimulatorWindowBase : Form
{
    private const int UiRefreshMs = 300;

    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _startStopButton = new("启动");
    private readonly ToolStripButton _resetButton = new("复位");
    private readonly ToolStripButton _reloadButton = new("重新加载");
    private readonly ToolStripLabel _statusLabel = new("就绪");
    private readonly Panel _banner = new();
    private readonly Label _bannerLabel = new();
    private readonly TabControl _tabs = new();
    private readonly FrameLogView _frameView = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _runLabel = new("已停止");
    private readonly ToolStripStatusLabel _frameLabel = new("报文 0");
    private readonly ToolStripStatusLabel _pathLabel = new();

    private long _frameVersion;
    private bool _busy;
    private bool _frameLogTabAdded;
    private DeviceComposition _lastComposition;

    protected SimulatorWindowBase(SimulatorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        _lastComposition = session.Composition;

        Text = $"{WindowKind} — {session.DisplayName}";
        MinimumSize = new Size(900, 600);
        Size = new Size(1200, 780);
        StartPosition = FormStartPosition.CenterParent;

        BuildLayout();

        Session.Reloaded += (_, _) => OnSessionReloaded();
        Session.Logged += (_, message) => PostStatus(message);

        _uiTimer.Interval = UiRefreshMs;
        _uiTimer.Tick += OnUiTick;
    }

    public SimulatorSession Session { get; }

    /// <summary>窗口正在关闭（正等着服务停下来）。主窗口据此拒绝重复打开同一份配置。</summary>
    public bool ClosingInProgress { get; private set; }

    /// <summary>标题栏里的窗口类型名。</summary>
    protected abstract string WindowKind { get; }

    /// <summary>把视图绑到 <see cref="Session"/>.Host。初次与重载后都走这里。</summary>
    protected abstract void BindSession();

    /// <summary>每个 UI 周期的刷新钩子，子类刷自己的视图。</summary>
    protected virtual void RefreshSession()
    {
    }

    /// <summary>加一个功能页。子类在构造体里调用；「报文日志」由基类在 OnLoad 追加到最后。</summary>
    protected void AddTab(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(2) };
        page.Controls.Add(content);
        _tabs.TabPages.Add(page);
    }

    /// <summary>顶部常驻提示条（混合配置、空配置这类需要一眼看到的情况）。</summary>
    protected void ShowBanner(string text)
    {
        _bannerLabel.Text = text;
        _banner.Visible = true;
    }

    protected void SetStatus(string message) => _statusLabel.Text = message;

    /// <summary>切到指定标题的页；找不到就不动。</summary>
    protected void SelectTab(string title)
    {
        foreach (TabPage page in _tabs.TabPages)
        {
            if (string.Equals(page.Text, title, StringComparison.Ordinal))
            {
                _tabs.SelectedTab = page;
                return;
            }
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 子类的构造体在基类之后才跑，所以「最后一个页签」的位置只能在这里占——
        // 基类构造函数里加的话，报文日志会跑到子类那些页的中间去。
        if (!_frameLogTabAdded)
        {
            _frameLogTabAdded = true;
            AddTab("报文日志", _frameView);
        }

        BindSession();

        // 窗口可能后开：把会话已经攒下的日志补显示一条，免得看起来像什么都没发生。
        IReadOnlyList<string> history = Session.LogHistory;
        if (history.Count > 0)
        {
            SetStatus(history[^1]);
        }

        UpdateRunState();
        _uiTimer.Start();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();

        if (Session.IsRunning && !ClosingInProgress)
        {
            // 先停服务再关窗，否则会留下几百毫秒「窗口没了、端口还占着」的窗口期，
            // 用户手快重开同一份配置就会撞端口。
            e.Cancel = true;
            ClosingInProgress = true;

            await Session.StopAsync().ConfigureAwait(true);

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
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_resetButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_statusLabel);

        _startStopButton.Click += OnStartStopClick;
        _reloadButton.Click += OnReloadClick;
        _resetButton.Click += (_, _) => SessionCommands.Reset(Session, SetStatus);

        _banner.Dock = DockStyle.Top;
        _banner.Height = 34;
        _banner.BackColor = Color.FromArgb(255, 243, 205);
        _banner.Padding = new Padding(10, 0, 10, 0);
        _banner.Visible = false;

        _bannerLabel.Dock = DockStyle.Fill;
        _bannerLabel.TextAlign = ContentAlignment.MiddleLeft;
        _bannerLabel.ForeColor = Color.FromArgb(120, 80, 0);
        _banner.Controls.Add(_bannerLabel);

        _tabs.Dock = DockStyle.Fill;

        _statusStrip.Items.Add(_runLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel { Text = "  |  " });
        _statusStrip.Items.Add(_frameLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true, Text = string.Empty });
        _statusStrip.Items.Add(_pathLabel);

        _pathLabel.Text = System.IO.Path.GetFullPath(Session.Path);

        // Dock 的停靠优先级由 Z 序决定（后加的先停靠），所以 Fill 的页签要先加。
        Controls.Add(_tabs);
        Controls.Add(_banner);
        Controls.Add(_toolStrip);
        Controls.Add(_statusStrip);
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        try
        {
            PullFrameLog();
            UpdateRunState();
            RefreshSession();
        }
        catch (Exception ex)
        {
            SetStatus($"刷新失败：{ex.Message}");
        }
    }

    /// <summary>从这份会话自己的环形缓冲增量拉报文——每份配置一条流水，互不掺和。</summary>
    private void PullFrameLog()
    {
        FrameLogEntry[] batch = Session.FrameLog.TakeSince(_frameVersion, maxItems: 2000, out long nextVersion);
        _frameVersion = nextVersion;

        if (batch.Length > 0)
        {
            _frameView.Append(batch);
        }

        _frameLabel.Text = $"报文 {Session.LogRevision}";
    }

    private void UpdateRunState()
    {
        bool running = Session.IsRunning;

        _startStopButton.Text = running ? "停止" : "启动";
        _runLabel.Text = running
            ? $"运行中（tick {Session.Host.Engine.TickInterval.TotalMilliseconds:F0} ms）"
            : "已停止";
    }

    private async void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            await SessionCommands.ToggleAsync(Session, this, SetStatus).ConfigureAwait(true);
            UpdateRunState();
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnReloadClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            SetStatus("正在重新加载…");

            ReloadResult result = await Session.ReloadAsync().ConfigureAwait(true);

            if (!result.Success)
            {
                MessageBox.Show(this, result.Failure, "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("重新加载失败，正在跑的这份没有受影响");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnSessionReloaded()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            BindSession();
            UpdateRunState();

            // 构成变了的话，这个窗口的类型可能已经不是最合适的——但换类型等于重建窗口，
            // 收益不值这个复杂度，提示用户自己关掉重开。
            DeviceComposition current = Session.Composition;
            if (current != _lastComposition)
            {
                _lastComposition = current;
                SetStatus("设备构成已变化，请关闭本窗口重新打开");
            }
            else
            {
                SetStatus($"已重新加载：{Session.DisplayName}");
            }
        });
    }

    /// <summary>把后台线程来的日志封送回 UI 线程。窗口还没句柄、或正在销毁时丢掉。</summary>
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
}
