using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Faults;

namespace PlcSimulator.Core.Tests;

public class ConfiguredFaultInjectorTests
{
    private static FaultRequestContext Context(
        byte functionCode = 3,
        ConnectionRole role = ConnectionRole.Read,
        ushort address = 0,
        string deviceId = "dev")
        => new(deviceId, "#1", "127.0.0.1", role, functionCode, address, 10);

    private static ConfiguredFaultInjector Injector(params FaultRuleConfig[] rules)
        => new(new FaultInjectionConfig { Enabled = true, Rules = [.. rules] });

    [Fact]
    public void Decide_MatchingFunctionCode_ReturnsConfiguredException()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "非法地址",
            Effect = "ExceptionResponse",
            ExceptionCode = 2,
            Match = new FaultMatchConfig { FunctionCodes = [3] },
        });

        FaultDecision decision = injector.Decide(Context(functionCode: 3));

        Assert.Equal(FaultAction.ExceptionResponse, decision.Action);
        Assert.Equal(2, decision.ExceptionCode);
        Assert.Equal("非法地址", decision.RuleName);
    }

    [Fact]
    public void Decide_NonMatchingFunctionCode_Passes()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "只影响读",
            Effect = "SilentDrop",
            Match = new FaultMatchConfig { FunctionCodes = [3] },
        });

        Assert.True(injector.Decide(Context(functionCode: 16)).IsPass);
    }

    [Fact]
    public void Decide_MatchingRoleOnly_AffectsThatRole()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "只让写失败",
            Effect = "CloseConnection",
            Match = new FaultMatchConfig { Role = "W" },
        });

        Assert.True(injector.Decide(Context(role: ConnectionRole.Read)).IsPass);
        Assert.Equal(FaultAction.CloseConnection, injector.Decide(Context(role: ConnectionRole.Write)).Action);
    }

    [Fact]
    public void Decide_AddressRange_LimitsScope()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "只影响前 32 个寄存器",
            Effect = "DelayThenPass",
            DelayMs = 500,
            Match = new FaultMatchConfig { AddressFrom = 0, AddressTo = 31 },
        });

        Assert.Equal(FaultAction.DelayThenPass, injector.Decide(Context(address: 10)).Action);
        Assert.True(injector.Decide(Context(address: 100)).IsPass);
    }

    [Fact]
    public void Decide_AfterMaxTimes_StopsInjecting()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "只命中两次",
            Effect = "SilentDrop",
            MaxTimes = 2,
        });

        Assert.False(injector.Decide(Context()).IsPass);
        Assert.False(injector.Decide(Context()).IsPass);
        Assert.True(injector.Decide(Context()).IsPass);
    }

    [Fact]
    public void Decide_ZeroProbability_NeverInjects()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "概率为零",
            Effect = "SilentDrop",
            Probability = 0,
        });

        for (int i = 0; i < 20; i++)
        {
            Assert.True(injector.Decide(Context()).IsPass);
        }
    }

    [Fact]
    public void SetRuleEnabledAt_False_StopsMatchingAndRestoresByDefault()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "可切换规则",
            Effect = "SilentDrop",
        });

        Assert.False(injector.Decide(Context()).IsPass);

        Assert.True(injector.SetRuleEnabledAt(0, false));
        Assert.True(injector.Decide(Context()).IsPass);

        Assert.True(injector.SetRuleEnabledAt(0, true));
        Assert.False(injector.Decide(Context()).IsPass);
    }

    [Fact]
    public void SetRuleEnabledAt_OutOfRange_ReturnsFalse()
        => Assert.False(Injector().SetRuleEnabledAt(99, true));

    [Fact]
    public void SuppressHeartbeat_WithStationScope_OnlyAffectsThatStation()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "站台 1005 心跳停止",
            Effect = "HeartbeatStop",
            StationNo = "1005",
        });

        Assert.True(injector.SuppressHeartbeat("dev", "1005"));
        Assert.False(injector.SuppressHeartbeat("dev", "1004"));
    }

    [Fact]
    public void SuppressHeartbeat_WithoutStationScope_AffectsAll()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "全部心跳停止",
            Effect = "HeartbeatStop",
        });

        Assert.True(injector.SuppressHeartbeat("dev", "1004"));
        Assert.True(injector.SuppressHeartbeat("dev", "9999"));
    }

    [Fact]
    public void HeartbeatStopRule_DoesNotAffectFrameLevelRequests()
    {
        // HeartbeatStop 是语义级故障，不应该拦住任何报文。
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "心跳停止",
            Effect = "HeartbeatStop",
        });

        Assert.True(injector.Decide(Context()).IsPass);
    }

    [Fact]
    public void PassThroughInjector_IsDisabledAndAllocatesNothing()
    {
        IFaultInjector injector = PassThroughFaultInjector.Instance;

        Assert.False(injector.Enabled);
        Assert.Empty(injector.Rules);
        Assert.False(injector.SuppressHeartbeat("dev", "1004"));
        Assert.True(injector.Decide(Context()).IsPass);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            injector.Decide(Context());
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Rules_ExposeStatusForGui()
    {
        ConfiguredFaultInjector injector = Injector(new FaultRuleConfig
        {
            Name = "可见规则",
            Effect = "ExceptionResponse",
            ExceptionCode = 6,
            MaxTimes = 1,
            Match = new FaultMatchConfig { FunctionCodes = [3], AddressFrom = 0 },
        });

        injector.Decide(Context(functionCode: 3));

        FaultRuleStatus status = Assert.Single(injector.Rules);
        Assert.Equal("可见规则", status.Name);
        Assert.Equal("ExceptionResponse", status.Effect);
        Assert.Equal(1, status.HitCount);
        Assert.True(status.RuntimeEnabled);
        Assert.Contains("功能码", status.MatchSummary, StringComparison.Ordinal);
    }
}
