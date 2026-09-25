using System.Diagnostics;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Hosting;

namespace PlcSimulator.Integration.Tests;

/// <summary>
/// M3 的核心验收：WCS 与模拟器之间的完整任务闭环，连续多轮无异常。
/// 流程 —— 下发任务号 → 模拟器动作 → 置心跳 → WCS 读到心跳写清零值 → 模拟器复位回空闲。
/// </summary>
public sealed class ConveyorTaskCycleTests : IAsyncLifetime
{
    private const int BlockBytes = TestConfigFactory.StationLengthBytes * 3;
    private const int ActionDelayMs = 100;
    private const int HeartbeatByteOffset = 8;
    private const int HeartbeatRegister = HeartbeatByteOffset / 2;
    private const int TaskNumRegister = 0;

    private SimulatorHost _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = TestPorts.Next();
        SimulatorConfig config = TestConfigFactory.BuildConveyor(
            _port, ActionDelayMs, jitterMs: 20, "1004", "1005", "1006");

        _host = SimulatorHost.Create(config);
        await _host.StartAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public void FullTaskCycle_FiftyRounds_AlwaysReturnsToIdle()
    {
        ConveyorStationMachine machine = _host.Engine.Find("1004")
            ?? throw new InvalidOperationException("未找到站台 1004 的状态机。");

        using MiniWcsClient client = new("127.0.0.1", _port);

        // 默认初值：无货待命
        Assert.Equal(StationState.Idle, machine.State);

        for (int round = 1; round <= 50; round++)
        {
            ushort taskNum = (ushort)(1000 + round);

            // 1. WCS 下发任务号（单字段写，大端不交换）
            client.WriteSingleRegister(TaskNumRegister, taskNum);
            WaitUntil(() => machine.State == StationState.Executing, $"第 {round} 轮：模拟器未进入动作态");

            // 2. 动作延时结束，模拟器置心跳
            WaitUntil(() => machine.State == StationState.Done, $"第 {round} 轮：模拟器未完成动作");

            // 3. WCS 轮询读到心跳 == 1
            byte[] block = client.ReadBytes(0, BlockBytes);
            Assert.Equal(1, WcsConveyorCodec.ReadU16(block, HeartbeatByteOffset));

            // 4. WCS 写清零值 2
            client.WriteSingleRegister(HeartbeatRegister, 2);
            WaitUntil(() => machine.State == StationState.Idle, $"第 {round} 轮：模拟器未回到空闲");

            // 5. 心跳已归零，站台可以接下一单
            byte[] afterClear = client.ReadBytes(0, BlockBytes);
            Assert.Equal(0, WcsConveyorCodec.ReadU16(afterClear, HeartbeatByteOffset));
            Assert.Equal(0, machine.Snapshot().TaskNum);
        }
    }

    [Fact]
    public void TaskCycle_TwoStations_AreIndependent()
    {
        ConveyorStationMachine first = _host.Engine.Find("1004")!;
        ConveyorStationMachine second = _host.Engine.Find("1005")!;

        using MiniWcsClient client = new("127.0.0.1", _port);

        // 只给 1005 下发任务，1004 不应有任何反应。
        const int secondStationTaskRegister = TestConfigFactory.StationLengthBytes / 2 + TaskNumRegister;
        client.WriteSingleRegister((ushort)secondStationTaskRegister, 2001);

        WaitUntil(() => second.State == StationState.Executing, "站台 1005 未进入动作态");

        Assert.Equal(StationState.Idle, first.State);

        WaitUntil(() => second.State == StationState.Done, "站台 1005 未完成动作");

        byte[] block = client.ReadBytes(0, BlockBytes);
        Assert.Equal(0, WcsConveyorCodec.ReadU16(block[..TestConfigFactory.StationLengthBytes], HeartbeatByteOffset));
        Assert.Equal(1, WcsConveyorCodec.ReadU16(block, TestConfigFactory.StationLengthBytes + HeartbeatByteOffset));
    }

    [Fact]
    public void ManualInjection_SetLoaded_TogglesStatusSeenByWcs()
    {
        ConveyorStationMachine machine = _host.Engine.Find("1006")!;
        using MiniWcsClient client = new("127.0.0.1", _port);

        int thirdStationByteOffset = TestConfigFactory.StationLengthBytes * 2;

        // 现场协议 X8 StaLoad：0 = 有货，1 = 无货。默认初值是无货。
        byte[] before = client.ReadBytes(0, BlockBytes);
        int statusBefore = WcsConveyorCodec.ReadU16(before, thirdStationByteOffset + 28);
        Assert.Equal(1, (statusBefore >> 8) & 1);

        machine.SetLoaded(true);

        byte[] after = client.ReadBytes(0, BlockBytes);
        int statusAfter = WcsConveyorCodec.ReadU16(after, thirdStationByteOffset + 28);
        Assert.Equal(0, (statusAfter >> 8) & 1);
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
