namespace PlcSimulator.Core.Protocols;

internal static class LayoutLookup
{
    internal static bool TryFind(IReadOnlyList<FieldDescriptor> fields, string name, out FieldDescriptor field)
    {
        foreach (FieldDescriptor candidate in fields)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                field = candidate;
                return true;
            }
        }

        field = null!;
        return false;
    }
}

public static class ProtocolRegistry
{
    private static readonly Dictionary<string, IProtocolTemplate> Templates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [NtiProtocol.ProtocolName] = NtiProtocol.Instance,
            [CatlProtocol.ProtocolName] = CatlProtocol.Instance,
        };

    public static IReadOnlyCollection<string> KnownNames => Templates.Keys;

    public static bool TryGet(string name, out IProtocolTemplate layout)
        => Templates.TryGetValue(name, out layout!);

    /// <summary>按名字取对接协议模板，取不到时抛出并列出可用的名字。</summary>
    public static IProtocolTemplate Get(string name)
    {
        if (TryGet(name, out IProtocolTemplate layout))
        {
            return layout;
        }

        throw new ArgumentException(
            $"未知的对接协议 \"{name}\"。可用：{string.Join(", ", Templates.Keys)}。");
    }
}
