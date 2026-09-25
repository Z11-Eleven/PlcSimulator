using PlcSimulator.Core.Configuration;

namespace PlcSimulator.Import;

public sealed class ImportOptions
{
    public string InputPath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    /// <summary>覆盖设备 IP；为空则用数据库 userid 字段。</summary>
    public string? IpOverride { get; set; }

    public int Port { get; set; } = 502;

    /// <summary>
    /// value / signaltype 的单位：true = 寄存器（Modbus 标准语义，命令行默认）。
    /// 此时站台的字节偏移 = value × 2。
    /// </summary>
    public bool ValueIsRegister { get; set; } = true;

    public string ProtocolType { get; set; } = "NTI";

    public int ActionDelayMs { get; set; } = 3000;

    public int JitterMs { get; set; } = 200;
}

/// <summary>
/// 由 wcs_opcitem 的数据行生成模拟器配置。
/// 分组规则与 WCS 一致（`ConveryPLC.ConveryInit`）：IP + DBAddress + belong + field1 四项相同即同一组。
/// </summary>
public static class ConfigBuilder
{
    private sealed record Row(
        string StationNo,
        string Name,
        string Ip,
        string DbAddress,
        string Belong,
        string Group,
        string ProtocolType,
        int Value,
        int SignalType,
        int StationType,
        int X,
        int Y,
        int Width,
        int Height,
        string ArrowDirection,
        string ZoneCode,
        string Remark,
        string Field5);

    public static SimulatorConfig Build(ImportTable table, ImportOptions options, out List<string> notes)
    {
        notes = [];

        int iStationNo = table.IndexOf("stationno");
        int iName = table.IndexOf("itemname");
        int iUser = table.IndexOf("userid");
        int iObjects = table.IndexOf("objects");
        int iSignal = table.IndexOf("signaltype");
        int iValue = table.IndexOf("value");
        int iGroup = table.IndexOf("field1");
        int iBelong = table.IndexOf("belong");
        int iType = table.IndexOf("stationtype");
        int iX = table.IndexOf("locationx");
        int iY = table.IndexOf("locationy");
        int iW = table.IndexOf("width");
        int iH = table.IndexOf("height");
        int iProtocol = table.IndexOf("protocolType");
        int iArrow = table.IndexOf("arrowdirection");
        int iZone = table.IndexOf("zonecode");
        int iRemark = table.IndexOf("remark");
        int iField5 = table.IndexOf("field5");

        if (iStationNo < 0 || iUser < 0 || iValue < 0 || iSignal < 0)
        {
            throw new InvalidDataException(
                "点位表缺少必需列。至少需要 stationno、userid、value、signaltype 四列。");
        }

        List<Row> rows = [];
        int skippedBelongZero = 0;
        int skippedNoStationNo = 0;

        foreach (string[] raw in table.Rows)
        {
            if (raw.Length == 0 || raw.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (ImportTable.Cell(raw, iBelong) == "0")
            {
                skippedBelongZero++;
                continue;
            }

            string stationNo = ImportTable.Cell(raw, iStationNo);
            if (string.IsNullOrWhiteSpace(stationNo))
            {
                skippedNoStationNo++;
                continue;
            }

            rows.Add(new Row(
                stationNo,
                ImportTable.Cell(raw, iName),
                options.IpOverride ?? ImportTable.Cell(raw, iUser),
                FirstPart(ImportTable.Cell(raw, iObjects)),
                ImportTable.Cell(raw, iBelong),
                ImportTable.Cell(raw, iGroup),
                ImportTable.Cell(raw, iProtocol),
                ToInt(FirstPart(ImportTable.Cell(raw, iValue))),
                ToInt(FirstPart(ImportTable.Cell(raw, iSignal))),
                ToInt(ImportTable.Cell(raw, iType)),
                ToInt(ImportTable.Cell(raw, iX)),
                ToInt(ImportTable.Cell(raw, iY)),
                Math.Max(1, ToInt(ImportTable.Cell(raw, iW))),
                Math.Max(1, ToInt(ImportTable.Cell(raw, iH))),
                ImportTable.Cell(raw, iArrow),
                Normalize(ImportTable.Cell(raw, iZone)),
                Normalize(ImportTable.Cell(raw, iRemark)),
                Normalize(ImportTable.Cell(raw, iField5))));
        }

        if (skippedBelongZero > 0)
        {
            notes.Add($"跳过 belong=0 的行 {skippedBelongZero} 条（WCS 不仿真这些站台）。");
        }

        if (skippedNoStationNo > 0)
        {
            notes.Add($"跳过 stationno 为空的行 {skippedNoStationNo} 条。");
        }

        int unit = options.ValueIsRegister ? 2 : 1;

        List<DeviceConfig> devices = [];

        foreach (IGrouping<(string Ip, string Db, string Belong, string Group), Row> group in
            rows.GroupBy(static r => (r.Ip, r.DbAddress, r.Belong, r.Group)))
        {
            List<Row> members = [.. group.OrderBy(static r => r.Value).ThenBy(static r => r.StationNo, StringComparer.Ordinal)];

            List<StationConfig> stations = [];
            int maxEnd = 0;

            foreach (Row row in members)
            {
                int byteOffset = row.Value * unit;
                int lengthBytes = row.SignalType * unit;

                stations.Add(new StationConfig
                {
                    StationNo = row.StationNo,
                    Name = string.IsNullOrWhiteSpace(row.Name) ? row.StationNo : row.Name,
                    Group = row.Group,
                    ByteOffset = byteOffset,
                    LengthBytes = lengthBytes,
                    StationType = row.StationType,
                    LocationX = row.X,
                    LocationY = row.Y,
                    Width = row.Width,
                    Height = row.Height,
                    ArrowDirection = Normalize(row.ArrowDirection),
                    ZoneCode = row.ZoneCode,
                    Remark = row.Remark,
                    Field5 = row.Field5,
                    Simulation = new SimulationConfig
                    {
                        ActionDelayMs = options.ActionDelayMs,
                        JitterMs = options.JitterMs,
                    },
                });

                maxEnd = Math.Max(maxEnd, byteOffset + lengthBytes);
            }

            string ip = group.Key.Ip;
            string belong = group.Key.Belong;

            devices.Add(new DeviceConfig
            {
                Id = $"CV-{belong}-{ip.Replace('.', '_')}",
                Name = $"{belong}线输送机（由 wcs_opcitem 生成）",
                Ip = ip,
                Port = options.Port,
                SlaveId = 1,
                Protocol = "Modbus",
                ProtocolTypeRaw = members[0].ProtocolType,
                DeviceType = "Conveyor",
                ProtocolType = options.ProtocolType,
                ReadByteOrderPolicy = "NTI-ConveyorRead",
                SingleFieldWriteByteOrderPolicy = "SingleFieldWrite",
                PollHintMs = 1000,
                Belong = belong,
                // 块要盖住 WCS 可能读写的范围；多留一段余量以容纳过量读。
                Blocks = [new RegisterBlockConfig
                {
                    Group = group.Key.Group,
                    BaseByteOffset = 0,
                    LengthBytes = maxEnd + 1024,
                }],
                Stations = stations,
            });
        }

        notes.Add($"生成 {devices.Count} 台设备、{rows.Count} 个站台。");

        // 站台号重复时 WCS 只认第一个（ConveryPLC 用 StationsInfo.Find 按站台号查找），
        // 重复项的寄存器段会被永远丢弃 —— 通常意味着数据库录入有误，值得明确报出来。
        foreach (IGrouping<string, Row> duplicated in
            rows.GroupBy(static r => r.StationNo).Where(static g => g.Count() > 1))
        {
            string where = string.Join(
                "、",
                duplicated.Select(static r => $"字节 {r.Value * 2}"));

            notes.Add(
                $"警告：stationNo \"{duplicated.Key}\" 重复 {duplicated.Count()} 次（{where}）。"
                + "WCS 按站台号查找只认第一个，重复项的寄存器段不会被解析，请核对数据。");
        }

        return new SimulatorConfig
        {
            Server = new ServerConfig
            {
                Listen = [.. devices
                    .Select(static d => d.Ip)
                    .Distinct(StringComparer.Ordinal)
                    .Select(ip => new ListenEndpointConfig { Ip = ip, Port = options.Port })],
                AcceptAnySlaveId = true,
            },
            ByteOrder = new ByteOrderConfig
            {
                Policies =
                {
                    ["NTI-ConveyorRead"] = new ByteOrderPolicyConfig
                    {
                        ExtraPairUnswapRanges = [[12, 28]],
                    },
                    ["SingleFieldWrite"] = new ByteOrderPolicyConfig(),
                },
            },
            FaultInjection = new FaultInjectionConfig { Enabled = false },
            Devices = devices,
        };
    }

    private static string FirstPart(string value)
        => value.Split(',')[0].Trim();

    /// <summary>数据库导出里空值写成 "null"，这里归一成空串。</summary>
    private static string Normalize(string value)
        => string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ? string.Empty : value.Trim();

    private static int ToInt(string value)
        => int.TryParse(value, out int result) ? result : 0;
}
