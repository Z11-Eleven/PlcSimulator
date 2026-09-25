namespace PlcSimulator.Core.Protocols;

/// <summary>
/// NTI 协议的站台字段定义。
/// 读偏移取自 <c>ConveryPLC.BindStationNTI</c>，写偏移取自
/// <c>ConveryPLC.Write(string[], ConveryEntity)</c>。
/// </summary>
public sealed class NtiProtocol : IProtocolTemplate
{
    public const string ProtocolName = "NTI";

    /// <summary>心跳寄存器在块内的字节偏移。</summary>
    public const int HeartbeatByteOffset = 8;

    public static readonly NtiProtocol Instance = new();

    public string Name => ProtocolName;

    public IReadOnlyList<FieldDescriptor> ReadFields { get; } =
    [
        new("tasknum", 0, FieldKind.U16),
        new("goodstype", 2, FieldKind.U16),
        new("from", 4, FieldKind.U16),
        new("to", 6, FieldKind.U16),
        new("heartbeat", HeartbeatByteOffset, FieldKind.U16),
        new("barcode", 12, FieldKind.Ascii, 16),
        new("status", 28, FieldKind.U16),
    ];

    public IReadOnlyList<FieldDescriptor> WriteFields { get; } =
    [
        new("tasknum", 0, FieldKind.U16),
        new("goodstype", 2, FieldKind.U16),
        new("from", 4, FieldKind.U16),
        new("to", 6, FieldKind.U16),
        new("heartbeat", HeartbeatByteOffset, FieldKind.U16),
        new("barcode", 12, FieldKind.Ascii, 16),
    ];

    public int MinBlockBytes => MaxEndOffset(ReadFields, WriteFields);

    public bool TryGetReadField(string name, out FieldDescriptor field)
        => LayoutLookup.TryFind(ReadFields, name, out field);

    public bool TryGetWriteField(string name, out FieldDescriptor field)
        => LayoutLookup.TryFind(WriteFields, name, out field);

    internal static int MaxEndOffset(params IReadOnlyList<FieldDescriptor>[] sets)
    {
        int max = 0;
        foreach (IReadOnlyList<FieldDescriptor> set in sets)
        {
            foreach (FieldDescriptor field in set)
            {
                max = Math.Max(max, field.EndByteOffset);
            }
        }

        return max;
    }
}
