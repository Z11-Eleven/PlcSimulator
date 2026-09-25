using System.IO.Compression;
using System.Text;

namespace PlcSimulator.Import.Tests;

/// <summary>
/// 点位表读取：xlsx 与 CSV 两条路径。
/// xlsx 这条最容易被导出工具的实现细节绊倒，所以测试里刻意还原了两个坑：
/// 工作表物理文件可能叫 sheet2.xml（编号跳号），以及空单元格会被整个省掉。
/// </summary>
public class ImportTableTests
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    private sealed class TempFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // 临时文件删不掉不影响测试结论
            }
        }
    }

    /// <summary>
    /// 拼一个最小可用的 xlsx。命名空间前缀刻意用 <c>x:</c>（WCS 的导出工具就是这么写的），
    /// 工作表物理路径由调用方指定，用来复现"编号跳号"。
    /// </summary>
    private static TempFile BuildXlsx(string sheetPart, string sheetXml, params string[] sharedStrings)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"plcsim-import-{Guid.NewGuid():N}.xlsx");

        using (FileStream file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            Write(zip, "_rels/.rels",
                $"""<Relationships xmlns="{PackageRelNs}"><Relationship Id="rId1" Type="{RelNs}/officeDocument" Target="xl/workbook.xml" /></Relationships>""");

            Write(zip, "xl/workbook.xml",
                $"""<x:workbook xmlns:x="{MainNs}"><x:sheets><x:sheet name="expdata" sheetId="1" r:id="rIdSheet" xmlns:r="{RelNs}" /></x:sheets></x:workbook>""");

            Write(zip, "xl/_rels/workbook.xml.rels",
                $"""<Relationships xmlns="{PackageRelNs}"><Relationship Id="rIdSheet" Type="{RelNs}/worksheet" Target="/{sheetPart}" /><Relationship Id="rIdSst" Type="{RelNs}/sharedStrings" Target="/xl/sharedStrings.xml" /></Relationships>""");

            Write(zip, "xl/sharedStrings.xml",
                $"""<x:sst xmlns:x="{MainNs}" count="{sharedStrings.Length}" uniqueCount="{sharedStrings.Length}">{string.Concat(sharedStrings.Select(static s => $"<x:si><x:t>{s}</x:t></x:si>"))}</x:sst>""");

            Write(zip, sheetPart, sheetXml);
        }

        return new TempFile(path);
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using Stream stream = zip.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    [Fact]
    public void Xlsx_SheetFileNumberedAheadOfSheet1_IsFound()
    {
        // WCS 导出的工作簿里只有一个工作表，物理文件却是 sheet2.xml。
        // 硬编码 sheet1.xml 会直接失败，必须按 workbook 的关系去找。
        using TempFile file = BuildXlsx(
            "xl/worksheets/sheet2.xml",
            $"""
             <x:worksheet xmlns:x="{MainNs}"><x:sheetData>
             <x:row><x:c r="A1" t="s"><x:v>0</x:v></x:c><x:c r="B1" t="s"><x:v>1</x:v></x:c></x:row>
             <x:row><x:c r="A2" t="s"><x:v>2</x:v></x:c><x:c r="B2" t="s"><x:v>3</x:v></x:c></x:row>
             </x:sheetData></x:worksheet>
             """,
            "stationno", "userid", "1001", "127.0.0.1");

        ImportTable table = ImportTable.Load(file.Path);

        Assert.Equal(["stationno", "userid"], table.Headers);
        Assert.Single(table.Rows);
        Assert.Equal("1001", ImportTable.Cell(table.Rows[0], 0));
        Assert.Equal("127.0.0.1", ImportTable.Cell(table.Rows[0], 1));
    }

    [Fact]
    public void Xlsx_SkippedCells_KeepTheirColumnPosition()
    {
        // 空单元格会被整个省掉：B 列空的时候，C 列的单元格仍写着 r="C2"。
        // 按出现顺序数格子会把 C 的内容当成 B 的，必须按 r 属性算列号。
        using TempFile file = BuildXlsx(
            "xl/worksheets/sheet1.xml",
            $"""
             <x:worksheet xmlns:x="{MainNs}"><x:sheetData>
             <x:row><x:c r="A1" t="s"><x:v>0</x:v></x:c><x:c r="B1" t="s"><x:v>1</x:v></x:c><x:c r="C1" t="s"><x:v>2</x:v></x:c></x:row>
             <x:row><x:c r="A2" t="s"><x:v>3</x:v></x:c><x:c r="C2"><x:v>30</x:v></x:c></x:row>
             </x:sheetData></x:worksheet>
             """,
            "stationno", "itemname", "signaltype", "1001");

        ImportTable table = ImportTable.Load(file.Path);
        string[] row = table.Rows[0];

        Assert.Equal("1001", ImportTable.Cell(row, 0));
        Assert.Equal(string.Empty, ImportTable.Cell(row, 1));   // B 列确实是空的
        Assert.Equal("30", ImportTable.Cell(row, 2));           // C 列的内容还在 C 列
    }

    [Fact]
    public void Xlsx_NumericCell_ComesThroughAsPlainText()
    {
        // 数据库导出的数字列（value / signaltype / locationx…）在 xlsx 里是数值单元格，
        // 没有 t 属性，值直接写在 <v> 里。
        using TempFile file = BuildXlsx(
            "xl/worksheets/sheet1.xml",
            $"""
             <x:worksheet xmlns:x="{MainNs}"><x:sheetData>
             <x:row><x:c r="A1" t="s"><x:v>0</x:v></x:c></x:row>
             <x:row><x:c r="A2"><x:v>19230</x:v></x:c></x:row>
             </x:sheetData></x:worksheet>
             """,
            "value");

        ImportTable table = ImportTable.Load(file.Path);

        Assert.Equal("19230", ImportTable.Cell(table.Rows[0], 0));
    }

    [Fact]
    public void Xlsx_InlineString_IsRead()
    {
        using TempFile file = BuildXlsx(
            "xl/worksheets/sheet1.xml",
            $"""
             <x:worksheet xmlns:x="{MainNs}"><x:sheetData>
             <x:row><x:c r="A1" t="s"><x:v>0</x:v></x:c></x:row>
             <x:row><x:c r="A2" t="inlineStr"><x:is><x:t>输送机监控</x:t></x:is></x:c></x:row>
             </x:sheetData></x:worksheet>
             """,
            "zonecode");

        ImportTable table = ImportTable.Load(file.Path);

        Assert.Equal("输送机监控", ImportTable.Cell(table.Rows[0], 0));
    }

    [Fact]
    public void OldXls_IsRejectedWithActionableMessage()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"plcsim-{Guid.NewGuid():N}.xls");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0]);

        try
        {
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ImportTable.Load(path));
            Assert.Contains("另存为", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Csv_StillLoadsAndSkipsBom()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"plcsim-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "﻿stationno,userid\r\n\"1001\",127.0.0.1\r\n", new UTF8Encoding(true));

        try
        {
            ImportTable table = ImportTable.Load(path);

            Assert.Equal(["stationno", "userid"], table.Headers);
            Assert.Equal("1001", ImportTable.Cell(table.Rows[0], 0));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
