using System.Diagnostics;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Hosting;

namespace PlcSimulator.Integration.Tests;

public sealed class WcsStationClearTests
{
    [Theory]
    [InlineData(StationState.Executing, false)]
    [InlineData(StationState.WaitingDownstream, false)]
    [InlineData(StationState.Transferring, false)]
    [InlineData(StationState.Done, false)]
    [InlineData(StationState.Loaded, false)]
    [InlineData(StationState.Transferring, true)]
    public async Task WcsClear_StationHasTask_ResetsMachineAndCancelsLocalCargo(StationState state, bool singleRegister)
    {
        int port = TestPorts.Next();
        var config = TestConfigFactory.BuildConveyor(port, 400, 0, "1001", "1002");
        foreach (var station in config.Devices[0].Stations)
        {
            station.Simulation.TransferDelayMs = 400;
        }

        await using var host = SimulatorHost.Create(config);
        await host.StartAsync();
        var source = host.Engine.Find("1001")!;
        var target = host.Engine.Find("1002")!;
        using var client = new MiniWcsClient("127.0.0.1", port, readTimeoutMs: 3000);

        if (state == StationState.Loaded)
        {
            host.Engine.Enqueue(() => source.ReceiveCargo(1234, "WCS-CLEAR", 7, "1002", "1001"));
        }
        else
        {
            if (state == StationState.WaitingDownstream)
            {
                host.Engine.Enqueue(() => target.SetManual(true));
                WaitUntil(() => target.IsManual);
            }

            client.WriteSingleRegister(3, 1002);
            client.WriteSingleRegister(0, 1234);
        }

        WaitUntil(() => source.State == state);
        host.Engine.Enqueue(() => source.SetManual(true));
        WaitUntil(() => source.IsManual);

        if (singleRegister)
        {
            // 在途时任务号早已由模拟器清零，WCS 再写零也必须触发取消。
            client.WriteSingleRegister(0, 0);
        }
        else
        {
            // WCS 的清空操作按整块写入，模拟器不得只清寄存器而保留内存任务。
            client.WriteBytes(0, new byte[TestConfigFactory.StationLengthBytes]);
        }

        WaitUntil(() => source.State == StationState.Idle && source.LastEvent.StartsWith("WCS 清空站台"));
        Assert.True(source.IsManual);
        Assert.Equal(0, source.Snapshot().TaskNum);
        byte[] cleared = client.ReadBytes(0, TestConfigFactory.StationLengthBytes);
        Assert.All(cleared[..28], value => Assert.Equal((byte)0, value));
        Assert.Equal(0x0100, WcsConveyorCodec.ReadU16(cleared, 28));

        host.Engine.Enqueue(() =>
        {
            source.SetManual(false);
            target.SetManual(false);
        });
        await Task.Delay(1100);
        Assert.Equal(StationState.Idle, source.State);
        Assert.Equal(0, source.Station.ReadIncomingU16("heartbeat"));
        Assert.Equal(state == StationState.Done ? StationState.Loaded : StationState.Idle, target.State);
        Assert.Equal(state == StationState.Done ? 1234 : 0, target.Station.ReadIncomingU16("tasknum"));
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 5000)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("等待站台状态变化超时。");
    }
}
