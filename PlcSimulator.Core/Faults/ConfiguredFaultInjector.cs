using PlcSimulator.Core.Configuration;

namespace PlcSimulator.Core.Faults;

/// <summary>
/// 由配置驱动的故障注入。规则在构造时预编译成委托链，
/// 每个请求只做几次比较，不做字符串解析或 LINQ 查询。
/// <para>
/// 故障只作用于请求/响应路径，**从不写入寄存器数据区**，
/// 因此开着故障也不会把现场数据改坏。
/// </para>
/// </summary>
public sealed class ConfiguredFaultInjector : IFaultInjector
{
    private readonly CompiledRule[] _rules;

    /// <summary>
    /// 被抑制心跳的站台，键见 <see cref="SuppressionKey"/>。
    /// 不能只用站台号：多条产线可以有同号站台，那样会把它们一起抑制掉。
    /// </summary>
    private readonly HashSet<string> _suppressedStations = new(StringComparer.Ordinal);

    /// <summary>整个设备的站台都抑制心跳（规则限定了设备但没限定站台）。</summary>
    private readonly HashSet<string> _suppressedDevices = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _suppressAllHeartbeats;

    public ConfiguredFaultInjector(FaultInjectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _rules = [.. config.Rules.Where(static r => r.Enabled).Select(static r => new CompiledRule(r))];

        foreach (FaultRuleConfig rule in config.Rules)
        {
            if (!rule.Enabled || !IsHeartbeatStopEffect(rule.Effect))
            {
                continue;
            }

            string? deviceId = rule.Match.DeviceId;

            if (string.IsNullOrWhiteSpace(rule.StationNo))
            {
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    _suppressAllHeartbeats = true;
                }
                else
                {
                    _suppressedDevices.Add(deviceId);
                }
            }
            else
            {
                _suppressedStations.Add(SuppressionKey(deviceId, rule.StationNo));
            }
        }
    }

    public bool Enabled => true;

    public IReadOnlyList<FaultRuleStatus> Rules => [.. _rules.Select(static r => r.Status())];

    public FaultDecision Decide(in FaultRequestContext context)
    {
        foreach (CompiledRule rule in _rules)
        {
            if (rule.TryMatch(in context, out FaultDecision decision))
            {
                return decision;
            }
        }

        return FaultDecision.Pass;
    }

    public bool SuppressHeartbeat(string deviceId, string stationNo)
        => _suppressAllHeartbeats
            || _suppressedDevices.Contains(deviceId)
            || _suppressedStations.Contains(SuppressionKey(null, stationNo))
            || _suppressedStations.Contains(SuppressionKey(deviceId, stationNo));

    /// <summary>抑制键：设备 id 为空表示这条规则不限设备（所有产线的该站台号都抑制）。</summary>
    private static string SuppressionKey(string? deviceId, string stationNo)
        => $"{deviceId}\0{stationNo}";

    /// <summary>
    /// 按序号切换规则。不能按名字切：规则名可以重复、也可以为空，
    /// 按名字只会命中第一条，界面上勾第二条结果动的是第一条。
    /// </summary>
    public bool SetRuleEnabledAt(int index, bool enabled)
    {
        if (index < 0 || index >= _rules.Length)
        {
            return false;
        }

        _rules[index].RuntimeEnabled = enabled;
        return true;
    }

    private static bool IsHeartbeatStopEffect(string effect)
        => effect.Equals("HeartbeatStop", StringComparison.OrdinalIgnoreCase);

    private static FaultAction ParseEffect(string effect)
        => Enum.TryParse(effect, ignoreCase: true, out FaultAction action)
            ? action
            : FaultAction.ExceptionResponse;

    private static (Func<FaultRequestContext, bool> Matcher, string Summary) BuildMatcher(FaultMatchConfig match)
    {
        List<Func<FaultRequestContext, bool>> predicates = [];
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(match.DeviceId))
        {
            string deviceId = match.DeviceId;
            predicates.Add(c => string.Equals(c.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            parts.Add($"设备={deviceId}");
        }

        if (!string.IsNullOrWhiteSpace(match.RemoteAddress))
        {
            string address = match.RemoteAddress;
            predicates.Add(c => string.Equals(c.RemoteAddress, address, StringComparison.Ordinal));
            parts.Add($"来源={address}");
        }

        if (!string.IsNullOrWhiteSpace(match.Role))
        {
            ConnectionRole role = match.Role.Equals("W", StringComparison.OrdinalIgnoreCase)
                ? ConnectionRole.Write
                : ConnectionRole.Read;

            predicates.Add(c => c.Role == role);
            parts.Add($"角色={(role == ConnectionRole.Write ? "写" : "读")}");
        }

        if (match.FunctionCodes.Count > 0)
        {
            HashSet<byte> codes = [.. match.FunctionCodes.Select(static v => (byte)v)];
            predicates.Add(c => codes.Contains(c.FunctionCode));
            parts.Add($"功能码={string.Join("/", codes.Select(static c => $"0x{c:X2}"))}");
        }

        if (match.AddressFrom is int from)
        {
            predicates.Add(c => c.Address >= from);
            parts.Add($"地址≥{from}");
        }

        if (match.AddressTo is int to)
        {
            predicates.Add(c => c.Address <= to);
            parts.Add($"地址≤{to}");
        }

        if (predicates.Count == 0)
        {
            return (static _ => true, "全部请求");
        }

        return (
            context =>
            {
                foreach (Func<FaultRequestContext, bool> predicate in predicates)
                {
                    if (!predicate(context))
                    {
                        return false;
                    }
                }

                return true;
            },
            string.Join("，", parts));
    }

    private sealed class CompiledRule
    {
        private readonly FaultAction _action;
        private readonly int _delayMs;
        private readonly byte _exceptionCode;
        private readonly int _probability;
        private readonly int _maxTimes;
        private readonly Func<FaultRequestContext, bool> _match;
        private readonly string _matchSummary;
        private readonly string _effectText;
        private int _hitCount;

        public CompiledRule(FaultRuleConfig config)
        {
            Name = string.IsNullOrWhiteSpace(config.Name) ? "(未命名规则)" : config.Name;
            _effectText = config.Effect;

            // HeartbeatStop 是语义级故障，不参与帧级匹配。
            _action = IsHeartbeatStopEffect(config.Effect) ? FaultAction.Pass : ParseEffect(config.Effect);
            _delayMs = Math.Max(0, config.DelayMs);
            _exceptionCode = (byte)Math.Clamp(config.ExceptionCode, 1, 255);
            _probability = Math.Clamp(config.Probability, 0, 100);
            _maxTimes = Math.Max(0, config.MaxTimes);

            (_match, _matchSummary) = BuildMatcher(config.Match);
        }

        public string Name { get; }

        public bool RuntimeEnabled { get; set; } = true;

        public bool TryMatch(in FaultRequestContext context, out FaultDecision decision)
        {
            decision = FaultDecision.Pass;

            if (!RuntimeEnabled || _action == FaultAction.Pass || !_match(context))
            {
                return false;
            }

            if (_maxTimes > 0 && Volatile.Read(ref _hitCount) >= _maxTimes)
            {
                return false;
            }

            if (_probability < 100 && Random.Shared.Next(100) >= _probability)
            {
                return false;
            }

            if (_maxTimes > 0)
            {
                Interlocked.Increment(ref _hitCount);
            }

            decision = new FaultDecision(_action, _delayMs, _exceptionCode, Name);
            return true;
        }

        public FaultRuleStatus Status()
            => new(Name, _effectText, _matchSummary, Volatile.Read(ref _hitCount), RuntimeEnabled);
    }
}
