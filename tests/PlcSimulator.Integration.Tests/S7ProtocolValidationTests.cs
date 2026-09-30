using System.Buffers.Binary;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocol;
using PlcSimulator.Protocol.S7;

namespace PlcSimulator.Integration.Tests;

public class S7ProtocolValidationTests
{
    [Fact]
    public void MultiItemRead_OddPayload_AlignsFollowingItem()
    {
        var processor = Processor(out _);
        byte[] response = processor.Process(Job([4, 2, .. Item(1, 3), .. Item(1, 2, 3)], []));
        Assert.Equal(new byte[] { 255, 4, 0, 24, 1, 2, 3, 0, 255, 4, 0, 16, 4, 5 }, response[14..]);
    }

    [Fact]
    public void MultiItemWrite_TruncatedSecondPayload_DoesNotApplyFirstWrite()
    {
        var processor = Processor(out var session);
        byte[] response = processor.Process(Job([5, 2, .. Item(1, 3), .. Item(1, 2)],
            [0, 4, 0, 24, 1, 2, 3, 0, 0, 4, 0, 16, 4]));
        Assert.Equal(0x85, response[10]);
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void MultiItemWrite_OddPayload_ProcessesBothItems()
    {
        var processor = Processor(out var session);
        byte[] response = processor.Process(Job([5, 2, .. Item(1, 3), .. Item(1, 2)],
            [0, 4, 0, 24, 1, 2, 3, 0, 0, 4, 0, 16, 4, 5]));
        Assert.Equal(new byte[] { 255, 255 }, response[14..]);
        Assert.Equal(2, session.Writes);
    }

    [Theory]
    [InlineData(2, 1, 0, 0x0A)]
    [InlineData(1, 2, 255, 5)]
    public void Read_UnknownDbOrOutOfRange_ReturnsItemError(int db, int count, int offset, byte code)
    {
        var processor = Processor(out _);
        byte[] response = processor.Process(Job([4, 1, .. Item(db, count, offset)], []));
        Assert.Equal(code, response[14]);
        Assert.Equal(18, response.Length);
    }

    [Fact]
    public void Read_ExceedsNegotiatedPdu_RejectsRequest()
    {
        var processor = Processor(out _);
        processor.Process(Job([0xF0, 0, 0, 1, 0, 1, 0, 240], []));
        byte[] response = processor.Process(Job([4, 1, .. Item(1, 240)], []));
        Assert.Equal(0x85, response[10]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("overlap")]
    [InlineData("offset")]
    [InlineData("payload")]
    [InlineData("port")]
    public void Config_InvalidS7Mapping_ReportsError(string condition)
    {
        var device = new DeviceConfig
        {
            Id = "SC01", Ip = "127.0.0.1", Protocol = "S7", ProtocolType = "SRM", Srm = new(), S7 = new()
        };
        switch (condition)
        {
            case "missing": device.S7 = null; break;
            case "overlap": device.S7.Status.DbNumber = 60; break;
            case "offset": device.S7.Command.ByteOffset = -1; break;
            case "payload": device.S7.CommandPayloadOffset = 1; break;
            case "port": device.S7.Port = 0; break;
        }
        Assert.False(ConfigLoader.Validate(new() { Server = new() { Listen = [] }, Devices = [device] }).IsValid);
    }

    private static S7RequestProcessor Processor(out Session session)
    {
        session = new();
        var processor = new S7RequestProcessor(session);
        processor.Process(Job([0xF0, 0, 0, 1, 0, 1, 3, 0xC0], []));
        return processor;
    }

    private static byte[] Item(int db, int count, int offset = 0)
    {
        byte[] bytes = [0x12, 0x0A, 0x10, 2, 0, 0, 0, 0, 0x84, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)db);
        int bits = offset * 8;
        bytes[9] = (byte)(bits >> 16); bytes[10] = (byte)(bits >> 8); bytes[11] = (byte)bits;
        return bytes;
    }

    private static byte[] Job(byte[] parameters, byte[] data)
    {
        byte[] frame = new byte[10 + parameters.Length + data.Length];
        frame[0] = 0x32; frame[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), (ushort)parameters.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), (ushort)data.Length);
        parameters.CopyTo(frame, 10); data.CopyTo(frame, 10 + parameters.Length);
        return frame;
    }

    private sealed class Session : IS7DeviceSession
    {
        public string DeviceId => "test";
        public int Writes { get; private set; }
        public S7AccessResult ReadDb(ushort dbNumber, int byteOffset, Span<byte> destination)
        {
            if (dbNumber != 1) return S7AccessResult.ObjectNotFound;
            if (byteOffset + destination.Length > 256) return S7AccessResult.InvalidAddress;
            for (int i = 0; i < destination.Length; i++) destination[i] = (byte)(byteOffset + i + 1);
            return S7AccessResult.Success;
        }
        public S7AccessResult WriteDb(ushort dbNumber, int byteOffset, ReadOnlySpan<byte> source)
        {
            Writes++;
            return S7AccessResult.Success;
        }
    }
}
