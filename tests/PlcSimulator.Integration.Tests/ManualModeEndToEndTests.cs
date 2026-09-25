using System.Diagnostics;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Devices;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Hosting;

namespace PlcSimulator.Integration.Tests;

/// <summary>
/// 手动模式的端到端验证，站在 WCS 的位置（真实 TCP）看现象：
/// 下游站台被打手动后，上游送出的托盘一直卡在线上，任务挂住不完成；
/// 手动站台的状态字 X9 同步置 0，WCS 读得到；恢复自动后货照常送达。
/// </summary>
public sealed class ManualModeEndToEndTests : IAsyncLifetime
{
    private const int StationBytes = TestConfigFactory.StationLengthBytes;
    private const int BlockBytes = StationBytes * 2;
    private const int ActionDelayMs = 200;
    private const int SecondStationTaskRegister = StationBytes / 2;

    private SimulatorHost _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = TestPorts.Next();
        SimulatorConfig config = TestConfigFactory.BuildConveyor(
            _port, ActionDelayMs, jitterMs: 0, "1001", "1002");

        _host = SimulatorHost.Create(config);
        await _host.StartAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private StationRuntime Station(int index) => _host.Devices[0].Stations[index];

    /// <summary>任务号按 WCS 的读法从寄存器里取，而不是看内存里的快照。</summary>
    private ushort TaskNumAt(int index) => Station(index).ReadIncomingU16("tasknum");

    [Fact]
    public void ManualDownstream_UpstreamTaskStaysUnfinished_UntilRestored()
    {
        ConveyorStationMachine source = _host.Engine.Find("1001")!;
        ConveyorStationMachine target = _host.Engine.Find("1002")!;

        target.SetManual(true);

        using MiniWcsClient client = new("127.0.0.1", _port);

        client.WriteSingleRegister(0, 1001);      // tasknum @ 寄存器 0
        client.WriteSingleRegister(3, 1002);      // to      @ 寄存器 3

        // 下一站手动收不下：托盘留在 1001 原地等，任务信息原样不动
        WaitUntil(() => source.State == StationState.WaitingDownstream, "源站台未进入「等下站」");
        Thread.Sleep(600);

        Assert.Equal(StationState.WaitingDownstream, source.State);
        Assert.Equal(1001, TaskNumAt(0));       // 任务号仍在源站台
        Assert.True(HasCargoAt(client, 0));     // 托盘也还在 1001

        // 下游一个字节都没收到
        Assert.Equal(0, TaskNumAt(1));
        Assert.False(HasCargoAt(client, 1));

        // 手动站台的状态字 X9 = 0，WCS 轮询就能看出它已被打为手动
        byte[] block = client.ReadBytes(0, BlockBytes);
        int targetStatus = WcsConveyorCodec.ReadU16(block, StationBytes + 28);
        Assert.Equal(0, (targetStatus >> 9) & 1);

        // 恢复自动：堵住的货立刻送达
        target.SetManual(false);
        WaitUntil(() => TaskNumAt(1) == 1001, "恢复自动后货未送达");
    }

    [Fact]
    public void ManualStation_IgnoresWcsTask_UntilRestored()
    {
        ConveyorStationMachine machine = _host.Engine.Find("1002")!;
        machine.SetManual(true);

        using MiniWcsClient client = new("127.0.0.1", _port);
        client.WriteSingleRegister(SecondStationTaskRegister, 2001);

        Thread.Sleep(600);
        Assert.Equal(StationState.Idle, machine.State);

        machine.SetManual(false);
        WaitUntil(() => machine.State == StationState.Executing, "恢复自动后未接单");
    }

    /// <summary>读某个站台的状态字 X8：true 表示有货。</summary>
    private bool HasCargoAt(MiniWcsClient client, int stationIndex)
    {
        byte[] block = client.ReadBytes(0, BlockBytes);
        int status = WcsConveyorCodec.ReadU16(block, (stationIndex * StationBytes) + 28);
        return ((status >> 8) & 1) == 0;
    }

    private static void WaitUntil(Func<bool> condition, string message, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"等待超时（{timeoutMs} ms）：{message}");
    }
}
