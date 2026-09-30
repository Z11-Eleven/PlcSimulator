using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Hosting;
using PlcSimulator.Protocol.S7;
using PlcSimulator.Core.Frames;
using System.Collections.Concurrent;

namespace PlcSimulator.Integration.Tests;

public class SrmS7EndToEndTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Move_MatchingWriteLayout_ReportsRunningThenDone(int payloadOffset)
    {
        var config = Config(payloadOffset);
        config.Devices[0].Srm!.TravelDelayMs = 300;
        await using var host = SimulatorHost.Create(config);
        await host.StartAsync();
        using var client = await Client.ConnectAsync(config.Devices[0].S7!.Port);
        Assert.Equal(0xFF, await client.WriteAsync(60, 0, Command(payloadOffset, SrmCommandType.PosGet, 1199, 5)));
        await WaitAsync(client, SrmFunctionReport.PosGetRunning);
        byte[] done = await WaitAsync(client, SrmFunctionReport.PosGetDone);
        Assert.Equal(5, done[10]);
    }

    [Fact]
    public async Task Move_23ByteWriteWithOffset2_DoesNotSubmitIncompletePayload()
    {
        var config = Config(2);
        await using var host = SimulatorHost.Create(config);
        await host.StartAsync();
        using var client = await Client.ConnectAsync(config.Devices[0].S7!.Port);
        Assert.Equal(0xFF, await client.WriteAsync(60, 0, Command(0, SrmCommandType.PosGet, 1199, 5)));
        await Task.Delay(200);
        Assert.Equal(SrmFunctionReport.Idle, (await client.ReadAsync(61, 0, 74))[6]);
        Assert.Equal(0, host.SrmDevices[0].PendingCommandCount);
    }

    [Fact]
    public async Task WriteLog_ContainsDbAddressAndItemReturnCode()
    {
        var config = Config(0);
        var log = new RingBufferFrameLog();
        var entries = new ConcurrentQueue<FrameLogEntry>();
        log.EntryAdded += (_, entry) => entries.Enqueue(entry);
        await using var host = SimulatorHost.Create(config, log);
        await host.StartAsync();
        using var client = await Client.ConnectAsync(config.Devices[0].S7!.Port);
        await client.WriteAsync(60, 0, Command(0, SrmCommandType.PosGet, 1199, 5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!entries.Any(e => e.Direction == FrameDirection.Sent && e.Summary.Contains("项返回码 FF")))
        {
            await Task.Delay(10, timeout.Token);
        }
        Assert.Contains(entries, e => e.Direction == FrameDirection.Received
            && e.Summary.StartsWith("[S7] 写入 DB60.0") && e.Summary.Contains("数量 23"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public async Task DbReadWrite_PickPutClear_PreservesSocketFields(int payloadOffset)
    {
        var config = Config(payloadOffset);
        await using var host = SimulatorHost.Create(config);
        await host.StartAsync();
        using var client = await Client.ConnectAsync(config.Devices[0].S7!.Port);
        byte[] idle = await client.ReadAsync(61, 0, 74);
        Assert.Equal(SrmFunctionMode.Standby, idle[7]);
        byte[] command = Command(payloadOffset, SrmCommandType.GetC, 0x1234, 2);
        Assert.Equal(0xFF, await client.WriteAsync(60, 0, command));
        Assert.Equal(command, await client.ReadAsync(60, 0, command.Length));
        byte[] pick = await WaitAsync(client, SrmFunctionReport.GetDone);
        Assert.Equal(0x12, pick[4]);
        Assert.Equal(0x34, pick[5]);
        Assert.Equal(SrmForkStatusBits.Fork1, pick[8]);
        Assert.Equal(0xFF, await client.WriteAsync(60, 0, Command(payloadOffset, SrmCommandType.PutC, 0x1234, 3)));
        byte[] put = await WaitAsync(client, SrmFunctionReport.PutDone);
        Assert.Equal(0, put[8]);
        Assert.Equal(3, put[10]);
        Assert.Equal(0xFF, await client.WriteAsync(60, 0, Command(payloadOffset, SrmCommandType.NoFunc, 0, 0)));
        byte[] cleared = await WaitAsync(client, SrmFunctionReport.Idle);
        Assert.All(cleared[2..6], b => Assert.Equal(0, b));
        Assert.All(await client.ReadAsync(70, 0, 100), b => Assert.Equal(0, b));
        Assert.Equal(3, await client.WriteAsync(61, 0, new byte[23]));
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task SplitWrites_UnfinishedPayload_DoesNotExecuteUntilComplete()
    {
        var config = Config(2);
        await using var host = SimulatorHost.Create(config);
        await host.StartAsync();
        using var client = await Client.ConnectAsync(config.Devices[0].S7!.Port);
        byte[] command = Command(2, SrmCommandType.GetC, 1001, 2);
        Assert.Equal(0xFF, await client.WriteAsync(60, 0, command[..15]));
        await Task.Delay(200);
        Assert.Equal(SrmFunctionReport.Idle, (await client.ReadAsync(61, 0, 74))[6]);
        Assert.Equal(0xFF, await client.WriteAsync(60, 15, command[15..]));
        await WaitAsync(client, SrmFunctionReport.GetDone);
        Assert.Equal(5, await client.WriteAsync(60, 25, new byte[2]));
    }

    [Fact]
    public async Task Start_PortOccupied_ReleasesEarlierListener()
    {
        int free = TestPorts.Next();
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            var runtime = PlcSimulator.Devices.Srm.SrmDeviceRuntime.Create(Config(2).Devices[0]);
            await using var server = new S7TcpServer([
                new(IPAddress.Loopback, free, runtime),
                new(IPAddress.Loopback, ((IPEndPoint)occupied.LocalEndpoint).Port, runtime)]);
            await Assert.ThrowsAsync<SocketException>(() => server.StartAsync());
            Assert.False(server.IsRunning);
            var probe = new TcpListener(IPAddress.Loopback, free);
            probe.Start();
            probe.Stop();
        }
        finally
        {
            occupied.Stop();
        }
    }

    private static SimulatorConfig Config(int offset) => new()
    {
        Server = new() { Listen = [] },
        Devices = [new()
        {
            Id = "s7-test", Ip = "127.0.0.1", Protocol = "S7", ProtocolType = "SRM", DeviceType = "Srm",
            S7 = new() { Port = TestPorts.Next(), CommandPayloadOffset = offset },
            Srm = new() { TravelDelayMs = 50, ActionDelayMs = 50, JitterMs = 0, ForkCount = 2 }
        }]
    };

    private static byte[] Command(int offset, byte type, ushort task, byte point)
    {
        byte[] bytes = new byte[offset == 2 ? 26 : 23];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 2), task);
        bytes[offset + 4] = type;
        bytes[offset + 5] = 1;
        bytes[offset + 7] = 1;
        bytes[offset + 8] = point;
        return bytes;
    }

    private static async Task<byte[]> WaitAsync(Client client, byte report)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            byte[] status = await client.ReadAsync(61, 0, 74);
            if (status[6] == report)
            {
                return status;
            }
            await Task.Delay(20, timeout.Token);
        }
    }

    // 独立构造客户端线报文，跨 TCP 验证 COTP、协商、读写及分包；不调用服务端编码器。
    private sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp = new();
        private ushort _reference;
        public static async Task<Client> ConnectAsync(int port)
        {
            var client = new Client();
            await client._tcp.ConnectAsync(IPAddress.Loopback, port);
            byte[] cc = await client.ExchangeAsync(Convert.FromHexString("0300001611E00000000100C1020100C2020101C0010A"));
            Assert.Equal(0xD0, cc[5]);
            byte[] setup = await client.JobAsync([0xF0, 0, 0, 1, 0, 1, 3, 0xC0], []);
            Assert.Equal(0, setup[17]);
            Assert.Equal(0, setup[18]);
            return client;
        }

        public async Task<byte[]> ReadAsync(int db, int offset, int length)
        {
            byte[] response = await JobAsync(Parameters(4, db, offset, length), []);
            Assert.Equal(0xFF, response[21]);
            return response[25..];
        }

        public async Task<byte> WriteAsync(int db, int offset, byte[] bytes)
        {
            byte[] data = new byte[4 + bytes.Length];
            data[1] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)(bytes.Length * 8));
            bytes.CopyTo(data, 4);
            return (await JobAsync(Parameters(5, db, offset, bytes.Length), data))[21];
        }

        private static byte[] Parameters(byte function, int db, int offset, int length)
        {
            byte[] p = [function, 1, 0x12, 0x0A, 0x10, 2, 0, 0, 0, 0, 0x84, 0, 0, 0];
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), (ushort)length);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(8), (ushort)db);
            int bits = offset * 8;
            p[11] = (byte)(bits >> 16);
            p[12] = (byte)(bits >> 8);
            p[13] = (byte)bits;
            return p;
        }

        private Task<byte[]> JobAsync(byte[] parameters, byte[] data)
        {
            byte[] frame = new byte[17 + parameters.Length + data.Length];
            frame[0] = 3;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)frame.Length);
            frame[4] = 2; frame[5] = 0xF0; frame[6] = 0x80; frame[7] = 0x32; frame[8] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(11), ++_reference);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13), (ushort)parameters.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(15), (ushort)data.Length);
            parameters.CopyTo(frame, 17);
            data.CopyTo(frame, 17 + parameters.Length);
            return ExchangeAsync(frame);
        }

        private async Task<byte[]> ExchangeAsync(byte[] request)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            NetworkStream stream = _tcp.GetStream();
            // 拆开 TPKT 头，避免服务端误把单次 TCP Read 当成完整报文。
            await stream.WriteAsync(request.AsMemory(0, 2), timeout.Token);
            await stream.WriteAsync(request.AsMemory(2), timeout.Token);
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            byte[] response = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2))];
            header.CopyTo(response, 0);
            await stream.ReadExactlyAsync(response.AsMemory(4), timeout.Token);
            return response;
        }
        public void Dispose() => _tcp.Dispose();
    }
}
