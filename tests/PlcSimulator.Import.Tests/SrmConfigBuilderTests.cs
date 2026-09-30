using PlcSimulator.Core.Configuration;

namespace PlcSimulator.Import.Tests;

public class SrmConfigBuilderTests
{
    private const string Header = "equipmentnum,RIPADDR,protocoltype,rport,field3,field2,forktype,stationpoint,pickstation,putstation,wdbaddr,rdbaddr,wdblength,rdblength";
    private static string Row(string id, int port, string protocol = "Socket_NTI", string name = "测试堆垛机")
        => $"{id},127.0.0.1,{protocol},{port},{name},1,3;3,\"P1,2;P2,3;\",P1,P2,80,81,23,171";

    private static SimulatorConfig Build(string csv, ImportOptions options, out List<string> notes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"srm-import-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, csv);
        try
        {
            return ConfigBuilder.Build(ImportTable.Load(path), options, out notes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Socket_ThreeRows_MergesDeviceAndStationPoints()
    {
        var config = Build(string.Join('\n', Header, Row("SC01", 2000), Row("SC01", 4000), Row("SC01", 6000)),
            new() { DeviceKind = ImportDeviceKind.Srm }, out _);
        var device = Assert.Single(config.Devices);
        Assert.Equal("Socket", device.Protocol);
        Assert.Equal(2, device.Srm!.ForkCount);
        Assert.Equal(2, device.Srm.StationPoints["P1"]);
        Assert.Equal(new[] { "P1" }, device.Srm.PickStations);
        Assert.Equal(6000, device.SocketPorts!.Alarm);
        Assert.Empty(config.Server.Listen);
        Assert.True(ConfigLoader.Validate(config).IsValid);
    }

    [Fact]
    public void SocketToS7_Options_UsesConfiguredDbAndLoopbackIps()
    {
        var config = Build(string.Join('\n', Header, Row("SC01", 2000), Row("SC02", 2000)),
            new() { DeviceKind = ImportDeviceKind.Srm, SrmTransport = "S7", UseLoopbackIps = true,
                S7 = new() { Command = new() { DbNumber = 90 }, CommandPayloadOffset = 0 } }, out _);
        Assert.Equal(2, config.Devices.Count);
        Assert.Equal("127.0.0.2", config.Devices[1].Ip);
        Assert.All(config.Devices, d =>
        {
            Assert.Equal("S7", d.Protocol);
            Assert.Null(d.SocketPorts);
            Assert.Equal(90, d.S7!.Command.DbNumber);
            Assert.Equal(23, d.S7.CommandLength);
        });
    }

    [Fact]
    public void NativeS7_UsesExportDbAndLengths()
    {
        var config = Build(string.Join('\n', Header, Row("SC01", 102, "S7_NTI")),
            new() { DeviceKind = ImportDeviceKind.Srm, SrmTransport = "S7", S7 = new() { CommandPayloadOffset = 0 } }, out _);
        var s7 = config.Devices[0].S7!;
        Assert.Equal(80, s7.Command.DbNumber);
        Assert.Equal(81, s7.Status.DbNumber);
        Assert.Equal(171, s7.StatusLength);
        Assert.Equal(0, s7.CommandPayloadOffset);
    }

    [Fact]
    public void NativeS7_ExportWriteArea26_PreservesSelected23BytePayloadLayout()
    {
        string row = Row("SC01", 102, "S7_NTI").Replace(",23,171", ",26,171");
        var config = Build(string.Join('\n', Header, row),
            new() { DeviceKind = ImportDeviceKind.Srm, SrmTransport = "S7", S7 = new() { CommandPayloadOffset = 0 } }, out _);
        Assert.Equal(0, config.Devices[0].S7!.CommandPayloadOffset);
        Assert.Equal(23, config.Devices[0].S7!.CommandLength);
        Assert.Equal(171, config.Devices[0].S7!.StatusLength);
    }

    [Theory]
    [InlineData("127.0.0.20", "127.0.0.20", "127.0.0.21")]
    [InlineData("127.0.0.255", "127.0.0.255", "127.0.1.0")]
    [InlineData("127.255.255.254", "127.255.255.254", "127.255.255.255")]
    public void LoopbackStart_AssignsConsecutiveIpv4Addresses(string start, string first, string second)
    {
        var config = Build(string.Join('\n', Header, Row("SC01", 2000), Row("SC02", 2000)),
            new() { DeviceKind = ImportDeviceKind.Srm, SrmTransport = "S7", UseLoopbackIps = true, LoopbackStartIp = start }, out _);
        Assert.Equal(first, config.Devices[0].Ip);
        Assert.Equal(second, config.Devices[1].Ip);
    }

    [Theory]
    [InlineData("192.168.0.20")]
    [InlineData("::1")]
    [InlineData("127.1")]
    [InlineData("127.0.0.256")]
    public void LoopbackStart_InvalidAddress_ReportsError(string start)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Build(string.Join('\n', Header, Row("SC01", 2000)),
            new() { DeviceKind = ImportDeviceKind.Srm, SrmTransport = "S7", UseLoopbackIps = true, LoopbackStartIp = start }, out _));
        Assert.Contains("起始回环 IP", ex.Message);
    }

    [Fact]
    public void LoopbackStart_AddressOverflow_RejectsLeavingLoopbackRange()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Build(string.Join('\n', Header, Row("SC01", 2000), Row("SC02", 2000)),
            new() { DeviceKind = ImportDeviceKind.Srm, SrmTransport = "S7", UseLoopbackIps = true, LoopbackStartIp = "127.255.255.255" }, out _));
        Assert.Contains("超出", ex.Message);
    }

    [Fact]
    public void DuplicateIpWithoutLoopback_RejectsConflictingEndpoints()
    {
        Assert.Throws<InvalidDataException>(() => Build(string.Join('\n', Header, Row("SC01", 2000), Row("SC02", 2000)),
            new() { DeviceKind = ImportDeviceKind.Srm, SrmTransport = "S7" }, out _));
    }

    [Fact]
    public void ConflictingMetadata_RejectsRatherThanPickingFirstRow()
    {
        Assert.Throws<InvalidDataException>(() => Build(string.Join('\n', Header, Row("SC01", 2000), Row("SC01", 4000, name: "不同名称")),
            new() { DeviceKind = ImportDeviceKind.Srm, SrmTransport = "S7" }, out _));
    }

    [Fact]
    public void Socket_MissingPort_RejectsIncompleteExport()
    {
        Assert.Throws<InvalidDataException>(() => Build(string.Join('\n', Header, Row("SC01", 2000)),
            new() { DeviceKind = ImportDeviceKind.Srm }, out _));
    }

    [Fact]
    public void WrongTable_ReportsMissingEquipmentColumns()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Build("stationno,userid,value,signaltype\n1001,127.0.0.1,0,15",
            new() { DeviceKind = ImportDeviceKind.Srm }, out _));
        Assert.Contains("equipmentnum", ex.Message);
    }

    [Fact]
    public void Conveyor_DefaultMode_PreservesExistingGenerator()
    {
        var config = Build("stationno,userid,value,signaltype,belong\n1001,127.0.0.1,0,15,1", new(), out _);
        Assert.Equal("Modbus", Assert.Single(config.Devices).Protocol);
        Assert.Single(config.Devices[0].Stations);
    }
}
