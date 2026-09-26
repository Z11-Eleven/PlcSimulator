using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Frames;
using PlcSimulator.Hosting;

namespace PlcSimulator.App;

/// <summary>
/// 一份已加载的配置及其运行时。
/// <para>
/// 每份配置对应一个自包含的 <see cref="SimulatorHost"/>：各自的数据区、各自的协议服务端、
/// 各自的端口，互不影响。调试台同时挂多份，就能一边模拟输送线、一边模拟堆垛机，
/// 而不是加载一份就把另一份顶掉。
/// </para>
/// </summary>
internal sealed class LoadedConfiguration
{
    public LoadedConfiguration(string path, ConfigLoadResult result, IFrameLog frameLog)
    {
        Path = path;
        Result = result;
        Host = SimulatorHost.Create(result.Config, frameLog);
    }

    public string Path { get; }

    public ConfigLoadResult Result { get; }

    public SimulatorHost Host { get; }

    public bool IsRunning => Host.IsRunning;

    /// <summary>
    /// 树上显示的名字：只取文件名。设备构成与完整路径放进 <see cref="ToolTip"/>——
    /// 树那一列不宽，把构成塞进标题会被截成「simulator.jso…」。
    /// </summary>
    public string DisplayName => System.IO.Path.GetFileName(Path);

    /// <summary>带运行标记的显示名，树节点用。</summary>
    public string DisplayNameWithState => IsRunning ? $"{DisplayName}  ● 运行中" : DisplayName;

    /// <summary>悬停提示：完整路径 + 这份配置里有几台什么设备。</summary>
    public string ToolTip => $"{System.IO.Path.GetFullPath(Path)}\n{DeviceSummary}";

    /// <summary>这份配置里有几台什么设备。</summary>
    public string DeviceSummary
    {
        get
        {
            int srm = Result.Config.Devices.Count(
                static d => string.Equals(d.Protocol, "Socket", StringComparison.OrdinalIgnoreCase));
            int conveyor = Result.Config.Devices.Count - srm;

            return (conveyor, srm) switch
            {
                (0, 0) => "无设备",
                (0, _) => $"堆垛机 {srm} 台",
                (_, 0) => $"输送线 {conveyor} 台",
                _ => $"输送线 {conveyor} 台 + 堆垛机 {srm} 台",
            };
        }
    }

    public override string ToString() => DisplayName;
}
