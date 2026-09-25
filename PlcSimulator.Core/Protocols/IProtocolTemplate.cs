namespace PlcSimulator.Core.Protocols;

public enum FieldKind
{
    U8,
    U16,
    I16,
    U32,
    I32,
    Ascii,
    Hex,
}

/// <summary>站台数据块内的一个字段。偏移一律以字节计，与数据库 value 字段同口径。</summary>
public sealed record FieldDescriptor(string Name, int ByteOffset, FieldKind Kind, int Length = 0)
{
    public int SizeInBytes => Kind switch
    {
        FieldKind.U8 => 1,
        FieldKind.U16 or FieldKind.I16 => 2,
        FieldKind.U32 or FieldKind.I32 => 4,
        FieldKind.Ascii or FieldKind.Hex => Length,
        _ => 0,
    };

    public int EndByteOffset => ByteOffset + SizeInBytes;
}

/// <summary>
/// 站台字段定义模板。NTI 与 CATL 的字段偏移完全不同，且各自**读写偏移也不对称**
/// （WCS 源码如此），所以读写各有一份字段表。
/// </summary>
public interface IProtocolTemplate
{
    string Name { get; }

    IReadOnlyList<FieldDescriptor> ReadFields { get; }

    IReadOnlyList<FieldDescriptor> WriteFields { get; }

    /// <summary>该协议要求站台块的最小字节数，低于此值会让 WCS 解析时越界。</summary>
    int MinBlockBytes { get; }

    /// <summary>按名字查读字段（如 "heartbeat"、"status"）。</summary>
    bool TryGetReadField(string name, out FieldDescriptor field);

    /// <summary>按名字查写字段（如 "tasknum"、"barcode"）。</summary>
    bool TryGetWriteField(string name, out FieldDescriptor field);
}
