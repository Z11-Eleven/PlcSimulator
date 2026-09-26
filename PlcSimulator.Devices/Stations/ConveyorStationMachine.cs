using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Faults;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Topology;

namespace PlcSimulator.Devices.Stations;

/// <summary>
/// 输送线站台的状态机，模拟货物在站台之间的流转。
/// <para>
/// 完整闭环：WCS 写任务号 → <see cref="StationState.Executing"/>（本站动作）
/// → <see cref="StationState.Transferring"/>（货物在途，两站都无货）
/// → 送达下一站 → <see cref="StationState.Done"/>（置心跳）→ WCS 写清零值 → 空闲。
/// </para>
/// <para>
/// **逐站推进**：有拓扑时，货物按 <see cref="StationTopology"/> 一站在一站地向目标前进，
/// 每次只送到「下一站」。这样中间站台都会真实占用，下游堵塞会自然向上游传导。
/// 没有拓扑（站台未配坐标/方向）时退回按任务里的目标站台直接投送。
/// </para>
/// <para>
/// **中转站台不置心跳**：任务不是 WCS 下的，收发双方都不期待握手；只有收到 WCS
/// 写入任务号的站台才置心跳，与现场「PLC 执行完 WCS 的任务后回执」的分工一致。
/// </para>
/// <para>
/// 由 <see cref="SimulationEngine"/> 以固定 tick 单线程驱动。业务逻辑因此不触碰
/// 协议线程与 UI 线程，既不阻塞连接，也能脱离网络做确定性测试。
/// </para>
/// </summary>
public sealed class ConveyorStationMachine
{
    private readonly StationRuntime _station;
    private readonly SimulationConfig _simulation;
    private readonly Random _random;
    private readonly bool _usesAckHandshake;
    private readonly IFaultInjector _faultInjector;
    private readonly Func<string, ConveyorStationMachine?>? _stationLookup;
    private readonly StationTopology? _topology;

    private StationState _state;
    private TimeSpan _stateElapsed;
    private int _actionDelayMs;
    private int _clearWaitMs;
    private ushort _activeTaskNum;
    private long _tickCount;

    /// <summary>货物来自上游站台：本站只负责把它继续往下传，不置心跳。</summary>
    private bool _autoForward;

    /// <summary>
    /// 手动/自动。手动时本站冻结：上游的托盘进不来、站上的托盘也不动，用来人为制造堵塞。
    /// 初值取自配置的 <c>simulation.initial.auto</c>。
    /// </summary>
    private bool _manual;

    /// <summary>手动冻结的累计时长，推进判定时要扣掉，否则解冻瞬间就会跳完剩下的动作。</summary>
    private long _frozenMs;

    /// <summary>
    /// 本次任务结束后货物仍留在本站（任务目标即本站）。清零后站上还留着这票货，
    /// 标志保持为真——此时它表示「站上是别人送来的、等 WCS 处置的货」。
    /// </summary>
    private bool _cargoStays;

    // 在途货物的内容，进入 Transferring 时记录，送达时写入目标站台
    private ushort _cargoTaskNum;
    private ushort _cargoGoodsType;
    private string _cargoBarcode = string.Empty;
    private string _cargoFromStationNo = string.Empty;

    /// <summary>本次投送的**实际下一站**（有拓扑时是相邻站台，否则等于最终目标）。</summary>
    private string _cargoTargetStationNo = string.Empty;

    /// <summary>任务里的**最终目标站台**，随货一路往下传，供后续站台继续寻路。</summary>
    private string _cargoFinalTargetNo = string.Empty;

    public ConveyorStationMachine(
        StationRuntime station,
        int? randomSeed = null,
        IFaultInjector? faultInjector = null,
        Func<string, ConveyorStationMachine?>? stationLookup = null,
        StationTopology? topology = null)
    {
        _station = station;
        _simulation = station.Simulation;
        _random = randomSeed is int seed ? new Random(seed) : new Random();
        _faultInjector = faultInjector ?? PassThroughFaultInjector.Instance;
        _stationLookup = stationLookup;
        _topology = topology;

        string? handshakeField = StationInitializer.ResolveHandshakeFieldName(station.Device.ProtocolTemplate)
            ?? throw new InvalidOperationException(
                $"对接协议 {station.Device.ProtocolTemplate.Name} 既没有 heartbeat 也没有 ack 字段，无法建立握手。");

        _usesAckHandshake = handshakeField != "heartbeat";
        _manual = !_simulation.Initial.Auto;
        _state = _simulation.Initial.Loaded ? StationState.Loaded : StationState.Idle;
        _actionDelayMs = _simulation.ActionDelayMs;
        LastEvent = "初始化";
    }

    public event EventHandler<StationSnapshot>? StateChanged;

    /// <summary>等待 WCS 清零超过 clearTimeoutMs 时触发，用于验证 WCS 是否还在轮询。</summary>
    public event EventHandler<StationSnapshot>? ClearTimedOut;

    public StationRuntime Station => _station;

    public StationState State => _state;

    public string LastEvent { get; private set; }

    /// <summary>该站台是否正在作业（有任务在处理或货物在途）。</summary>
    public bool IsBusy => StationStateRules.IsBusy(_state);

    /// <summary>本站是否处于手动。</summary>
    public bool IsManual => _manual;

    /// <summary>
    /// 本站能否再接收上游送来的托盘：站上有货（含停在站上等 WCS 处置的）、正在作业、
    /// 或处于手动时都收不下。
    /// </summary>
    public bool CanAcceptCargo => StationStateRules.CanAccept(_state, _manual);

    /// <summary>扣掉手动冻结时长后的有效停留时间（毫秒），只有它才用于推进动作。</summary>
    private double EffectiveElapsedMs => _stateElapsed.TotalMilliseconds - _frozenMs;

    /// <summary>推进一帧。delta 由引擎按固定 tick 提供，便于确定性测试。</summary>
    public void Tick(TimeSpan delta)
    {
        _stateElapsed += delta;
        _tickCount++;

        if (_manual)
        {
            // 手动冻结机械动作。停留时间照涨（看得出卡了多久），但冻结时长要记下来，
            // 恢复自动后推进判定把它扣掉，否则解冻瞬间就会跳完剩余动作。
            _frozenMs += (long)delta.TotalMilliseconds;

            // 「待清零」不冻结：WCS 的清零写与心跳回执属于通信，手动挡的是机械动作。
            if (_state == StationState.Done)
            {
                WaitForWcsClear(delta);
            }

            return;
        }

        switch (_state)
        {
            case StationState.Idle:
            case StationState.Loaded:
                if (_autoForward)
                {
                    TryAutoForward();
                }
                else
                {
                    TryAcceptTask();
                }

                break;

            case StationState.Executing:
                if (EffectiveElapsedMs >= _actionDelayMs)
                {
                    FinishAction();
                }

                break;

            case StationState.WaitingDownstream:
                // 托盘留在本站等下一站空出来，能收了就发车。
                if (TargetCanAccept())
                {
                    DepartToTarget();
                }

                break;

            case StationState.Transferring:
                TryDeliver(delta);
                break;

            case StationState.Done:
                WaitForWcsClear(delta);
                break;

            case StationState.Fault:
            default:
                break;
        }
    }

    // ---- 手动注入（GUI 上的按钮） ----

    /// <summary>注入或撤销「站台有货」。</summary>
    public void SetLoaded(bool loaded)
    {
        if (_state is StationState.Executing
            or StationState.WaitingDownstream
            or StationState.Transferring
            or StationState.Done)
        {
            // 作业过程中不允许改变装载状态，避免与 WCS 的流程判断打架。
            return;
        }

        _autoForward = false;
        _cargoStays = false;
        Transition(loaded ? StationState.Loaded : StationState.Idle, loaded ? "手动注入：站台有货" : "手动注入：站台清空");
    }

    /// <summary>
    /// 切换手动/自动。手动只冻结动作推进，不改变状态本身：站上的托盘原地留着、
    /// 上游的托盘在线上等着，恢复自动后从冻结处继续（冻结时长不计入动作延时）。
    /// 状态字的 X9（AUTO 位）随之下翻转，WCS 能读到本站已被打为手动。
    /// </summary>
    public void SetManual(bool manual)
    {
        if (_manual == manual)
        {
            return;
        }

        _manual = manual;
        UpdateStatusBits();
        RaiseChanged(manual ? "打为手动：本站冻结，托盘不进也不动" : "恢复自动：本站继续运行");
    }

    public void InjectBarcode(string barcode)
    {
        _station.WriteOutgoingAscii("barcode", barcode);
        RaiseChanged("手动注入条码");
    }

    /// <summary>
    /// 把本站标成故障。引擎在 tick 里捕获到异常时调用：本站停止推进，
    /// 免得每帧都抛同一场异常把日志刷爆，也方便在界面上直接看出是哪一台出了问题。
    /// 手动「复位」可以把它恢复。
    /// </summary>
    public void MarkFaulted(string reason) => Transition(StationState.Fault, reason);

    /// <summary>把站台复位成配置初值（含手/自动，手动状态不会保留）。</summary>
    public void Reset()
    {
        StationInitializer.Apply(_station.Device, _station);
        _activeTaskNum = 0;
        _clearWaitMs = 0;
        _autoForward = false;
        _cargoStays = false;
        _manual = !_simulation.Initial.Auto;
        ClearCargo();
        Transition(_simulation.Initial.Loaded ? StationState.Loaded : StationState.Idle, "复位");
    }

    /// <summary>
    /// 接收上游送来的货物，整份任务信息（含目标站台）一并写入。
    /// 由上游站台的状态机在送达时调用；本站随后会自动把它继续往下传。
    /// 这里不检查本站是否空闲——是否送达由上游依据 <see cref="IsBusy"/> 判断。
    /// </summary>
    public void ReceiveCargo(
        ushort taskNum,
        string barcode,
        ushort goodsType,
        string fromStationNo,
        string toStationNo)
    {
        _station.AcceptCargo(taskNum, barcode, goodsType, fromStationNo, toStationNo);

        // 随货的这几项直接记在内存里。寄存器那份是按**读布局**写的，而回读走的是写布局——
        // CATL 两套偏移不同（to 在 @36 / @4），回读会把目标读成 0，货物就被当成"离场"丢掉了。
        _cargoTaskNum = taskNum;
        _cargoGoodsType = goodsType;
        _cargoBarcode = barcode;
        _cargoFinalTargetNo = toStationNo;

        // 这是中转货物，不是 WCS 下的任务：标记为自动转发，本站只负责往下传。
        // _activeTaskNum 也要跟上，否则界面上的任务号列在收到货时是空的。
        _activeTaskNum = taskNum;
        _autoForward = true;

        // 新货进站，站上不再有「等 WCS 处置的旧货」。
        _cargoStays = false;

        Transition(StationState.Loaded, $"收到来自 {fromStationNo} 的货物（任务号 {taskNum}，目标 {toStationNo}）");
    }

    public StationSnapshot Snapshot()
        => new(
            _station.Device.Config.Id,
            _station.StationNo,
            _station.Name,
            _state,
            LastEvent,
            _activeTaskNum,
            _station.ReadOutgoingAscii("barcode"),
            _state is StationState.Loaded
                or StationState.Executing
                or StationState.WaitingDownstream
                or StationState.Transferring
                or StationState.Done,
            !_manual,
            _state == StationState.Fault,
            _stateElapsed,
            _tickCount,
            _manual);

    private void TryAcceptTask()
    {
        ushort taskNum = _station.ReadIncomingU16("tasknum");

        if (taskNum == 0)
        {
            return;
        }

        // 任务号跟着托盘走：托盘离站时本站的字段就清了，所以 WCS 之后写任何非 0 值
        // ——包括刚执行过的那个号——都是新任务，不做「是否重复」的判断。
        //
        // 唯一的例外是站上停着等 WCS 处置的货：寄存器里那份是**随货带来的**信息
        // （属于这票货的属性），不是新任务。任务号与目标都没变就不再触发，
        // 否则托盘一停稳就会被自己带来的号反复当成新任务执行、反复置心跳。
        if (_cargoStays
            && taskNum == _cargoTaskNum
            && _cargoFinalTargetNo == _station.ReadIncomingU16("to").ToString())
        {
            return;
        }

        _activeTaskNum = taskNum;
        _actionDelayMs = Math.Max(0, _simulation.ActionDelayMs + NextJitter());
        _clearWaitMs = 0;
        _cargoStays = false;

        Transition(StationState.Executing, $"收到任务号 {taskNum}，动作中（{_actionDelayMs} ms）");
    }

    /// <summary>
    /// 中转货物：沿拓扑把它继续往目标方向送。已经到达目标站台时不再移动，货物留在这里。
    /// </summary>
    private void TryAutoForward()
    {
        string destination = ResolveDestination(_cargoFinalTargetNo);

        if (destination == _station.StationNo)
        {
            _autoForward = false;
            _cargoStays = true;
            RaiseChanged($"货物已到达目标站台 {destination}，留在此处等待 WCS");
            return;
        }

        _activeTaskNum = _station.ReadIncomingU16("tasknum");
        _actionDelayMs = Math.Max(0, _simulation.ActionDelayMs + NextJitter());
        _cargoStays = false;

        Transition(StationState.Executing, $"中转货物，继续送往站台 {destination}（动作中）");
    }

    /// <summary>
    /// 本站动作结束。有明确目标站台时货物进入在途，等送达后完成；
    /// 没有目标（或目标不在仿真范围内）时货物直接离场，本站立即完成。
    /// </summary>
    private void FinishAction()
    {
        // 中转货物的这几项在收货时就记进内存了，不能回读寄存器：寄存器那份按**读布局**写、
        // 回读走**写布局**，CATL 下两套偏移不同会把目标读成 0。只有 WCS 下的任务才以寄存器为准。
        if (!_autoForward)
        {
            _cargoTaskNum = _activeTaskNum;
            _cargoGoodsType = _station.ReadIncomingU16("goodstype");
            _cargoBarcode = _station.ReadOutgoingAscii("barcode");
            _cargoFinalTargetNo = _station.ReadIncomingU16("to").ToString();
        }

        // 「来源站台」记的是把货交给本站的那一站（上一跳），逐站推进时一路更新
        _cargoFromStationNo = _station.StationNo;

        _cargoTargetStationNo = ResolveDestination(_cargoFinalTargetNo);

        // 目标就是本站：货物已经在该在的位置，不搬运也不清任务信息
        if (_cargoTargetStationNo == _station.StationNo)
        {
            _cargoStays = true;
            CompleteWithoutDelivery($"目标即本站 {_cargoTargetStationNo}，货物留在本站");
            return;
        }

        if (!HasTarget())
        {
            _station.ClearTaskFields();
            CompleteWithoutDelivery($"目标站台 {_cargoTargetStationNo} 不在仿真范围，货物离场");
            return;
        }

        // 下一站收不下就**不发车**：托盘留在本站等，任务信息原样不动。
        // 推到一半悬在路上、两站都查不到货，既不符合现场也会让 WCS 以为货丢了。
        if (!TargetCanAccept())
        {
            _cargoStays = false;
            Transition(StationState.WaitingDownstream, BlockedReason());
            return;
        }

        DepartToTarget();
    }

    /// <summary>
    /// 下游站台当前能否接收托盘。
    /// <para>
    /// 提升机另有规矩：**托盘不能在提升机里停着**。所以发给提升机之前还要多看一站——
    /// 提升机自己的下一站要是收不下，托盘就留在本站等，别送进提升机卡着。
    /// </para>
    /// </summary>
    private bool TargetCanAccept()
    {
        if (_stationLookup?.Invoke(_cargoTargetStationNo) is not ConveyorStationMachine target
            || !target.CanAcceptCargo)
        {
            return false;
        }

        return target.CanPassThrough(_cargoFinalTargetNo);
    }

    /// <summary>
    /// 托盘只是路过本站时，本站的下一站收不收得下。
    /// 普通站台允许托盘停在自己这里，恒为 true；提升机不允许停盘，所以要往下再看一站。
    /// </summary>
    private bool CanPassThrough(string finalTarget)
    {
        if (!IsLifter)
        {
            return true;
        }

        string next = ResolveDestination(finalTarget);

        // 终点就是本站，没别的地方可去，只能停在盘上
        if (next == _station.StationNo)
        {
            return true;
        }

        return _stationLookup?.Invoke(next)?.CanAcceptCargo ?? true;
    }

    /// <summary>本台是不是提升机——现场约定 remark 写「提升机」，拓扑也按它归组。</summary>
    private bool IsLifter
        => string.Equals(_station.Config.Remark, "提升机", StringComparison.Ordinal);

    private string BlockedReason()
    {
        ConveyorStationMachine? target = _stationLookup?.Invoke(_cargoTargetStationNo);

        if (target?.IsManual == true)
        {
            return $"下游站台 {_cargoTargetStationNo} 处于手动，托盘留在本站等待";
        }

        string state = target is null ? "不可用" : target.Snapshot().StateText;
        return $"下游站台 {_cargoTargetStationNo} 收不下（{state}），托盘留在本站等待";
    }

    /// <summary>
    /// 托盘真正离站：整份任务信息（含目标站台）一起清掉，本站置为无货。
    /// 货物内容早已读进 <c>_cargo*</c> 字段，随货一路传到下一站。
    /// </summary>
    private void DepartToTarget()
    {
        _station.ClearTaskFields();
        Transition(StationState.Transferring, $"货物已离开本站，前往站台 {_cargoTargetStationNo}（在途）");
    }

    /// <summary>
    /// 解析本次投送的目标：有拓扑时取「往任务目标方向的下一站」，实现逐站推进；
    /// 没有拓扑或找不到路径时退回任务里的目标站台，一步直达。
    /// </summary>
    private string ResolveDestination(string finalTarget)
    {
        if (_topology is not null
            && _topology.TryGetNextHop(_station.StationNo, finalTarget, out string nextHop))
        {
            return nextHop;
        }

        return finalTarget;
    }

    /// <summary>
    /// 在途推进：计满在途时长后尝试送达；目标站台忙时保持等待（堵塞）。
    /// </summary>
    private void TryDeliver(TimeSpan delta)
    {
        ConveyorStationMachine target = _stationLookup!(_cargoTargetStationNo)!;
        int transferDelayMs = Math.Max(0, _simulation.TransferDelayMs);

        if (EffectiveElapsedMs < transferDelayMs)
        {
            return;
        }

        if (!target.CanAcceptCargo)
        {
            // 堵塞：目标站台上有货 / 正在作业 / 被打了手动，货物在线上等待，不丢失也不覆盖。
            string reason = target.IsManual
                ? $"目标站台 {_cargoTargetStationNo} 处于手动，货物在线上等待（堵塞）"
                : $"目标站台 {_cargoTargetStationNo} 收不下，货物在线上等待（堵塞）";

            if (!string.Equals(LastEvent, reason, StringComparison.Ordinal))
            {
                RaiseChanged(reason);
            }

            return;
        }

        // 把「最终目标」继续往下传，而不是本次的下一站——否则收方会以为货已到终点。
        target.ReceiveCargo(
            _cargoTaskNum, _cargoBarcode, _cargoGoodsType, _cargoFromStationNo, _cargoFinalTargetNo);

        CompleteWithoutDelivery($"货物已送达站台 {_cargoTargetStationNo}");
    }

    /// <summary>完成本次投送：置心跳并进入待清零；中转货物不置心跳，直接回到空闲。</summary>
    private void CompleteWithoutDelivery(string reason)
    {
        _activeTaskNum = 0;

        bool relayed = _autoForward;
        _autoForward = false;
        ClearCargo();

        // 中转站台不置心跳：任务不是 WCS 下的，收发双方都不期待握手。
        if (relayed)
        {
            Transition(StationState.Idle, $"{reason}（中转，无需握手）");
            return;
        }

        if (_faultInjector.SuppressHeartbeat(_station.Device.Config.Id, _station.StationNo))
        {
            Transition(StationState.Done, $"{reason}；心跳被故障注入抑制");
            return;
        }

        WriteHandshake((ushort)_simulation.Heartbeat.SetValue);
        Transition(StationState.Done, $"{reason}；心跳已置 {(ushort)_simulation.Heartbeat.SetValue}，等待 WCS 清零");
    }

    private bool HasTarget()
        => _stationLookup is not null
            && !string.IsNullOrWhiteSpace(_cargoTargetStationNo)
            && _cargoTargetStationNo != "0"
            && _cargoTargetStationNo != _station.StationNo
            && _stationLookup(_cargoTargetStationNo) is not null;

    private void ClearCargo()
    {
        _cargoGoodsType = 0;
        _cargoBarcode = string.Empty;
        _cargoFromStationNo = string.Empty;
        _cargoTargetStationNo = string.Empty;

        if (_cargoStays)
        {
            // 货还在站上，任务号与目标是**这票货的标识**，留着：
            // 下一轮要靠它们区分「随货信息」与「WCS 新下的任务」。
            return;
        }

        _cargoTaskNum = 0;
        _cargoFinalTargetNo = string.Empty;
    }

    private void WaitForWcsClear(TimeSpan delta)
    {
        ushort heartbeat = ReadHandshake();

        if (heartbeat == (ushort)_simulation.Heartbeat.ClearOnValue)
        {
            WriteHandshake(0);
            _clearWaitMs = 0;

            // 货物留在本站时回到「有货待命」，否则回到空闲。
            // 「有货待命」下 _cargoStays 保持为真：它此时表示「站上是别人送来的、
            // 等 WCS 处置的货」，下一轮靠它区分随货信息与 WCS 新下的任务。
            if (_cargoStays)
            {
                Transition(StationState.Loaded, "WCS 已清零，货物留在本站");
            }
            else
            {
                Transition(StationState.Idle, "WCS 已清零，站台复位");
            }

            return;
        }

        _clearWaitMs += (int)delta.TotalMilliseconds;
        if (_simulation.ClearTimeoutMs > 0 && _clearWaitMs >= _simulation.ClearTimeoutMs)
        {
            _clearWaitMs = 0;
            RaiseChanged("等待 WCS 清零超时");
            ClearTimedOut?.Invoke(this, Snapshot());
        }
    }

    /// <summary>
    /// 写握手字段。NTI 用 16 位心跳寄存器；CATL 用回执字节 ack，
    /// 其语义不同（置 1 表示「已收到命令」，随后 WCS 会把整块回写成 0）。
    /// </summary>
    private void WriteHandshake(ushort value)
    {
        if (_usesAckHandshake)
        {
            _station.WriteOutgoingU8("ack", (byte)value);
            return;
        }

        _station.WriteOutgoingU16("heartbeat", value);
    }

    private ushort ReadHandshake()
        => _usesAckHandshake ? _station.ReadIncomingU8("ack") : _station.ReadIncomingU16("heartbeat");

    private int NextJitter()
        => _simulation.JitterMs <= 0 ? 0 : _random.Next(-_simulation.JitterMs, _simulation.JitterMs + 1);

    private void Transition(StationState next, string reason)
    {
        _state = next;
        _stateElapsed = TimeSpan.Zero;
        _frozenMs = 0;
        UpdateStatusBits();
        RaiseChanged(reason);
    }

    /// <summary>
    /// 同步状态字。位定义见现场协议：X8 StaLoad（0 有货 / 1 无货）、
    /// X9 AUTO（1 自动 / 0 手动）、X10 Fault（1 有故障）。
    /// X11-X14 的动作方向本模拟器暂不产生，始终为 0。
    /// </summary>
    private void UpdateStatusBits()
    {
        // 只有本站仍持有货物时才算「有货」：在途与待清零都表示货物已经离开了本站。
        bool loaded = _state is StationState.Loaded or StationState.Executing or StationState.WaitingDownstream;

        // 故障可由状态机进入故障态产生，也可由配置直接指定（模拟现场设备故障）。
        bool fault = _state == StationState.Fault || _simulation.Initial.Fault;

        // AUTO 位与手动开关联动：打手动的站台 X9 归 0，WCS 据此能看出本站已脱离自动。
        bool auto = !_manual;

        if (_station.Device.ProtocolTemplate.Name.Equals(NtiProtocol.ProtocolName, StringComparison.OrdinalIgnoreCase))
        {
            int status = 0;

            if (!loaded)
            {
                status |= 1 << 8;
            }

            if (auto)
            {
                status |= 1 << 9;
            }

            if (fault)
            {
                status |= 1 << 10;
            }

            _station.WriteOutgoingU16("status", (ushort)status);
            return;
        }

        if (_station.Device.ProtocolTemplate.Name.Equals(CatlProtocol.ProtocolName, StringComparison.OrdinalIgnoreCase))
        {
            _station.WriteOutgoingU8("autoManual", (byte)(auto ? 2 : 1));
            _station.WriteOutgoingU8("status", (byte)(loaded ? 0 : 1));
            _station.WriteOutgoingU8("fault", (byte)(fault ? 0 : 1));
        }
    }

    private void RaiseChanged(string reason)
    {
        LastEvent = reason;
        StateChanged?.Invoke(this, Snapshot());
    }
}
