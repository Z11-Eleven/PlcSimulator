using System.Text;

namespace PlcSimulator.Import;

/// <summary>
/// 从数据库导出的点位表：表头 + 数据行。
/// <para>
/// <see cref="Load"/> 按扩展名分派：<c>.xlsx</c> 走 <see cref="XlsxReader"/>，
/// 其余按 CSV 文本读。两种来源在这里汇成同一种结构，下游的
/// <see cref="ConfigBuilder"/> 因此不关心文件是怎么来的。
/// </para>
/// </summary>
public sealed class ImportTable
{
    private readonly List<string> _headers;
    private readonly List<string[]> _rows;

    internal ImportTable(List<string> headers, List<string[]> rows)
    {
        _headers = headers;
        _rows = rows;
    }

    public IReadOnlyList<string> Headers => _headers;

    public IReadOnlyList<string[]> Rows => _rows;

    /// <summary>
    /// 读入点位表。按扩展名分派：<c>.xlsx</c> 按 Excel 工作簿解析，其余按 CSV 文本解析。
    /// </summary>
    public static ImportTable Load(string path)
    {
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return XlsxReader.Read(path);
        }

        if (Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                ".xls 是 Excel 97-2003 的二进制格式，读不了。请用 Excel 打开后另存为 .xlsx 或 CSV。");
        }

        return ParseCsv(ReadText(path));
    }

    /// <summary>按列名取索引，找不到返回 -1。</summary>
    public int IndexOf(string name)
        => _headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

    public static string Cell(string[] row, int index)
        => index >= 0 && index < row.Length ? row[index].Trim() : string.Empty;

    /// <summary>
    /// Excel 导出的 CSV 常见 GB2312/UTF-8 混用：先按**严格** UTF-8 读（非法字节直接抛），
    /// 抛了再退到 GB2312。
    /// <para>
    /// 这里必须用严格模式：宽松的 <c>Encoding.UTF8</c> 遇到非法字节只会替换成 U+FFFD、
    /// 永远不抛异常，回退分支就成了死代码。后果很隐蔽——stationno 这类 ASCII 列照样能读，
    /// 配置看着是生成成功了，但 remark / zonecode 的中文全成乱码，
    /// 「提升机」归组随之静默失效（路径变得和 WCS 不一致），而且全程没有任何提示。
    /// </para>
    /// </summary>
    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(
                path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return File.ReadAllText(path, Encoding.GetEncoding("GB2312"));
        }
    }

    private static ImportTable ParseCsv(string text)
    {
        List<string[]> rows = ParseRows(text);

        if (rows.Count == 0)
        {
            throw new InvalidDataException("CSV 内容为空。");
        }

        List<string> headers = [.. rows[0].Select(static h => h.Trim().TrimStart('﻿'))];
        rows.RemoveAt(0);

        return new ImportTable(headers, rows);
    }

    /// <summary>
    /// 极简 CSV 解析：支持引号包裹的字段（字段内含逗号或换行）、双引号转义、CRLF 与 LF 换行。
    /// 从 Excel 另存为 CSV 导出的文件可以直接读。
    /// </summary>
    private static List<string[]> ParseRows(string text)
    {
        List<string[]> rows = [];
        List<string> current = [];
        StringBuilder field = new();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                    continue;
                }

                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    current.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    current.Add(field.ToString());
                    field.Clear();
                    rows.Add([.. current]);
                    current.Clear();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            rows.Add([.. current]);
        }

        return rows;
    }
}
