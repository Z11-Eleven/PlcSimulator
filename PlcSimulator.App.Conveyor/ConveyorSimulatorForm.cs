using PlcSimulator.App.Shared;

namespace PlcSimulator.App.Conveyor;

/// <summary>
/// 输送机模拟器窗口：只有输送线自己的东西——点位监视、流程状态机、路径诊断、故障注入。
/// 堆垛机面板与它的报文日志在 <c>PlcSimulator.App.Srm</c> 的窗口里。
/// </summary>
public sealed class ConveyorSimulatorForm : SimulatorWindowBase
{
    private readonly PointMonitorView _pointView = new();
    private readonly StationStateView _stateView = new();
    private readonly PathDiagnosticView _pathView = new();
    private readonly FaultInjectionView _faultView = new();

    public ConveyorSimulatorForm(SimulatorSession session)
        : base(session)
    {
        AddTab("点位监视", _pointView);
        AddTab("流程状态机", _stateView);
        AddTab("路径诊断", _pathView);
        AddTab("故障注入", _faultView);

        ShowCompositionBannerIfNeeded(session);
    }

    protected override string WindowKind => "输送机模拟器";

    protected override void BindSession()
    {
        _pointView.Bind(Session.Host);
        _pointView.Reset();
        _stateView.Bind(Session.Host);
        _pathView.Bind(Session.Host);
        _faultView.Bind(Session.Host.FaultInjector);

        SelectTab("点位监视");
    }

    protected override void RefreshSession()
    {
        _pointView.RefreshDirty();
        _stateView.Update(Session.Host.Engine.Snapshots());
        _faultView.RefreshHits();
    }

    /// <summary>
    /// 两类设备挤在一份配置里时给个明示：本窗口只显示输送线，堆垛机那半边会照常起服务，
    /// 但没有可操作的界面、报文还会混进本窗口的日志。用常驻横幅而不是弹框——每次打开都弹会烦。
    /// </summary>
    private void ShowCompositionBannerIfNeeded(SimulatorSession session)
    {
        switch (session.Composition)
        {
            case DeviceComposition.Both:
                ShowBanner(
                    "这份配置里既有站台又有堆垛机。本窗口只显示输送线，堆垛机没有可操作的界面，"
                    + "它的报文也会出现在本窗口的日志里——建议把两类设备拆成两份配置。");
                break;

            case DeviceComposition.Empty:
                ShowBanner("这份配置里没有启用的设备：检查 devices[].enabled 与 protocol。");
                break;

            default:
                break;
        }
    }
}
