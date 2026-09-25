using PlcSimulator.Core.Registers;

namespace PlcSimulator.Protocol.Modbus.Tests;

public class ModbusRequestProcessorTests
{
    private const int Mbap = ModbusFrames.MbapHeaderLength;

    private sealed record Fixture(ModbusRequestProcessor Processor, DeviceRegisterSpace Space);

    private static Fixture CreateFixture(byte slaveId = 1, bool acceptAnySlaveId = true)
    {
        var space = new DeviceRegisterSpace("dev", [new RegisterBlock("g1", 0, 256)]);
        var resolver = new StaticDeviceResolver(
            [new ModbusDeviceBinding("dev", slaveId, space)],
            acceptAnySlaveId);
        return new Fixture(new ModbusRequestProcessor(resolver), space);
    }

    private static ModbusRequest Read(ushort address, ushort quantity, ModbusFunctionCode functionCode = ModbusFunctionCode.ReadHoldingRegisters)
        => new(1, 1, functionCode, address, quantity, 0, 0, []);

    private static ModbusRequest Write(ushort address, byte[] payload, ModbusFunctionCode functionCode = ModbusFunctionCode.WriteMultipleRegisters)
        => new(1, 1, functionCode, address, (ushort)(payload.Length / 2), 0, 0, payload);

    private static bool IsException(byte[] frame) => (frame[Mbap] & 0x80) != 0;

    private static ModbusExceptionCode ExceptionCode(byte[] frame) => (ModbusExceptionCode)frame[Mbap + 1];

    private static byte[] ReadData(byte[] frame)
    {
        var data = new byte[frame[Mbap + 1]];
        Array.Copy(frame, Mbap + 2, data, 0, data.Length);
        return data;
    }

    [Fact]
    public void ReadHoldingRegisters_MappedAddress_ReturnsStoredBytes()
    {
        Fixture fixture = CreateFixture();
        fixture.Space.TryWriteBytes(4, [0x12, 0x34]);

        byte[] response = fixture.Processor.Process(Read(2, 1));

        Assert.False(IsException(response));
        Assert.Equal(ModbusFunctionCode.ReadHoldingRegisters, (ModbusFunctionCode)response[Mbap]);
        Assert.Equal([0x12, 0x34], ReadData(response));
    }

    [Fact]
    public void ReadHoldingRegisters_UnmappedAddress_ZeroFills()
    {
        Fixture fixture = CreateFixture();

        // WCS 会读超出配置块的地址，必须零填充而不是异常。
        byte[] response = fixture.Processor.Process(Read(1000, 2));

        Assert.False(IsException(response));
        Assert.Equal([0x00, 0x00, 0x00, 0x00], ReadData(response));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public void ReadHoldingRegisters_QuantityOutOfRange_ReturnsIllegalDataValue(ushort quantity)
    {
        Fixture fixture = CreateFixture();

        byte[] response = fixture.Processor.Process(Read(0, quantity));

        Assert.True(IsException(response));
        Assert.Equal(ModbusExceptionCode.IllegalDataValue, ExceptionCode(response));
    }

    [Fact]
    public void ReadInputRegisters_IsSupported()
    {
        Fixture fixture = CreateFixture();
        fixture.Space.TryWriteBytes(0, [0x00, 0x05]);

        byte[] response = fixture.Processor.Process(Read(0, 1, ModbusFunctionCode.ReadInputRegisters));

        Assert.False(IsException(response));
        Assert.Equal(ModbusFunctionCode.ReadInputRegisters, (ModbusFunctionCode)response[Mbap]);
        Assert.Equal([0x00, 0x05], ReadData(response));
    }

    [Fact]
    public void WriteSingleRegister_ThenRead_ReturnsWrittenValue()
    {
        Fixture fixture = CreateFixture();

        byte[] writeResponse = fixture.Processor.Process(
            new ModbusRequest(1, 1, ModbusFunctionCode.WriteSingleRegister, 4, 1, 0, 0, [0x04, 0xD2]));

        Assert.False(IsException(writeResponse));

        byte[] readResponse = fixture.Processor.Process(Read(4, 1));
        Assert.Equal([0x04, 0xD2], ReadData(readResponse));
    }

    [Fact]
    public void WriteMultipleRegisters_ThenRead_ReturnsWrittenValues()
    {
        Fixture fixture = CreateFixture();

        byte[] response = fixture.Processor.Process(Write(0, [0x00, 0x01, 0x00, 0x02]));

        Assert.False(IsException(response));
        Assert.Equal(ModbusFunctionCode.WriteMultipleRegisters, (ModbusFunctionCode)response[Mbap]);
        Assert.Equal(0, response[Mbap + 1]);
        Assert.Equal(0, response[Mbap + 2]);
        Assert.Equal(2, ModbusFrames.ReadUInt16BigEndian(response, Mbap + 3));
    }

    [Fact]
    public void WriteMultipleRegisters_OutsideConfiguredBlocks_ReturnsIllegalDataAddress()
    {
        Fixture fixture = CreateFixture();

        byte[] response = fixture.Processor.Process(Write(500, [0x00, 0x01]));

        Assert.True(IsException(response));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ExceptionCode(response));
    }

    [Fact]
    public void WriteMultipleRegisters_ByteCountMismatch_ReturnsIllegalDataValue()
    {
        Fixture fixture = CreateFixture();

        // quantity 声明 3 个寄存器，却只给了 2 字节。
        var request = new ModbusRequest(1, 1, ModbusFunctionCode.WriteMultipleRegisters, 0, 3, 0, 0, [0x00, 0x01]);

        byte[] response = fixture.Processor.Process(request);

        Assert.True(IsException(response));
        Assert.Equal(ModbusExceptionCode.IllegalDataValue, ExceptionCode(response));
    }

    [Fact]
    public void UnknownFunctionCode_ReturnsIllegalFunction()
    {
        Fixture fixture = CreateFixture();
        var request = new ModbusRequest(1, 1, (ModbusFunctionCode)0x42, 0, 1, 0, 0, []);

        byte[] response = fixture.Processor.Process(request);

        Assert.True(IsException(response));
        Assert.Equal(0xC2, response[Mbap]);
        Assert.Equal(ModbusExceptionCode.IllegalFunction, ExceptionCode(response));
    }

    [Fact]
    public void UnknownSlaveId_WhenStrict_ReturnsGatewayFailure()
    {
        Fixture fixture = CreateFixture(slaveId: 7, acceptAnySlaveId: false);

        byte[] response = fixture.Processor.Process(Read(0, 1));

        Assert.True(IsException(response));
        Assert.Equal(ModbusExceptionCode.GatewayTargetDeviceFailedToRespond, ExceptionCode(response));
    }

    [Fact]
    public void UnknownSlaveId_WhenLenient_StillServesSingleDevice()
    {
        // 闭源 DLL 可能把站台号当作单元标识发出来，单设备端点需要容忍。
        Fixture fixture = CreateFixture(slaveId: 7, acceptAnySlaveId: true);

        byte[] response = fixture.Processor.Process(Read(0, 1));

        Assert.False(IsException(response));
    }

    [Fact]
    public void ReadCoils_ReturnsBitPackedData()
    {
        Fixture fixture = CreateFixture();
        fixture.Space.TryWriteBytes(0, [0b0000_0101, 0b0000_0010]);

        byte[] response = fixture.Processor.Process(Read(0, 16, ModbusFunctionCode.ReadCoils));

        Assert.False(IsException(response));
        Assert.Equal([0b0000_0101, 0b0000_0010], ReadData(response));
    }

    [Fact]
    public void ReadCoils_QuantityNotMultipleOfEight_ReturnsSinglePackedByte()
    {
        Fixture fixture = CreateFixture();
        fixture.Space.TryWriteBytes(0, [0b0000_0101]);

        byte[] response = fixture.Processor.Process(Read(0, 8, ModbusFunctionCode.ReadCoils));

        Assert.False(IsException(response));
        Assert.Equal([0b0000_0101], ReadData(response));
    }

    [Fact]
    public void WriteSingleCoil_On_SetsBitWithoutTouchingNeighbours()
    {
        Fixture fixture = CreateFixture();
        fixture.Space.TryWriteBytes(0, [0b0000_0001]);

        byte[] response = fixture.Processor.Process(
            new ModbusRequest(1, 1, ModbusFunctionCode.WriteSingleCoil, 2, 1, 0, 0, [0xFF, 0x00]));

        Assert.False(IsException(response));

        byte[] raw = new byte[1];
        fixture.Space.TryReadBytes(0, raw);
        Assert.Equal(0b0000_0101, raw[0]);
    }

    [Fact]
    public void WriteSingleCoil_InvalidValue_ReturnsIllegalDataValue()
    {
        Fixture fixture = CreateFixture();

        byte[] response = fixture.Processor.Process(
            new ModbusRequest(1, 1, ModbusFunctionCode.WriteSingleCoil, 2, 1, 0, 0, [0x12, 0x34]));

        Assert.True(IsException(response));
        Assert.Equal(ModbusExceptionCode.IllegalDataValue, ExceptionCode(response));
    }

    [Fact]
    public void Response_EchoesTransactionIdAndUnitId()
    {
        Fixture fixture = CreateFixture();
        var request = new ModbusRequest(0xBEEF, 3, ModbusFunctionCode.ReadHoldingRegisters, 0, 1, 0, 0, []);

        byte[] response = fixture.Processor.Process(request);

        Assert.True(ModbusFrames.TryReadHeader(response, out ushort transactionId, out _, out _));
        Assert.Equal(0xBEEF, transactionId);
        Assert.Equal(3, response[6]);
    }
}
