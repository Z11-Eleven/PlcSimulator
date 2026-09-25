using System.IO.Compression;
using System.Text;
using System.Xml;

namespace PlcSimulator.Import;

/// <summary>
/// 读 .xlsx（Excel 2007+）。xlsx 就是一个装着 XML 的 zip，这里用 BCL 的
/// <see cref="ZipArchive"/> + <see cref="XmlReader"/> 直接解析，不引第三方库——
/// 开发机离线，且这个工程一直保持零外部依赖。
/// <para>
/// 只读**第一个工作表**。两个坑要注意：
/// </para>
/// <list type="bullet">
/// <item>工作表文件不一定叫 <c>sheet1.xml</c>，导出工具（比如 WCS 那个）可能写 <c>sheet2.xml</c>，
/// 必须按 workbook 的关系（rels）去找，不能硬编码路径。</item>
/// <item>各元素带什么命名空间前缀由导出工具决定（WCS 用 <c>x:</c>），
/// 所以一律按 <see cref="XmlReader.LocalName"/> 匹配，不看前缀。</item>
/// </list>
/// </summary>
internal static class XlsxReader
{
    private const string OfficeDocumentRelSuffix = "/officeDocument";
    private const string WorksheetRelSuffix = "/worksheet";
    private const string SharedStringsRelSuffix = "/sharedStrings";

    public static ImportTable Read(string path)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);

        string workbookPart = ResolveWorkbookPart(zip);
        (string sheetPart, string sheetName) = ResolveFirstSheet(zip, workbookPart);
        List<string> sharedStrings = ReadSharedStrings(zip, workbookPart);
        List<string[]> rows = ReadSheet(zip, sheetPart, sharedStrings);

        if (rows.Count == 0)
        {
            throw new InvalidDataException($"工作表 \"{sheetName}\" 里没有数据。");
        }

        List<string> headers = [.. rows[0].Select(static h => h.Trim())];
        rows.RemoveAt(0);

        return new ImportTable(headers, rows);
    }

    /// <summary>从包根的 _rels 关系里找工作簿部件，默认 xl/workbook.xml。</summary>
    private static string ResolveWorkbookPart(ZipArchive zip)
    {
        foreach ((string id, string type, string target) in ReadRelationships(zip, "_rels/.rels"))
        {
            if (type.EndsWith(OfficeDocumentRelSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePart(target, baseDirectory: string.Empty);
            }
        }

        return "xl/workbook.xml";
    }

    /// <summary>取第一个工作表：先拿它的 r:id，再到工作簿自己的 rels 里换出真正的部件路径。</summary>
    private static (string Part, string Name) ResolveFirstSheet(ZipArchive zip, string workbookPart)
    {
        string? sheetName = null;
        string? relationshipId = null;

        using (XmlReader reader = CreateReader(zip, workbookPart))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet")
                {
                    continue;
                }

                sheetName = reader.GetAttribute("name");

                // r:id 的前缀由导出工具决定，按局部名找。
                for (int i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToAttribute(i);
                    if (reader.LocalName == "id")
                    {
                        relationshipId = reader.Value;
                        break;
                    }
                }

                reader.MoveToElement();
                break;
            }
        }

        if (relationshipId is null)
        {
            throw new InvalidDataException("工作簿里没有工作表。");
        }

        string workbookDirectory = GetDirectory(workbookPart);

        foreach ((string id, string type, string target) in ReadRelationships(zip, $"{workbookDirectory}/_rels/{Path.GetFileName(workbookPart)}.rels"))
        {
            if (string.Equals(id, relationshipId, StringComparison.Ordinal)
                && type.EndsWith(WorksheetRelSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return (NormalizePart(target, workbookDirectory), sheetName ?? "Sheet1");
            }
        }

        throw new InvalidDataException($"工作簿里找不到 r:id=\"{relationshipId}\" 对应的工作表。");
    }

    private static List<string> ReadSharedStrings(ZipArchive zip, string workbookPart)
    {
        string workbookDirectory = GetDirectory(workbookPart);
        string? part = null;

        foreach ((string id, string type, string target) in ReadRelationships(zip, $"{workbookDirectory}/_rels/{Path.GetFileName(workbookPart)}.rels"))
        {
            if (type.EndsWith(SharedStringsRelSuffix, StringComparison.OrdinalIgnoreCase))
            {
                part = NormalizePart(target, workbookDirectory);
                break;
            }
        }

        List<string> strings = [];

        if (part is null || zip.GetEntry(part) is null)
        {
            return strings;
        }

        using XmlReader reader = CreateReader(zip, part);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
            {
                continue;
            }

            // 一个 si 可能由多个 <t> 片段组成（富文本），全部拼起来。
            StringBuilder text = new();
            int depth = reader.Depth;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si" && reader.Depth == depth)
                {
                    break;
                }

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                {
                    text.Append(ReadElementText(reader));
                }
            }

            strings.Add(text.ToString());
        }

        return strings;
    }

    private static List<string[]> ReadSheet(ZipArchive zip, string sheetPart, List<string> sharedStrings)
    {
        List<string[]> rows = [];
        Dictionary<int, string> cells = [];
        int maxColumn = -1;
        bool inRow = false;

        using XmlReader reader = CreateReader(zip, sheetPart);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                cells.Clear();
                maxColumn = -1;
                inRow = true;
                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && inRow)
            {
                string[] row = new string[maxColumn + 1];

                for (int i = 0; i <= maxColumn; i++)
                {
                    row[i] = cells.TryGetValue(i, out string? cellValue) ? cellValue : string.Empty;
                }

                rows.Add(row);
                inRow = false;
                continue;
            }

            if (!inRow || reader.NodeType != XmlNodeType.Element || reader.LocalName != "c")
            {
                continue;
            }

            // 空单元格会被整个省掉（比如 B 列为空时直接从 A 跳到 C），
            // 所以列号必须从 r 属性（"C5"）算，不能按出现顺序数。
            string? reference = reader.GetAttribute("r");
            int column = reference is null ? maxColumn + 1 : ColumnIndex(reference);

            string value = reader.IsEmptyElement ? string.Empty : ReadCellValue(reader, sharedStrings);

            cells[column] = value;
            maxColumn = Math.Max(maxColumn, column);
        }

        return rows;
    }

    private static string ReadCellValue(XmlReader reader, List<string> sharedStrings)
    {
        string? type = reader.GetAttribute("t");
        string? rawValue = null;
        string? inlineText = null;
        int depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "c" && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "v")
            {
                rawValue = ReadElementText(reader);
            }
            else if (reader.LocalName == "t")
            {
                inlineText = (inlineText ?? string.Empty) + ReadElementText(reader);
            }
        }

        if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
        {
            return inlineText ?? string.Empty;
        }

        if (string.Equals(type, "s", StringComparison.Ordinal))
        {
            return int.TryParse(rawValue, out int index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }

        // 数字、公式结果（t="str"）、布尔都直接用 <v> 里的文本
        return rawValue ?? inlineText ?? string.Empty;
    }

    /// <summary>
    /// 读当前元素的文本内容，读完停在它的结束标签上（外层 Read 会继续前进）。
    /// 用 ReadElementContentAsString 会多跳一格，循环里容易漏元素，所以自己收。
    /// </summary>
    private static string ReadElementText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        int depth = reader.Depth;
        StringBuilder text = new();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
            {
                text.Append(reader.Value);
            }
        }

        return text.ToString();
    }

    private static IEnumerable<(string Id, string Type, string Target)> ReadRelationships(
        ZipArchive zip, string relsPart)
    {
        if (zip.GetEntry(relsPart) is null)
        {
            yield break;
        }

        using XmlReader reader = CreateReader(zip, relsPart);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
            {
                continue;
            }

            yield return (
                reader.GetAttribute("Id") ?? string.Empty,
                reader.GetAttribute("Type") ?? string.Empty,
                reader.GetAttribute("Target") ?? string.Empty);
        }
    }

    private static XmlReader CreateReader(ZipArchive zip, string part)
    {
        Stream? stream = OpenPart(zip, part)
            ?? throw new InvalidDataException($"工作簿里找不到部件 {part}。");

        return XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, CloseInput = true });
    }

    private static Stream? OpenPart(ZipArchive zip, string part)
        => zip.GetEntry(part)?.Open();

    private static string GetDirectory(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? string.Empty : part[..slash];
    }

    /// <summary>关系里的 Target 可能是绝对路径（/xl/...）也可能是相对路径，统一成包内路径。</summary>
    private static string NormalizePart(string target, string baseDirectory)
    {
        string path = target.Replace('\\', '/');

        if (path.StartsWith('/'))
        {
            return path.TrimStart('/');
        }

        return baseDirectory.Length == 0 ? path : $"{baseDirectory}/{path}";
    }

    /// <summary>把 "C5" 这样的引用换算成 0 起的列号。</summary>
    private static int ColumnIndex(string reference)
    {
        int index = 0;

        foreach (char c in reference)
        {
            if (!char.IsLetter(c))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return index - 1;
    }
}
