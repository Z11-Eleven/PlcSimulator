using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Core.Tests;

public class SrmFramesTests
{
    /// <summary>
    /// 状态区字段偏移，逐条对照 WCS 的 <c>BindScInfoNTI</c>（NTIScBLL.cs:3134-3182）。
    /// 这张表是模拟器与 WCS 之间最容易悄悄漂移的地方，固化成断言。
    /// </summary>
    [Theory]
    [InlineData("reserved", 0, 1)]
    [InlineData("fork2tasknum", 2, 2)]
    [InlineData("fork1tasknum", 4, 2)]
    [InlineData("report", 6, 1)]
    [InlineData("mode", 7, 1)]
    [InlineData("forkstatus", 8, 1)]
    [InlineData("activefork", 9, 1)]
    [InlineData("actionpoint", 10, 1)]
    [InlineData("aisle", 11, 1)]
    [InlineData("row", 12, 1)]
    [InlineData("column", 13, 1)]
    [InlineData("cell", 14, 1)]
    [InlineData("level", 15, 1)]
    [InlineData("depth", 16, 1)]
    [InlineData("photo", 18, 1)]
    [InlineData("face1", 20, 1)]
    [InlineData("face2", 21, 1)]
    [InlineData("fire", 22, 1)]
    public void SrmLayout_StatusField_HasWcsOffset(string name, int offset, int size)
    {
        Assert.True(SrmLayout.TryGetStatusField(name, out FieldDescriptor field));
        Assert.Equal(offset, field.ByteOffset);
        Assert.Equal(size, field.SizeInBytes);
    }

    /// <summary>指令帧负载字段偏移，对照 WCS 的 <c>SendCodeNTI</c>（NTIScBLL.cs:2174-2245）。</summary>
    [Theory]
    [InlineData("fork2tasknum", 0, 2)]
    [InlineData("fork1tasknum", 2, 2)]
    [InlineData("commandtype", 4, 1)]
    [InlineData("fork1goodstype", 5, 1)]
    [InlineData("fork2goodstype", 6, 1)]
    [InlineData("forkno", 7, 1)]
    [InlineData("actionpoint", 8, 1)]
    [InlineData("aisle", 9, 1)]
    [InlineData("row", 10, 1)]
    [InlineData("column", 11, 1)]
    [InlineData("cell", 12, 1)]
    [InlineData("level", 13, 1)]
    [InlineData("depth", 14, 1)]
    [InlineData("fireflag", 19, 1)]
    [InlineData("face1", 21, 1)]
    [InlineData("face2", 22, 1)]
    public void SrmLayout_CommandField_HasWcsOffset(string name, int offset, int size)
    {
        Assert.True(SrmLayout.TryGetCommandField(name, out FieldDescriptor field));
        Assert.Equal(offset, field.ByteOffset);
        Assert.Equal(size, field.SizeInBytes);
    }

    /// <summary>
    /// 手工按 WCS 的组帧规则摆一帧字节（而不是调 <see cref="SrmFrames.BuildCommandFrame"/>），
    /// 免得实现与断言互相印证。
    /// </summary>
    [Fact]
    public void TryParseCommand_GivenFullFrame_ParsesEveryFieldAtItsOffset()
    {
        byte[] frame = new byte[SrmLayout.CommandFrameLength];

        frame[2] = 0x00; frame[3] = 0x11;   // 工位2任务号 = 17
        frame[4] = 0x12; frame[5] = 0x34;   // 工位1任务号 = 4660
        frame[6] = SrmCommandType.GetC;     // 指令类型 = 112 取货
        frame[7] = 5;                       // 货叉1货物类型
        frame[8] = 6;                       // 货叉2货物类型
        frame[9] = 1;                       // 货叉号
        frame[10] = 2;                      // 动作点
        frame[11] = 1;                      // 巷道
        frame[12] = 1;                      // 排
        frame[13] = 46;                     // 列
        frame[14] = 1;                      // 货格
        frame[15] = 6;                      // 层
        frame[16] = 1;                      // 深
        frame[21] = 1;                      // 火警标志
        frame[23] = (byte)'A';              // 面1
        frame[24] = (byte)'B';              // 面2

        Assert.True(SrmFrames.TryParseCommand(frame, out SrmCommand command));

        Assert.Equal(4660, command.Fork1TaskNum);
        Assert.Equal(17, command.Fork2TaskNum);
        Assert.Equal(SrmCommandType.GetC, command.CommandType);
        Assert.Equal(5, command.Fork1GoodsType);
        Assert.Equal(6, command.Fork2GoodsType);
        Assert.Equal(1, command.ForkNo);
        Assert.Equal(2, command.ActionPoint);
        Assert.Equal(1, command.Aisle);
        Assert.Equal(1, command.Row);
        Assert.Equal(46, command.Column);
        Assert.Equal(1, command.Cell);
        Assert.Equal(6, command.Level);
        Assert.Equal(1, command.Depth);
        Assert.Equal(1, command.FireFlag);
        Assert.Equal((byte)'A', command.Face1);
        Assert.Equal((byte)'B', command.Face2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(SrmLayout.CommandFrameLength - 1)]
    public void TryParseCommand_GivenShortFrame_ReturnsFalse(int length)
    {
        byte[] frame = new byte[length];

        Assert.False(SrmFrames.TryParseCommand(frame, out _));
    }

    [Fact]
    public void BuildCommandFrame_ThenParse_RoundTripsEveryField()
    {
        SrmCommand original = new(
            Fork1TaskNum: 4660,
            Fork2TaskNum: 17,
            CommandType: SrmCommandType.PutC,
            ForkNo: 2,
            Fork1GoodsType: 5,
            Fork2GoodsType: 6,
            ActionPoint: 3,
            Aisle: 1,
            Row: 2,
            Column: 46,
            Cell: 1,
            Level: 6,
            Depth: 2,
            FireFlag: 1,
            Face1: (byte)'A',
            Face2: (byte)'B');

        byte[] frame = new byte[SrmLayout.CommandFrameLength];
        SrmFrames.BuildCommandFrame(original, frame);

        Assert.Equal(0, frame[0]);
        Assert.Equal(0, frame[1]);

        Assert.True(SrmFrames.TryParseCommand(frame, out SrmCommand parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void BuildCommandFrame_GivenTooSmallDestination_Throws()
    {
        byte[] tooSmall = new byte[SrmLayout.CommandFrameLength - 1];

        Assert.Throws<ArgumentException>(() => SrmFrames.BuildCommandFrame(default, tooSmall));
    }

    [Fact]
    public void WriteStatus_WritesEveryFieldAtItsOffset()
    {
        byte[] area = new byte[SrmLayout.StatusAreaLength];

        SrmStatusValues values = new(
            Fork1TaskNum: 4660,
            Fork2TaskNum: 17,
            FunctionReport: SrmFunctionReport.GetDone,
            FunctionMode: SrmFunctionMode.Standby,
            ForkStatus: SrmForkStatusBits.Fork1,
            ActiveFork: 1,
            ActionPoint: 2,
            Aisle: 1,
            Row: 1,
            Column: 46,
            Cell: 1,
            Level: 6,
            Depth: 1,
            PhotoSensor: 1,
            Face1: (byte)'A',
            Face2: (byte)'B',
            FireAlarm: 0);

        SrmFrames.WriteStatus(area, values);

        Assert.Equal(0x00, area[0]);
        Assert.Equal(0x00, area[1]);
        Assert.Equal(17, (area[2] << 8) | area[3]);
        Assert.Equal(4660, (area[4] << 8) | area[5]);
        Assert.Equal(SrmFunctionReport.GetDone, area[6]);
        Assert.Equal(SrmFunctionMode.Standby, area[7]);
        Assert.Equal(SrmForkStatusBits.Fork1, area[8]);
        Assert.Equal(1, area[9]);
        Assert.Equal(2, area[10]);
        Assert.Equal(1, area[11]);
        Assert.Equal(1, area[12]);
        Assert.Equal(46, area[13]);
        Assert.Equal(1, area[14]);
        Assert.Equal(6, area[15]);
        Assert.Equal(1, area[16]);
        Assert.Equal(1, area[18]);
        Assert.Equal((byte)'A', area[20]);
        Assert.Equal((byte)'B', area[21]);
        Assert.Equal(0, area[22]);
    }

    [Fact]
    public void WriteStatus_GivenTooSmallArea_Throws()
    {
        byte[] tooSmall = new byte[22];

        Assert.Throws<ArgumentException>(() => SrmFrames.WriteStatus(tooSmall, default));
    }

    [Fact]
    public void SrmFunctionMode_Standby_IsAutoPlusReady()
    {
        // WCS 判设备可用的条件是 [0]==1 && [4]==1 && [7]==0，正常待机即 0x11。
        Assert.Equal(0x11, SrmFunctionMode.Standby);

        Assert.Equal(SrmFunctionMode.Auto, SrmFunctionMode.Standby & SrmFunctionMode.Auto);
        Assert.Equal(SrmFunctionMode.Ready, SrmFunctionMode.Standby & SrmFunctionMode.Ready);
        Assert.Equal(0, SrmFunctionMode.Standby & SrmFunctionMode.Fault);
    }

    [Theory]
    [InlineData(1, 0, 0, 1)]
    [InlineData(2, 0, 0, 2)]
    [InlineData(3, 0, 0, 3)]
    [InlineData(0, 4660, 0, 1)]
    [InlineData(0, 0, 17, 2)]
    [InlineData(0, 4660, 17, 3)]
    [InlineData(0, 0, 0, 0)]
    public void SrmCommand_ResolveForkNo_UsesForkNoThenTaskNums(
        byte forkNo, ushort fork1TaskNum, ushort fork2TaskNum, int expected)
    {
        SrmCommand command = new(
            Fork1TaskNum: fork1TaskNum,
            Fork2TaskNum: fork2TaskNum,
            CommandType: SrmCommandType.GetC,
            ForkNo: forkNo,
            Fork1GoodsType: 0,
            Fork2GoodsType: 0,
            ActionPoint: 0,
            Aisle: 0,
            Row: 0,
            Column: 0,
            Cell: 0,
            Level: 0,
            Depth: 0,
            FireFlag: 0,
            Face1: 0,
            Face2: 0);

        Assert.Equal(expected, command.ResolveForkNo());
    }

    [Fact]
    public void SrmCommand_IsClear_OnlyForNoFunc()
    {
        Assert.True(Command(SrmCommandType.NoFunc).IsClear);
        Assert.False(Command(SrmCommandType.GetC).IsClear);
        Assert.False(Command(SrmCommandType.PutC).IsClear);
    }

    [Fact]
    public void SrmCommand_IsAvoidance_RequiresPosGetWithTask1199()
    {
        Assert.True(Command(SrmCommandType.PosGet, fork1TaskNum: SrmCommandType.AvoidanceTaskNum).IsAvoidance);
        Assert.False(Command(SrmCommandType.PosGet, fork1TaskNum: 100).IsAvoidance);
        Assert.False(Command(SrmCommandType.GetC, fork1TaskNum: SrmCommandType.AvoidanceTaskNum).IsAvoidance);
    }

    private static SrmCommand Command(
        byte commandType,
        byte forkNo = 0,
        ushort fork1TaskNum = 0,
        ushort fork2TaskNum = 0)
        => new(
            Fork1TaskNum: fork1TaskNum,
            Fork2TaskNum: fork2TaskNum,
            CommandType: commandType,
            ForkNo: forkNo,
            Fork1GoodsType: 0,
            Fork2GoodsType: 0,
            ActionPoint: 0,
            Aisle: 0,
            Row: 0,
            Column: 0,
            Cell: 0,
            Level: 0,
            Depth: 0,
            FireFlag: 0,
            Face1: 0,
            Face2: 0);
}
