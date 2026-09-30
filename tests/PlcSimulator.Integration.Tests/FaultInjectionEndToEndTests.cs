using System.Diagnostics;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Devices.Stations;
using PlcSimulator.Hosting;

namespace PlcSimulator.Integration.Tests;

/// <summary>
/// 故障注入在真实 TCP 下的行为验证。
/// 每台模拟器起在独立端口上，测试之间互不影响。
/// </summary>
public sealed class FaultInjectionEndToEndTests
{
    private const int BlockBytes = TestConfigFactory.StationLengthBytes * 3;

    [Fact]
    public Task ExceptionResponse_ReturnsConfiguredCode() => RunAsync(
        new FaultInjectionConfig
        {
            Enabled = true,
            Rules =
            [
                new FaultRuleConfig
                {
                    Name = "非法地址",
                    Effect = "ExceptionResponse",
                    ExceptionCode = 2,
                    Match = new FaultMatchConfig { FunctionCodes = [3] },
                },
            ],
        },
        (_, port) =>
        {
            using var client = new MiniWcsClient("127.0.0.1", port);
            ModbusException exception = Assert.Throws<ModbusException>(() => client.ReadBytes(0, BlockBytes));
            Assert.Contains("0x02", exception.Message, StringComparison.Ordinal);
            return Task.CompletedTask;
        });

    [Fact]
    public Task SilentDrop_ClientTimesOut() => RunAsync(
        new FaultInjectionConfig
        {
            Enabled = true,
            Rules = [new FaultRuleConfig { Name = "读不响应", Effect = "SilentDrop" }],
        },
        (_, port) =>
        {
            using var client = new MiniWcsClient("127.0.0.1", port, readTimeoutMs: 1200);

            // 服务端不回帧，客户端只能自己超时——这正是 WCS 断线降速重连的触发条件。
            Assert.ThrowsAny<IOException>(() => client.ReadBytes(0, BlockBytes));
            return Task.CompletedTask;
        });

    [Fact]
    public Task CloseConnection_DropsSocket() => RunAsync(
        new FaultInjectionConfig
        {
            Enabled = true,
            Rules = [new FaultRuleConfig { Name = "立即断开", Effect = "CloseConnection" }],
        },
        (_, port) =>
        {
            using var client = new MiniWcsClient("127.0.0.1", port, readTimeoutMs: 2000);
            Assert.ThrowsAny<IOException>(() => client.ReadBytes(0, BlockBytes));
            return Task.CompletedTask;
        });

    [Fact]
    public Task DelayThenPass_StillSucceedsButSlower() => RunAsync(
        new FaultInjectionConfig
        {
            Enabled = true,
            Rules = [new FaultRuleConfig { Name = "延时 500ms", Effect = "DelayThenPass", DelayMs = 500 }],
        },
        (_, port) =>
        {
            using var client = new MiniWcsClient("127.0.0.1", port);

            var stopwatch = Stopwatch.StartNew();
            byte[] data = client.ReadBytes(0, BlockBytes);
            stopwatch.Stop();

            Assert.Equal(BlockBytes, data.Length);
            Assert.True(stopwatch.ElapsedMilliseconds >= 400, $"实际耗时 {stopwatch.ElapsedMilliseconds} ms");
            return Task.CompletedTask;
        });

    [Fact]
    public Task FaultInjection_DoesNotMutateRegisters() => RunAsync(
        new FaultInjectionConfig
        {
            Enabled = true,
            Rules = [new FaultRuleConfig { Name = "写被拒", Effect = "ExceptionResponse", ExceptionCode = 4 }],
        },
        (host, port) =>
        {
            using var client = new MiniWcsClient("127.0.0.1", port);

            byte[] before = new byte[2];
            host.Devices[0].Space.TryReadBytes(0, before);

            Assert.Throws<ModbusException>(() => client.WriteSingleRegister(0, 1234));

            // 故障门位于访问数据区之前，因此被拒的写不会落盘。
            byte[] after = new byte[2];
            host.Devices[0].Space.TryReadBytes(0, after);
            Assert.Equal(before, after);
            return Task.CompletedTask;
        });

    [Fact]
    public Task MaxTimesExceeded_NormalTrafficResumes() => RunAsync(
        new FaultInjectionConfig
        {
            Enabled = true,
            Rules =
            [
                new FaultRuleConfig
                {
                    Name = "只坏两次",
                    Effect = "ExceptionResponse",
                    ExceptionCode = 4,
                    MaxTimes = 2,
                },
            ],
        },
        (_, port) =>
        {
            using var client = new MiniWcsClient("127.0.0.1", port);

            Assert.Throws<ModbusException>(() => client.ReadBytes(0, BlockBytes));
            Assert.Throws<ModbusException>(() => client.ReadBytes(0, BlockBytes));

            byte[] data = client.ReadBytes(0, BlockBytes);
            Assert.Equal(BlockBytes, data.Length);
            return Task.CompletedTask;
        });

    [Fact]
    public Task HeartbeatStop_StationCompletesWithoutRaisingHeartbeat() => RunAsync(
        new FaultInjectionConfig
        {
            Enabled = true,
            Rules = [new FaultRuleConfig { Name = "1004 心跳停止", Effect = "HeartbeatStop", StationNo = "1004" }],
        },
        async (host, port) =>
        {
            using var client = new MiniWcsClient("127.0.0.1", port);

            client.WriteSingleRegister(0, 1234);
            client.WriteSingleRegister(3, 1006);   // to @ 字节偏移 6：送到 1006，好让 1005 保持空闲

            ConveyorStationMachine machine = host.Engine.Find("1004")!;
            await WaitUntilAsync(() => machine.State == StationState.Done, "站台未完成动作");

            byte[] block = client.ReadBytes(0, BlockBytes);
            Assert.Equal(0, WcsConveyorCodec.ReadU16(block, 8));

            // 其它站台不受影响，仍保持默认的无货待命状态。
            Assert.Equal(StationState.Idle, host.Engine.Find("1005")!.State);
        });

    [Fact]
    public Task DisabledFaultConfig_BehavesNormally() => RunAsync(
        new FaultInjectionConfig
        {
            Enabled = false,
            Rules = [new FaultRuleConfig { Name = "未启用", Effect = "SilentDrop" }],
        },
        (host, port) =>
        {
            Assert.False(host.FaultInjector.Enabled);

            using var client = new MiniWcsClient("127.0.0.1", port);
            byte[] data = client.ReadBytes(0, BlockBytes);
            Assert.Equal(BlockBytes, data.Length);
            return Task.CompletedTask;
        });

    private static async Task RunAsync(FaultInjectionConfig faults, Func<SimulatorHost, int, Task> body)
    {
        int port = TestPorts.Next();
        SimulatorConfig config = TestConfigFactory.BuildConveyor(port, 100, 20, "1004", "1005", "1006");
        config.FaultInjection = faults;

        var host = SimulatorHost.Create(config);
        await host.StartAsync();

        try
        {
            await body(host, port);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"等待超时（{timeoutMs} ms）：{message}");
    }
}
