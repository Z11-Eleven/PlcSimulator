namespace PlcSimulator.Core.Configuration;

public sealed class FaultInjectionConfig
{
    public bool Enabled { get; set; }

    public List<FaultRuleConfig> Rules { get; set; } = [];
}

public sealed class FaultRuleConfig
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public FaultMatchConfig Match { get; set; } = new();

    /// <summary>
    /// Pass / ExceptionResponse / SilentDrop / DelayThenPass / DelayThenException / CloseConnection，
    /// 另有语义级的 HeartbeatStop。
    /// </summary>
    public string Effect { get; set; } = "ExceptionResponse";

    /// <summary>延时类效果使用的毫秒数。</summary>
    public int DelayMs { get; set; } = 3000;

    /// <summary>异常响应使用的异常码，默认 0x04（从站设备故障）。</summary>
    public int ExceptionCode { get; set; } = 4;

    /// <summary>命中概率（百分比），默认 100。</summary>
    public int Probability { get; set; } = 100;

    /// <summary>最多命中次数，0 表示不限。建议至少设置本项或 durationSec 之一，避免忘记关闭。</summary>
    public int MaxTimes { get; set; }

    /// <summary>HeartbeatStop 效果作用的站台号，留空表示所有站台。</summary>
    public string? StationNo { get; set; }
}

public sealed class FaultMatchConfig
{
    public string? DeviceId { get; set; }

    public string? RemoteAddress { get; set; }

    /// <summary>"R" 或 "W"，留空表示不限。</summary>
    public string? Role { get; set; }

    /// <summary>功能码白名单（十进制），留空表示不限。</summary>
    public List<int> FunctionCodes { get; set; } = [];

    public int? AddressFrom { get; set; }

    public int? AddressTo { get; set; }
}
