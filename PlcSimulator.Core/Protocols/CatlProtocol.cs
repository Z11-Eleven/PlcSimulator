namespace PlcSimulator.Core.Protocols;

/// <summary>
/// CATL 协议的站台字段定义。
/// <para>
/// 读偏移与写偏移**不对称**（读 from@34 / to@36，写 from@32 / to@4），
/// 这是 WCS 源码的既成事实，模拟器照实复刻而不是替它纠正。
/// </para>
/// </summary>
public sealed class CatlProtocol : IProtocolTemplate
{
    public const string ProtocolName = "CATL";

    /// <summary>CATL 没有独立心跳寄存器，改用回执字节 ack。</summary>
    public const int AckByteOffset = 32;

    public static readonly CatlProtocol Instance = new();

    public string Name => ProtocolName;

    public IReadOnlyList<FieldDescriptor> ReadFields { get; } =
    [
        new("autoManual", 2, FieldKind.U8),
        new("status", 3, FieldKind.U8),
        new("fault", 5, FieldKind.U8),
        new("goodstype", 7, FieldKind.U8),
        new("tasknum", 10, FieldKind.U16),
        new("barcode", 12, FieldKind.Ascii, 16),
        new("ack", AckByteOffset, FieldKind.U8),
        new("from", 34, FieldKind.U16),
        new("to", 36, FieldKind.U16),
    ];

    public IReadOnlyList<FieldDescriptor> WriteFields { get; } =
    [
        new("to", 4, FieldKind.U16),
        new("goodstype", 6, FieldKind.U16),
        new("tasknum", 10, FieldKind.U16),
        new("barcode", 12, FieldKind.Ascii, 16),
        new("from", 32, FieldKind.U16),
    ];

    public int MinBlockBytes => NtiProtocol.MaxEndOffset(ReadFields, WriteFields);

    public bool TryGetReadField(string name, out FieldDescriptor field)
        => LayoutLookup.TryFind(ReadFields, name, out field);

    public bool TryGetWriteField(string name, out FieldDescriptor field)
        => LayoutLookup.TryFind(WriteFields, name, out field);
}
