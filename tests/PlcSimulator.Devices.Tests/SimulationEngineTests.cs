using PlcSimulator.Devices.Stations;

namespace PlcSimulator.Devices.Tests;

/// <summary>
/// 引擎的操作投递队列：GUI 的按钮不直接改状态机字段，而是排进队列由引擎线程执行，
/// 这样「复位」这类操作才不会与同一时刻的 tick 撞车。
/// </summary>
public class SimulationEngineTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private static (SimulationEngine Engine, ConveyorStationMachine Machine) CreateEngine(out StationRuntime station)
    {
        station = StationFixtures.CreateStation();
        var engine = new SimulationEngine(Tick);
        var machine = new ConveyorStationMachine(station, randomSeed: 1);
        engine.Add(machine);
        return (engine, machine);
    }

    [Fact]
    public void Enqueue_WhenEngineNotRunning_RunsImmediately()
    {
        (SimulationEngine engine, ConveyorStationMachine machine) = CreateEngine(out _);

        engine.Enqueue(() => machine.SetManual(true));

        // 引擎没在跑就没有并发可言，按钮要立刻见效，不能让用户以为点了没反应。
        Assert.True(machine.IsManual);
    }

    [Fact]
    public async Task Enqueue_WhileRunning_RunsOperationsInOrderOnEngineThread()
    {
        (SimulationEngine engine, ConveyorStationMachine machine) = CreateEngine(out _);

        List<string> order = [];
        engine.Start();

        try
        {
            engine.Enqueue(() => order.Add("有货"));
            engine.Enqueue(() => machine.SetLoaded(true));
            engine.Enqueue(() => order.Add("手动"));
            engine.Enqueue(() => machine.SetManual(true));

            await Task.Delay(300);
        }
        finally
        {
            await engine.StopAsync();
        }

        Assert.Equal(["有货", "手动"], order);
        Assert.True(machine.IsManual);
        Assert.Equal(StationState.Loaded, machine.State);
    }
}
