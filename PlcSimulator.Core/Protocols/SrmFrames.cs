namespace PlcSimulator.Core.Protocols;

/// <summary>
/// 堆垛机报文的编解码。纯函数：不碰 socket，也不持有状态。
/// </summary>
public static class SrmFrames
{
    /// <summary>状态区里最大字段偏移 + 1。</summary>
    private const int MinStatusBytes = 23;

    /// <summary>
    /// 解析 WCS 发来的 26 字节指令帧。长度不足时返回 false，不抛异常。
    /// </summary>
    public static bool TryParseCommand(ReadOnlySpan<byte> frame, out SrmCommand command)
    {
        command = default;

        if (frame.Length < SrmLayout.CommandFrameLength)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = frame[SrmLayout.CommandPayloadOffset..];
        command = new SrmCommand(
            Fork1TaskNum: ReadU16(payload, 2),
            Fork2TaskNum: ReadU16(payload, 0),
            CommandType: payload[4],
            ForkNo: payload[7],
            Fork1GoodsType: payload[5],
            Fork2GoodsType: payload[6],
            ActionPoint: payload[8],
            Aisle: payload[9],
            Row: payload[10],
            Column: payload[11],
            Cell: payload[12],
            Level: payload[13],
            Depth: payload[14],
            FireFlag: payload[19],
            Face1: payload[21],
            Face2: payload[22]);

        return true;
    }

    /// <summary>
    /// 按 WCS 的组帧方式生成一条 26 字节指令帧（前导两字节为 0，
    /// 对应 <c>NTIScBLL.cs:2250-2254</c> 的 <c>new byte[26]</c> + 偏移 2 拷贝）。
    /// 供命令行驱动器与测试扮演 WCS 时使用。
    /// </summary>
    public static void BuildCommandFrame(in SrmCommand command, Span<byte> destination)
    {
        if (destination.Length < SrmLayout.CommandFrameLength)
        {
            throw new ArgumentException(
                $"指令帧至少需要 {SrmLayout.CommandFrameLength} 字节，实际 {destination.Length} 字节。",
                nameof(destination));
        }

        destination[..SrmLayout.CommandFrameLength].Clear();
        Span<byte> payload = destination[SrmLayout.CommandPayloadOffset..];

        WriteU16(payload, 0, command.Fork2TaskNum);
        WriteU16(payload, 2, command.Fork1TaskNum);
        payload[4] = command.CommandType;
        payload[5] = command.Fork1GoodsType;
        payload[6] = command.Fork2GoodsType;
        payload[7] = command.ForkNo;
        payload[8] = command.ActionPoint;
        payload[9] = command.Aisle;
        payload[10] = command.Row;
        payload[11] = command.Column;
        payload[12] = command.Cell;
        payload[13] = command.Level;
        payload[14] = command.Depth;
        payload[19] = command.FireFlag;
        payload[21] = command.Face1;
        payload[22] = command.Face2;
    }

    /// <summary>
    /// 把一套状态值编码进状态区。偏移 0..2 是保留字节（WCS 的解析器从不读取），恒为 0。
    /// </summary>
    public static void WriteStatus(Span<byte> destination, in SrmStatusValues values)
    {
        if (destination.Length < MinStatusBytes)
        {
            throw new ArgumentException(
                $"状态区至少需要 {MinStatusBytes} 字节，实际 {destination.Length} 字节。",
                nameof(destination));
        }

        destination[0] = 0;
        destination[1] = 0;

        WriteU16(destination, 2, values.Fork2TaskNum);
        WriteU16(destination, 4, values.Fork1TaskNum);
        destination[6] = values.FunctionReport;
        destination[7] = values.FunctionMode;
        destination[8] = values.ForkStatus;
        destination[9] = values.ActiveFork;
        destination[10] = values.ActionPoint;
        destination[11] = values.Aisle;
        destination[12] = values.Row;
        destination[13] = values.Column;
        destination[14] = values.Cell;
        destination[15] = values.Level;
        destination[16] = values.Depth;
        destination[18] = values.PhotoSensor;
        destination[20] = values.Face1;
        destination[21] = values.Face2;
        destination[22] = values.FireAlarm;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    private static void WriteU16(Span<byte> buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }
}
