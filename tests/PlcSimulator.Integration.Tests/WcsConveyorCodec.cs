using System.Text;

namespace PlcSimulator.Integration.Tests;

/// <summary>
/// WCS 输送线协议处理的 oracle：把 ConveryPLC 里那两段字节变换与字段解析原样重写一遍。
/// 模拟器一旦偏离 WCS 的约定，对拍就会失败；将来 WCS 升级时重新对齐这里即可发现协议漂移。
/// </summary>
public static class WcsConveyorCodec
{
    /// <summary>复刻 ConveryPLC.cs:419 —— 条码区的字序还原。</summary>
    public static byte[] UnswapBarcode(byte[] raw)
    {
        byte[] result = [.. raw];
        PairReverse(result, 0);
        return result;
    }

    public static void PairReverse(byte[] buffer, int from)
    {
        for (int i = from; i + 1 < buffer.Length; i += 2)
        {
            (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
        }
    }

    public static ushort ReadU16(byte[] buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    public static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    /// <summary>复刻 BindStationNTI 里的条码解析：先还原字序，再去空格与尾部填充。</summary>
    public static string ReadBarcode(byte[] buffer, int offset, int length)
    {
        byte[] raw = buffer[offset..(offset + length)];
        byte[] restored = UnswapBarcode(raw);
        return Encoding.ASCII.GetString(restored).Replace(" ", string.Empty).TrimEnd('\0').Trim('\r');
    }

    /// <summary>复刻 FunctionMode(value, bits) 的按位展开，下标 0 是最低位。</summary>
    public static int[] FunctionMode(int value, int bitCount)
    {
        int[] bits = new int[bitCount];
        for (int i = 0; i < bitCount; i++)
        {
            bits[i] = (value >> i) & 1;
        }

        return bits;
    }
}
