namespace PlcSimulator.Protocol.Modbus.Tests;

public class ModbusFramesTests
{
    [Fact]
    public void TryReadHeader_ValidHeader_ParsesAllFields()
    {
        byte[] header = [0x00, 0x2A, 0x00, 0x00, 0x00, 0x06, 0x01];

        Assert.True(ModbusFrames.TryReadHeader(header, out ushort transactionId, out ushort protocolId, out ushort length));
        Assert.Equal(0x2A, transactionId);
        Assert.Equal(0, protocolId);
        Assert.Equal(6, length);
    }

    [Fact]
    public void TryReadHeader_ShortBuffer_ReturnsFalse()
    {
        byte[] header = [0x00, 0x2A, 0x00, 0x00, 0x00];

        Assert.False(ModbusFrames.TryReadHeader(header, out _, out _, out _));
    }

    [Fact]
    public void BuildFrame_ThenReadHeader_RoundTripsFields()
    {
        byte[] frame = ModbusFrames.BuildFrame(0x1234, 0x07, [0x03, 0x00, 0x00, 0x00, 0x02]);

        Assert.True(ModbusFrames.TryReadHeader(frame, out ushort transactionId, out ushort protocolId, out ushort length));
        Assert.Equal(0x1234, transactionId);
        Assert.Equal(0, protocolId);
        Assert.Equal(6, length);
        Assert.Equal(0x07, frame[6]);
        Assert.Equal(5, frame.Length - ModbusFrames.MbapHeaderLength);
    }

    [Fact]
    public void TryDecodePdu_ReadHoldingRegisters_ParsesAddressAndQuantity()
    {
        byte[] pdu = [0x03, 0x00, 0x10, 0x00, 0x20];

        Assert.True(ModbusFrames.TryDecodePdu(1, 1, pdu, out ModbusRequest request, out _));
        Assert.Equal(ModbusFunctionCode.ReadHoldingRegisters, request.FunctionCode);
        Assert.Equal(16, request.Address);
        Assert.Equal(32, request.Quantity);
        Assert.False(request.IsWrite);
    }

    [Fact]
    public void TryDecodePdu_WriteMultipleRegisters_ParsesPayload()
    {
        byte[] pdu = [0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x12, 0x34, 0x56, 0x78];

        Assert.True(ModbusFrames.TryDecodePdu(1, 1, pdu, out ModbusRequest request, out _));
        Assert.Equal(ModbusFunctionCode.WriteMultipleRegisters, request.FunctionCode);
        Assert.Equal(2, request.Quantity);
        Assert.Equal([0x12, 0x34, 0x56, 0x78], request.Payload);
        Assert.True(request.IsWrite);
    }

    [Fact]
    public void TryDecodePdu_ReadWriteMultipleRegisters_ParsesBothHalves()
    {
        byte[] pdu = [0x17, 0x00, 0x01, 0x00, 0x02, 0x00, 0x0A, 0x00, 0x01, 0x02, 0xAB, 0xCD];

        Assert.True(ModbusFrames.TryDecodePdu(1, 1, pdu, out ModbusRequest request, out _));
        Assert.Equal(1, request.Address);
        Assert.Equal(2, request.Quantity);
        Assert.Equal(10, request.WriteAddress);
        Assert.Equal(1, request.WriteQuantity);
        Assert.Equal([0xAB, 0xCD], request.Payload);
    }

    [Fact]
    public void TryDecodePdu_UnknownFunction_ReturnsIllegalFunction()
    {
        byte[] pdu = [0x42, 0x00, 0x00];

        Assert.False(ModbusFrames.TryDecodePdu(1, 1, pdu, out _, out ModbusExceptionCode error));
        Assert.Equal(ModbusExceptionCode.IllegalFunction, error);
    }

    [Theory]
    [InlineData(new byte[] { 0x03, 0x00, 0x00 })]                     // 读请求长度不足
    [InlineData(new byte[] { 0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x12 })] // 字节数与声明不符
    public void TryDecodePdu_MalformedPdu_ReturnsIllegalDataValue(byte[] pdu)
    {
        Assert.False(ModbusFrames.TryDecodePdu(1, 1, pdu, out _, out ModbusExceptionCode error));
        Assert.Equal(ModbusExceptionCode.IllegalDataValue, error);
    }

    [Fact]
    public void BuildException_SetsHighBitOnFunctionCode()
    {
        byte[] frame = ModbusFrames.BuildException(1, 1, ModbusFunctionCode.ReadHoldingRegisters, ModbusExceptionCode.IllegalDataAddress);

        Assert.Equal(0x83, frame[ModbusFrames.MbapHeaderLength]);
        Assert.Equal(0x02, frame[ModbusFrames.MbapHeaderLength + 1]);
    }

    [Fact]
    public void BuildReadResponse_WritesByteCountAndData()
    {
        byte[] frame = ModbusFrames.BuildReadResponse(1, 1, ModbusFunctionCode.ReadHoldingRegisters, [0x04, 0xD2]);

        Assert.Equal(0x03, frame[ModbusFrames.MbapHeaderLength]);
        Assert.Equal(0x02, frame[ModbusFrames.MbapHeaderLength + 1]);
        Assert.Equal(0x04, frame[ModbusFrames.MbapHeaderLength + 2]);
        Assert.Equal(0xD2, frame[ModbusFrames.MbapHeaderLength + 3]);
    }
}
