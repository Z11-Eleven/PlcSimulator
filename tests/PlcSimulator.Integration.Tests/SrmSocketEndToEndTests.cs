using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Srm;
using PlcSimulator.Hosting;

namespace PlcSimulator.Integration.Tests;

/// <summary>
/// 堆垛机（Socket 传输）的端到端测试：用真实 TcpClient 扮演 WCS 连它的三个端口。
/// </summary>
public class SrmSocketEndToEndTests
{
    [Fact]
    public async Task StatusPort_GivenTriggerByte_ReturnsStatusFrameWithStandbyMode()
    {
        SimulatorHost host = await StartSrmAsync();

        await using (host)
        using (var client = new MiniWcsSrmClient(host.SrmDevices[0].Ports))
        {
            byte[] frame = await client.PollStatusAsync();

            Assert.Equal(host.SrmDevices[0].Ports.StatusFrameLength, frame.Length);
            Assert.Equal(SrmFunctionReport.Idle, frame[6]);
            Assert.Equal(SrmFunctionMode.Standby, frame[7]);
        }
    }

    [Fact]
    public async Task CommandPort_GivenCommandFrame_SimulatorDoesNotReply()
    {
        SimulatorHost host = await StartSrmAsync();

        await using (host)
        using (var client = new MiniWcsSrmClient(host.SrmDevices[0].Ports))
        {
            client.SendCommand(BuildCommand(SrmCommandType.GetC, taskNum: 1001, actionPoint: 2));

            // WCS 在 2000 端口没有接收处理分支；模拟器若回写，字节会堆在对端缓冲里
            // 越积越多，最终把状态口的解析搅乱。
            Assert.False(await client.TryReadCommandPortAsync(timeoutMs: 300));
        }
    }

    [Fact]
    public async Task TaskCycle_GetcPutcNoFunc_ReportsStandbyThenGetDonePutDoneBackToStandby()
    {
        SimulatorHost host = await StartSrmAsync(travelDelayMs: 50, actionDelayMs: 50);

        await using (host)
        using (var client = new MiniWcsSrmClient(host.SrmDevices[0].Ports))
        {
            // 取货
            client.SendCommand(BuildCommand(SrmCommandType.GetC, taskNum: 1001, actionPoint: 2));
            byte[] afterPick = await client.WaitForReportAsync(SrmFunctionReport.GetDone);

            Assert.Equal(1001, (afterPick[4] << 8) | afterPick[5]);
            Assert.Equal(SrmForkStatusBits.Fork1, afterPick[8]);   // 货叉 1 载货

            // 放货
            client.SendCommand(BuildCommand(SrmCommandType.PutC, taskNum: 1001, actionPoint: 3));
            byte[] afterPut = await client.WaitForReportAsync(SrmFunctionReport.PutDone);

            Assert.Equal(0, afterPut[8]);   // 已卸空
            Assert.Equal(3, afterPut[10]);  // 位置跟着动作走

            // 清除
            client.SendCommand(BuildCommand(SrmCommandType.NoFunc, taskNum: 0, actionPoint: 0));
            byte[] idle = await client.WaitForReportAsync(SrmFunctionReport.Idle);

            Assert.Equal(0, (idle[4] << 8) | idle[5]);
            Assert.Equal(0, (idle[2] << 8) | idle[3]);
        }
    }

    [Fact]
    public async Task StatusPort_MultipleTriggers_EachGetsAReply()
    {
        SimulatorHost host = await StartSrmAsync();

        await using (host)
        using (var client = new MiniWcsSrmClient(host.SrmDevices[0].Ports))
        {
            // 连发两次轮询，应当拿到两帧——逐字节应答是这套协议的基本约定。
            for (int i = 0; i < 3; i++)
            {
                byte[] frame = await client.PollStatusAsync();
                Assert.Equal(SrmFunctionMode.Standby, frame[7]);
            }
        }
    }

    [Fact]
    public async Task AlarmPort_ByDefault_ReturnsAllZeroBitmap()
    {
        SimulatorHost host = await StartSrmAsync();

        await using (host)
        using (var client = new MiniWcsSrmClient(host.SrmDevices[0].Ports))
        {
            byte[] bitmap = await client.PollAlarmAsync();

            // 报警位图只在工作模式的故障位置位时才被 WCS 解析，平时全零即可。
            Assert.Equal(host.SrmDevices[0].Ports.AlarmFrameLength, bitmap.Length);
            Assert.All(bitmap, static b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public async Task MixedDevices_ConveyorAndSrm_BothServeSimultaneously()
    {
        int modbusPort = TestPorts.Next();

        SimulatorConfig conveyor = TestConfigFactory.BuildConveyor(
            modbusPort, actionDelayMs: 100, jitterMs: 0, "1001");

        SimulatorConfig srm = TestConfigFactory.BuildSrm(
            TestPorts.Next(), TestPorts.Next(), TestPorts.Next());

        var mixed = new SimulatorConfig
        {
            Server = conveyor.Server,
            ByteOrder = conveyor.ByteOrder,
            Devices = [.. conveyor.Devices, .. srm.Devices],
        };

        await using SimulatorHost host = SimulatorHost.Create(mixed);
        await host.StartAsync();

        // 一个 Modbus 端点 + 三个 Socket 端口同时在监听。
        Assert.Equal(4, host.Endpoints.Count);
        Assert.Contains(host.Endpoints, e => e.Port == modbusPort);
        Assert.Contains(host.Endpoints, e => e.Port == host.SrmDevices[0].Ports.Command);
        Assert.Contains(host.Endpoints, e => e.Port == host.SrmDevices[0].Ports.Status);
        Assert.Contains(host.Endpoints, e => e.Port == host.SrmDevices[0].Ports.Alarm);

        // 两条链路都真的能用：Modbus 侧站台已建，堆垛机侧状态口能应答。
        Assert.NotEmpty(host.Devices);
        Assert.Single(host.SrmDevices);

        using var client = new MiniWcsSrmClient(host.SrmDevices[0].Ports);
        byte[] frame = await client.PollStatusAsync();
        Assert.Equal(SrmFunctionMode.Standby, frame[7]);
    }

    [Fact]
    public async Task SrmOnlyConfig_WithoutModbusEndpoint_StartsSuccessfully()
    {
        // 只跑堆垛机的配置里 server.listen 是空的，不该因此起不来。
        SimulatorHost host = await StartSrmAsync();

        await using (host)
        {
            Assert.True(host.IsRunning);
            Assert.Empty(host.Devices);
            Assert.Single(host.SrmDevices);
            Assert.Equal(3, host.Endpoints.Count);
        }
    }

    private static async Task<SimulatorHost> StartSrmAsync(
        int travelDelayMs = 100,
        int actionDelayMs = 100)
    {
        SimulatorConfig config = TestConfigFactory.BuildSrm(
            TestPorts.Next(), TestPorts.Next(), TestPorts.Next(), travelDelayMs, actionDelayMs);

        SimulatorHost host = SimulatorHost.Create(config);
        await host.StartAsync();
        return host;
    }

    private static SrmCommand BuildCommand(byte type, ushort taskNum, byte actionPoint) => new(
        Fork1TaskNum: taskNum,
        Fork2TaskNum: 0,
        CommandType: type,
        ForkNo: 1,
        Fork1GoodsType: 1,
        Fork2GoodsType: 0,
        ActionPoint: actionPoint,
        Aisle: 1,
        Row: 1,
        Column: 1,
        Cell: 1,
        Level: 1,
        Depth: 1,
        FireFlag: 0,
        Face1: 0,
        Face2: 0);
}
