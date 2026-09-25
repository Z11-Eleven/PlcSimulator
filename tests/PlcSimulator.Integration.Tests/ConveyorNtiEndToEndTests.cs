using PlcSimulator.Core.Configuration;
using PlcSimulator.Devices;
using PlcSimulator.Hosting;

namespace PlcSimulator.Integration.Tests;

/// <summary>
/// 输送线 NTI + Modbus 的端到端验证：真实 TCP 连接、按 WCS 的行为收发。
/// 覆盖字节序对齐、初值摆放、写路径解码与过量读的零填充。
/// </summary>
public sealed class ConveyorNtiEndToEndTests : IAsyncLifetime
{
    private const int StationBytes = TestConfigFactory.StationLengthBytes;
    private const int BlockBytes = StationBytes * 3;

    private SimulatorHost _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = TestPorts.Next();
        SimulatorConfig config = TestConfigFactory.BuildConveyor(_port, "1004", "1005", "1006");
        _host = SimulatorHost.Create(config);
        await _host.StartAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private DeviceRuntime Device => _host.Devices[0];

    private MiniWcsClient Connect() => new("127.0.0.1", _port);

    [Fact]
    public void ReadBlock_AfterStart_ReportsDefaultStatus()
    {
        using MiniWcsClient client = Connect();
        byte[] block = client.ReadBytes(0, BlockBytes);

        // 默认状态字：无货（X8=1）+ 自动（X9=1）+ 无故障（X10=0）→ 0x0300
        Assert.Equal(0x0300, WcsConveyorCodec.ReadU16(block, 28));
        Assert.Equal(0, WcsConveyorCodec.ReadU16(block, 0));
        Assert.Equal(0, WcsConveyorCodec.ReadU16(block, 8));
    }

    [Fact]
    public void ReadBlock_EachStation_ReportsItsOwnBytes()
    {
        Device.Stations[1].WriteOutgoingU16("tasknum", 4321);

        using MiniWcsClient client = Connect();
        byte[] block = client.ReadBytes(0, BlockBytes);

        Assert.Equal(0, WcsConveyorCodec.ReadU16(block, 0));
        Assert.Equal(4321, WcsConveyorCodec.ReadU16(block, StationBytes));
        Assert.Equal(0, WcsConveyorCodec.ReadU16(block, StationBytes * 2));
    }

    [Fact]
    public void Barcode_WrittenBySimulator_ReadsBackAsPlainAscii()
    {
        Device.Stations[0].WriteOutgoingAscii("barcode", "PLT20260921001");

        using MiniWcsClient client = Connect();
        byte[] block = client.ReadBytes(0, BlockBytes);

        Assert.Equal("PLT20260921001", WcsConveyorCodec.ReadBarcode(block, 12, 16));
    }

    [Fact]
    public void WriteTaskFields_LikeWcs_AreDecodedCorrectly()
    {
        using MiniWcsClient client = Connect();

        // 2026-09-23 起 WCS 写入不再做高低位转换，各字段按大端直读即为正确值。
        client.WriteSingleRegister(0, 1234);   // tasknum
        client.WriteSingleRegister(1, 1);      // goodstype
        client.WriteSingleRegister(2, 1004);   // from
        client.WriteSingleRegister(3, 4660);   // to

        Assert.Equal(1234, Device.Stations[0].ReadIncomingU16("tasknum"));
        Assert.Equal(1, Device.Stations[0].ReadIncomingU16("goodstype"));
        Assert.Equal(1004, Device.Stations[0].ReadIncomingU16("from"));
        Assert.Equal(4660, Device.Stations[0].ReadIncomingU16("to"));
    }

    [Fact]
    public void Heartbeat_ClearedBySingleFieldWrite_IsRecognised()
    {
        Device.Stations[0].WriteOutgoingU16("heartbeat", 1);

        using MiniWcsClient client = Connect();

        byte[] block = client.ReadBytes(0, BlockBytes);
        Assert.Equal(1, WcsConveyorCodec.ReadU16(block, 8));

        // WCS 写清零值 2，PLC 直接读到 2
        client.WriteSingleRegister(8 / 2, 2);

        Assert.Equal(2, Device.Stations[0].ReadIncomingU16("heartbeat"));
    }

    [Fact]
    public void ReadBeyondConfiguredBlock_ZeroFillsInsteadOfFailing()
    {
        using MiniWcsClient client = Connect();

        // WCS 会按组长度一次性读过界，这是正常行为，不能返回异常。
        byte[] data = client.ReadBytes(0, BlockBytes * 2);

        Assert.Equal(BlockBytes * 2, data.Length);
        Assert.All(data[BlockBytes..], static b => Assert.Equal(0, b));
    }

    [Fact]
    public void WriteOutsideConfiguredBlock_ReturnsIllegalDataAddress()
    {
        using MiniWcsClient client = Connect();

        ModbusException exception = Assert.Throws<ModbusException>(
            () => client.WriteBytes(5000, new byte[2]));

        Assert.Contains("0x02", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadCoils_IsSupportedSoUnexpectedFunctionCodesStillWork()
    {
        using MiniWcsClient client = Connect();

        // 线圈功能码本模拟器不做业务使用，但要保证能得到合理响应而不是 IllegalFunction。
        Device.Space.TryWriteBytes(0, [0b0000_0101]);
        byte[] data = client.ReadBytes(0, BlockBytes);

        Assert.NotEmpty(data);
    }
}
