using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Protocols;

namespace PlcSimulator.Core.Tests;

public class ByteOrderPolicyTests
{
    /// <summary>
    /// WCS 源码里字交换的原始写法（ConveryPLC.cs:419 / 871、ScPLC.cs:163 同构）：
    /// <c>GroupBy(i / 2).SelectMany(x => [x.Last().x, x.First().x])</c>。
    /// 这里原样重写作为 oracle，任何策略改动都会被对拍发现。
    /// </summary>
    private static byte[] WcsPairReverse(byte[] source)
        => [.. source
            .Select((value, index) => new { value, index })
            .GroupBy(x => x.index / 2)
            .SelectMany(x => new byte[] { x.Last().value, x.First().value })];

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void Transform_SwapFromOffset_MatchesWcsPairReverseTail(int from)
    {
        byte[] data = [.. Enumerable.Range(0, 30).Select(i => (byte)i)];
        byte[] expected = [.. data];
        byte[] reversedTail = WcsPairReverse([.. data[from..]]);
        Array.Copy(reversedTail, 0, expected, from, reversedTail.Length);

        var policy = new ByteOrderPolicy { SwapFromByteOffset = from };
        byte[] actual = [.. data];
        policy.Transform(actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Transform_SwapFromOffset_LeavesHeadUntouched()
    {
        // 输送线整块写：字节 [0..6) 原样，[6..] 起反序（ConveryPLC.cs:867-872）。
        var policy = new ByteOrderPolicy { SwapFromByteOffset = 6 };
        byte[] buffer = [0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x12, 0x34];

        policy.Transform(buffer);

        Assert.Equal([0x00, 0x01, 0x00, 0x02, 0x00, 0x03], buffer[..6]);
        Assert.Equal([0x04, 0x00, 0x34, 0x12], buffer[6..]);
    }

    [Fact]
    public void Transform_ExtraRange_ReversesOnlyThatRange()
    {
        // 输送线读：只有条码区间 [12,28) 需要还原（ConveryPLC.cs:419）。
        var policy = new ByteOrderPolicy { ExtraPairUnswapRanges = [[12, 28]] };
        byte[] buffer = new byte[32];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)i;
        }

        policy.Transform(buffer);

        Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), buffer[..12]);
        Assert.Equal(Enumerable.Range(12, 16).Select(i => (byte)((i % 2 == 0) ? i + 1 : i - 1)), buffer[12..28]);
        Assert.Equal(Enumerable.Range(28, 4).Select(i => (byte)i), buffer[28..]);
    }

    [Fact]
    public void Transform_BarcodeRange_RestoresAscii()
    {
        // 模拟器内部按正序存条码，发给 WCS 的线路字节应成对互换，
        // WCS 再交换一次还原成可读 ASCII。
        var policy = new ByteOrderPolicy { ExtraPairUnswapRanges = [[12, 28]] };

        byte[] internalBytes = new byte[32];
        "ABCDEFGHIJKLMNOP"u8.CopyTo(internalBytes.AsSpan(12));

        byte[] wire = [.. internalBytes];
        policy.Transform(wire);

        Assert.Equal("BADCFEHGJILKNMPO"u8.ToArray(), wire[12..28]);
        Assert.Equal("ABCDEFGHIJKLMNOP"u8.ToArray(), WcsPairReverse(wire[12..28]));
    }

    [Fact]
    public void Transform_AppliedTwice_RestoresOriginal()
    {
        var policy = new ByteOrderPolicy { SwapFromByteOffset = 6, ExtraPairUnswapRanges = [[12, 28]] };
        byte[] original = [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 7))];
        byte[] buffer = [.. original];

        policy.Transform(buffer);
        policy.Transform(buffer);

        Assert.Equal(original, buffer);
    }

    [Fact]
    public void Identity_LeavesBytesUnchanged()
    {
        byte[] original = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        byte[] buffer = [.. original];

        ByteOrderPolicy.Identity.Transform(buffer);

        Assert.Equal(original, buffer);
        Assert.True(ByteOrderPolicy.Identity.IsIdentity);
    }

    [Fact]
    public void Transform_RandomEvenLengthBuffers_MatchesWcsLinqImplementation()
    {
        var random = new Random(20260921);

        for (int trial = 0; trial < 1000; trial++)
        {
            int length = random.Next(2, 16) * 2;
            byte[] data = new byte[length];
            random.NextBytes(data);

            int from = random.Next(0, (length / 2) + 1) * 2;

            byte[] expected = [.. data];
            byte[] reversedTail = WcsPairReverse([.. data[from..]]);
            Array.Copy(reversedTail, 0, expected, from, reversedTail.Length);

            var policy = new ByteOrderPolicy { SwapFromByteOffset = from };
            byte[] actual = [.. data];
            policy.Transform(actual);

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// 固化「NTI 写路径里哪些字段需要高低位转换」这一约定。
    /// 偏移 ≥ 6 的字段需要交换，&lt; 6 的不需要——这正是 WCS 整块写路径的分界，
    /// 也是判断某次写入是否漏了转换的依据。
    /// </summary>
    [Theory]
    [InlineData("tasknum", false)]
    [InlineData("goodstype", false)]
    [InlineData("from", false)]
    [InlineData("to", true)]
    [InlineData("heartbeat", true)]
    [InlineData("barcode", true)]
    public void CoversField_NtiWritePolicy_MatchesSwapBoundary(string fieldName, bool expected)
    {
        var policy = new ByteOrderPolicy { SwapFromByteOffset = 6 };

        Assert.True(NtiProtocol.Instance.TryGetWriteField(fieldName, out FieldDescriptor field));
        Assert.Equal(expected, policy.CoversField(field));
    }

    [Fact]
    public void CoversField_ReadPolicy_OnlyCoversBarcodeRange()
    {
        var policy = new ByteOrderPolicy { ExtraPairUnswapRanges = [[12, 28]] };

        Assert.False(policy.CoversField(ReadField("to")));
        Assert.False(policy.CoversField(ReadField("heartbeat")));
        Assert.True(policy.CoversField(ReadField("barcode")));

        static FieldDescriptor ReadField(string name)
        {
            Assert.True(NtiProtocol.Instance.TryGetReadField(name, out FieldDescriptor field));
            return field;
        }
    }
}
