using PlcSimulator.App.Shared;

namespace PlcSimulator.App.Srm;

/// <summary>
/// 堆垛机模拟器窗口：只有堆垛机自己的东西——面板与报文日志。
/// 输送机的点位监视、流程状态机、路径诊断、故障注入都不在这里，它们在
/// <c>PlcSimulator.App.Conveyor</c> 的窗口里。
/// </summary>
public sealed class SrmSimulatorForm : SimulatorWindowBase
{
    private readonly SrmStateView _stateView = new();

    public SrmSimulatorForm(SimulatorSession session)
        : base(session)
    {
        AddTab("堆垛机面板", _stateView);

        if (session.Composition == DeviceComposition.Empty)
        {
            ShowBanner("这份配置里没有启用的堆垛机：检查 devices[].enabled 与 protocol 是否为 \"Socket\"。");
        }
    }

    protected override string WindowKind => "堆垛机模拟器";

    protected override void BindSession()
    {
        _stateView.Bind(Session.Host);
        SelectTab("堆垛机面板");
    }

    protected override void RefreshSession() => _stateView.UpdateMachines();
}
