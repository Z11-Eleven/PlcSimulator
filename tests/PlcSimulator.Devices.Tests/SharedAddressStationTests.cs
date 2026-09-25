using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Stations;

namespace PlcSimulator.Devices.Tests;

/// <summary>
/// 现场存在多个站台号共用同一段寄存器的情况：它们其实是同一个物理站台，
/// 只是 WCS 需要用两个图标显示。这类站台必须共用一个状态机。
/// </summary>
public class SharedAddressStationTests
{
    private static DeviceRuntime CreateDevice()
    {
        var config = new DeviceConfig
        {
            Id = "cv-test",
            Name = "测试输送机",
            Ip = "127.0.0.1",
            ProtocolType = NtiProtocol.ProtocolName,
            Protocol = "Modbus",
            ReadByteOrderPolicy = "NTI-ConveyorRead",
            SingleFieldWriteByteOrderPolicy = "SingleFieldWrite",
            Blocks = [new RegisterBlockConfig { Group = string.Empty, BaseByteOffset = 0, LengthBytes = 256 }],
            Stations =
            [
                new StationConfig { StationNo = "1001", ByteOffset = 0, LengthBytes = 30, Simulation = new SimulationConfig() },
                new StationConfig { StationNo = "10021", ByteOffset = 30, LengthBytes = 30, Simulation = new SimulationConfig() },
                new StationConfig { StationNo = "10022", ByteOffset = 30, LengthBytes = 30, Simulation = new SimulationConfig() },
            ],
        };

        var device = new DeviceRuntime(
            config,
            NtiProtocol.Instance,
            new ByteOrderPolicy { Name = "NTI-ConveyorRead", ExtraPairUnswapRanges = [[12, 28]] },
            ByteOrderPolicy.Identity);

        StationInitializer.Apply(device);
        return device;
    }

    [Fact]
    public void PhysicalStations_WithSharedAddress_KeepsOnlyOnePerRange()
    {
        DeviceRuntime device = CreateDevice();

        Assert.Equal(3, device.Stations.Count);
        Assert.Equal(2, device.PhysicalStations.Count);
    }

    [Fact]
    public void Stations_WithSharedAddress_AreMarkedAsAliasAndPointToPrimary()
    {
        DeviceRuntime device = CreateDevice();

        StationRuntime first = device.Stations[0];
        StationRuntime shared = device.Stations[1];
        StationRuntime alias = device.Stations[2];

        Assert.False(first.IsAlias);
        Assert.Equal("1001", first.PrimaryStationNo);

        Assert.False(shared.IsAlias);
        Assert.Equal("10021", shared.PrimaryStationNo);

        Assert.True(alias.IsAlias);
        Assert.Equal("10021", alias.PrimaryStationNo);
    }

    [Fact]
    public void SimulationEngine_WithSharedAddress_CreatesOneMachinePerPhysicalStation()
    {
        DeviceRuntime device = CreateDevice();

        var engine = new SimulationEngine();
        engine.RegisterDevice(device, randomSeed: 1);

        Assert.Equal(2, engine.Machines.Count);
        Assert.NotNull(engine.Find("1001"));
        Assert.NotNull(engine.Find("10021"));

        // 别名站台号也指向同一个物理站台的状态机：拓扑按全部站台建，
        // 寻路结果里可能出现别名站台号，送达时必须找得到对应的机器。
        Assert.Same(engine.Find("10021"), engine.Find("10022"));
    }

    [Fact]
    public void TaskCycle_WithSharedAddress_IsHandledOnlyOnce()
    {
        DeviceRuntime device = CreateDevice();
        var engine = new SimulationEngine(TimeSpan.FromMilliseconds(50));
        engine.RegisterDevice(device, randomSeed: 1);

        ConveyorStationMachine primary = engine.Find("10021")!;
        StationRuntime shared = device.Stations[2];

        // WCS 给这段共用地址写任务号（两个图标对应同一段寄存器）。
        shared.Device.Space.TryWriteBytes(shared.ByteOffset, [0x04, 0xD2]);
        engine.AdvanceOneTick();

        Assert.Equal(StationState.Executing, primary.State);

        // 只应有一个状态机在跑，心跳也只会被这一台置位。
        for (int i = 0; i < 70; i++)
        {
            engine.AdvanceOneTick();
        }

        Assert.Equal(StationState.Done, primary.State);

        byte[] heartbeat = new byte[2];
        shared.Device.Space.TryReadBytes(shared.ByteOffset + 8, heartbeat);
        Assert.Equal(1, (heartbeat[0] << 8) | heartbeat[1]);
    }
}
