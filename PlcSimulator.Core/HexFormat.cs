using System.Text;

namespace PlcSimulator.Core;

/// <summary>十六进制与 ASCII 显示工具，供报文日志与 GUI 复用。</summary>
public static class HexFormat
{
    private const string Digits = "0123456789ABCDEF";

    /// <summary>输出形如 "00 01 04 D2" 的空格分隔十六进制串。超过 maxBytes 时截断并追加省略标记。</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes, int maxBytes = int.MaxValue)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        int shown = Math.Min(bytes.Length, maxBytes);
        var builder = new StringBuilder((shown * 3) + 4);

        for (int i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            byte value = bytes[i];
            builder.Append(Digits[value >> 4]).Append(Digits[value & 0x0F]);
        }

        if (shown < bytes.Length)
        {
            builder.Append(" ...");
        }

        return builder.ToString();
    }

    /// <summary>把字节解释为 ASCII，不可打印字符显示为 '.'。</summary>
    public static string ToAscii(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(bytes.Length);
        foreach (byte value in bytes)
        {
            builder.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
        }

        return builder.ToString();
    }
}
