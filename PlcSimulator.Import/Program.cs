using System.Text;
using System.Text.Json;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Import;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return 0;
}

var options = new ImportOptions();
List<string> positional = [];

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" or "-o" when i + 1 < args.Length:
            options.OutputPath = args[++i];
            break;

        case "--ip" when i + 1 < args.Length:
            options.IpOverride = args[++i];
            break;

        case "--port" when i + 1 < args.Length:
            options.Port = int.Parse(args[++i]);
            break;

        case "--protocol-type" when i + 1 < args.Length:
            options.ProtocolType = args[++i];
            break;

        case "--delay" when i + 1 < args.Length:
            options.ActionDelayMs = int.Parse(args[++i]);
            break;

        case "--jitter" when i + 1 < args.Length:
            options.JitterMs = int.Parse(args[++i]);
            break;

        case "--value-is-byte":
            options.ValueIsRegister = false;
            break;

        case "--value-is-register":
            options.ValueIsRegister = true;
            break;

        default:
            positional.Add(args[i]);
            break;
    }
}

if (positional.Count > 0)
{
    options.InputPath = positional[0];
}

if (string.IsNullOrWhiteSpace(options.InputPath))
{
    Console.Error.WriteLine("缺少输入文件。");
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

if (!File.Exists(options.InputPath))
{
    Console.Error.WriteLine($"找不到输入文件：{Path.GetFullPath(options.InputPath)}");
    return 1;
}

options.OutputPath = string.IsNullOrWhiteSpace(options.OutputPath)
    ? "config/simulator.generated.json"
    : options.OutputPath;

try
{
    ImportTable table = ImportTable.Load(options.InputPath);
    Console.WriteLine($"已读取 {Path.GetFullPath(options.InputPath)}，{table.Rows.Count} 行。");

    SimulatorConfig config = ConfigBuilder.Build(table, options, out List<string> notes);

    string json = JsonSerializer.Serialize(config, ConfigLoader.SerializerOptions);

    // 不带 BOM：ConfigLoader 能读，但别的 JSON 工具对 BOM 支持不一。
    File.WriteAllText(options.OutputPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    foreach (string note in notes)
    {
        Console.WriteLine(note);
    }

    Console.WriteLine();
    Console.WriteLine($"已生成 {Path.GetFullPath(options.OutputPath)}");
    Console.WriteLine($"value/signaltype 单位：{(options.ValueIsRegister ? "寄存器（字节偏移 = value × 2）" : "字节")}");

    foreach (DeviceConfig device in config.Devices)
    {
        int maxEnd = device.Stations.Count == 0
            ? 0
            : device.Stations.Max(static s => s.ByteOffset + s.LengthBytes);

        Console.WriteLine(
            $"  {device.Id}  IP {device.Ip}  站台 {device.Stations.Count} 个  "
            + $"块 [0, {device.Blocks[0].LengthBytes}) 字节（站台最大到 {maxEnd}）");
    }

    Console.WriteLine();
    Console.WriteLine("建议接着执行：");
    Console.WriteLine($"  dotnet run --project PlcSimulator.Cli -- check {options.OutputPath}");

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"生成失败：{ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("由 wcs_opcitem 导出数据生成模拟器配置");
    Console.WriteLine();
    Console.WriteLine("用法：");
    Console.WriteLine("  import <输入.csv | 输入.xlsx> [选项]");
    Console.WriteLine();
    Console.WriteLine("选项：");
    Console.WriteLine("  -o, --output <路径>      输出配置路径，默认 config/simulator.generated.json");
    Console.WriteLine("      --ip <IP>            覆盖设备 IP（默认用数据库 userid 字段）");
    Console.WriteLine("      --port <端口>        设备端口，默认 502");
    Console.WriteLine("      --protocol-type <协议>  对接协议（决定各字段的字节偏移），默认 NTI");
    Console.WriteLine("      --delay <ms>         模拟动作延时，默认 3000");
    Console.WriteLine("      --jitter <ms>        动作抖动，默认 200");
    Console.WriteLine("      --value-is-byte      把 value/signaltype 当字节解释");
    Console.WriteLine("      --value-is-register  把 value/signaltype 当寄存器解释（默认）");
    Console.WriteLine();
    Console.WriteLine("输入格式：");
    Console.WriteLine("  数据库表 wcs_opcitem 的导出：.xlsx 工作簿或 CSV（逗号分隔）都可以，读第一个工作表。");
    Console.WriteLine("  至少需要 stationno、userid、value、signaltype 四列。");
}
