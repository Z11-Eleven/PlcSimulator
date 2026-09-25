using PlcSimulator.Core.Registers;

namespace PlcSimulator.Core.Tests;

public class DeviceRegisterSpaceTests
{
    private static DeviceRegisterSpace CreateSpace(params (string Group, int Offset, int Length)[] blocks)
        => new("device-1", blocks.Select(b => new RegisterBlock(b.Group, b.Offset, b.Length)));

    [Fact]
    public void TryReadRegisters_WithinBlock_ReturnsStoredBytes()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 32));
        space.TryWriteBytes(4, [0x12, 0x34]);

        byte[] buffer = new byte[2];

        Assert.True(space.TryReadRegisters(2, 1, buffer));
        Assert.Equal([0x12, 0x34], buffer);
    }

    [Fact]
    public void TryReadRegisters_BeyondConfiguredBlocks_ZeroFills()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 4));
        byte[] buffer = new byte[4];

        // WCS 会读超出配置块范围的地址，这是正常行为，必须零填充而不是报错。
        Assert.True(space.TryReadRegisters(100, 2, buffer));
        Assert.Equal([0x00, 0x00, 0x00, 0x00], buffer);
    }

    [Fact]
    public void TryReadRegisters_AcrossBlockGap_ZeroFillsGap()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 4), ("g2", 8, 4));
        space.TryWriteBytes(0, [0xAA, 0xBB, 0xCC, 0xDD]);
        space.TryWriteBytes(8, [0x11, 0x22, 0x33, 0x44]);

        byte[] buffer = new byte[12];

        Assert.True(space.TryReadRegisters(0, 6, buffer));
        Assert.Equal(
            [0xAA, 0xBB, 0xCC, 0xDD, 0x00, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44],
            buffer);
    }

    [Fact]
    public void TryReadRegisters_DestinationTooSmall_ReturnsFalse()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 32));
        byte[] buffer = new byte[1];

        Assert.False(space.TryReadRegisters(0, 1, buffer));
    }

    [Fact]
    public void TryWriteRegisters_OutsideConfiguredBlocks_ReturnsFalse()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 4));

        Assert.False(space.TryWriteRegisters(100, [0x01, 0x02]));
    }

    [Fact]
    public void TryWriteRegisters_PartiallyOutsideBlock_ReturnsFalse()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 4));

        // 范围 [2,6) 只有前 2 字节落在块内，必须整体拒绝，避免静默丢数据。
        Assert.False(space.TryWriteRegisters(1, [0x01, 0x02, 0x03, 0x04]));
    }

    [Fact]
    public void TryWriteRegisters_AcrossTwoAdjacentBlocks_Succeeds()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 4), ("g2", 4, 4));

        Assert.True(space.TryWriteRegisters(0, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]));

        byte[] buffer = new byte[8];
        Assert.True(space.TryReadRegisters(0, 4, buffer));
        Assert.Equal([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], buffer);
    }

    [Fact]
    public void TryWriteRegisters_OddByteCount_ReturnsFalse()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 32));

        Assert.False(space.TryWriteRegisters(0, [0x01, 0x02, 0x03]));
    }

    [Fact]
    public void TryWriteRegisters_EmptyPayload_ReturnsFalse()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 32));

        Assert.False(space.TryWriteRegisters(0, []));
    }

    [Fact]
    public void Constructor_OverlappingBlocks_Throws()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => CreateSpace(("g1", 0, 8), ("g2", 4, 8)));

        Assert.Contains("重叠", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TakeDirtyRanges_AfterWrites_ReturnsMergedRanges()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 64));
        space.TryWriteBytes(0, [0x01, 0x02]);
        space.TryWriteBytes(2, [0x03, 0x04]);
        space.TryWriteBytes(32, [0x05, 0x06]);

        List<(int Start, int End)> ranges = space.TakeDirtyRanges();

        Assert.Equal(2, ranges.Count);
        Assert.Equal((0, 4), ranges[0]);
        Assert.Equal((32, 34), ranges[1]);
        Assert.Empty(space.TakeDirtyRanges());
    }

    [Fact]
    public void Version_AfterWrite_Increments()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 32));
        long before = space.Version;
        space.TryWriteBytes(0, [0x01, 0x02]);

        Assert.True(space.Version > before);
    }

    [Fact]
    public void TryClearRange_ZeroesBytes()
    {
        DeviceRegisterSpace space = CreateSpace(("g1", 0, 8));
        space.TryWriteBytes(0, [0xFF, 0xFF, 0xFF, 0xFF]);

        Assert.True(space.TryClearRange(0, 4));

        byte[] buffer = new byte[4];
        space.TryReadBytes(0, buffer);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], buffer);
    }
}
