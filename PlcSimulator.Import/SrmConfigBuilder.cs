using System.Net;
using System.Buffers.Binary;
using System.Globalization;
using PlcSimulator.Core.Configuration;

namespace PlcSimulator.Import;

/// <summary>合并 wcs_equipmentinfo 中同一设备的通信行，生成 NTI 堆垛机配置。</summary>
public static class SrmConfigBuilder
{
    public static SimulatorConfig Build(ImportTable table, ImportOptions options, out List<string> notes)
    {
        notes = [];
        if (options.SrmTransport is not ("Socket" or "S7"))
        {
            throw new InvalidDataException("堆垛机通讯方式只能选择 Socket 或 S7。");
        }
        foreach (string column in new[] { "equipmentnum", "RIPADDR", "protocoltype" })
        {
            if (table.IndexOf(column) < 0)
            {
                throw new InvalidDataException($"堆垛机设备表缺少必需列 {column}，请导出 wcs_equipmentinfo。");
            }
        }

        string Cell(string[] row, string column) => Normalize(ImportTable.Cell(row, table.IndexOf(column)));
        List<string[]> rows = [];
        foreach (string[] row in table.Rows.Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v))))
        {
            string protocol = Cell(row, "protocoltype");
            if (protocol.Equals("SRM", StringComparison.OrdinalIgnoreCase)
                || (protocol.Contains("NTI", StringComparison.OrdinalIgnoreCase)
                    && (protocol.Contains("Socket", StringComparison.OrdinalIgnoreCase) || protocol.Contains("S7", StringComparison.OrdinalIgnoreCase))))
            {
                if (Cell(row, "equipmentnum").Length == 0)
                {
                    throw new InvalidDataException("堆垛机行的 equipmentnum 为空。");
                }
                rows.Add(row);
            }
            else
            {
                notes.Add($"跳过设备 {Cell(row, "equipmentnum")}：协议 {protocol} 不属于已支持的 NTI Socket / S7 堆垛机。");
            }
        }
        if (rows.Count == 0)
        {
            throw new InvalidDataException("导出文件中没有可生成的 NTI Socket / S7 堆垛机。");
        }
        if (options.UseLoopbackIps && !string.IsNullOrWhiteSpace(options.IpOverride))
        {
            throw new InvalidDataException("独立回环 IP 与设备 IP 覆盖不能同时使用，请选择其中一种。");
        }

        List<DeviceConfig> devices = [];
        uint loopbackStart = options.UseLoopbackIps ? ParseLoopbackStart(options.LoopbackStartIp) : 0;
        foreach (var group in rows.GroupBy(r => Cell(r, "equipmentnum"), StringComparer.OrdinalIgnoreCase))
        {
            string id = group.Key;
            string Common(string column)
            {
                string[] values = group.Select(r => Cell(r, column)).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                if (values.Length > 1)
                {
                    throw new InvalidDataException($"设备 {id} 的 {column} 在多行中不一致，请核对导出数据。");
                }
                return values.FirstOrDefault() ?? string.Empty;
            }
            string rawProtocol = Common("protocoltype");
            string originalIp = Common("RIPADDR");
            string ip = options.UseLoopbackIps ? Loopback(loopbackStart, devices.Count) : options.IpOverride?.Trim() ?? originalIp;
            if (!IPAddress.TryParse(ip, out _))
            {
                throw new InvalidDataException($"设备 {id} 的监听 IP 无效。");
            }
            if (options.UseLoopbackIps)
            {
                notes.Add($"设备 {id} 分配监听地址 {ip}；WCS 中该设备的连接 IP 也应设为此地址。");
            }

            string forkType = Common("forktype");
            int forks = 2;
            if (forkType.Length > 0)
            {
                string configured = forkType.Split(';').Last().Trim();
                forks = configured switch
                {
                    "3" => 2,
                    "1" or "2" => 1,
                    _ => throw new InvalidDataException($"设备 {id} 的 forktype 无法识别（应为 3;3、1;1 或 2;2）。"),
                };
            }
            else
            {
                forkType = "3;3";
                notes.Add($"设备 {id} 缺少 forktype，使用双货叉默认值 3;3。");
            }
            var srm = new SrmOptionsConfig
            {
                ForkType = forkType, ForkCount = forks, TravelDelayMs = options.TravelDelayMs,
                ActionDelayMs = options.ActionDelayMs, JitterMs = options.JitterMs,
                PickStations = Stations(Common("pickstation")), PutStations = Stations(Common("putstation")),
                NearFarStationsRaw = Common("field4"), AvoidanceRaw = Common("field5"),
            };
            string points = Common("stationpoint");
            foreach (string pair in points.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = pair.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out int point) || point is < 0 or > 255)
                {
                    throw new InvalidDataException($"设备 {id} 的 stationpoint 格式错误，应为 站台号,动作点;站台号,动作点。");
                }
                if (srm.StationPoints.TryGetValue(parts[0], out int existing) && existing != point)
                {
                    throw new InvalidDataException($"设备 {id} 的站台 {parts[0]} 对应多个动作点。");
                }
                srm.StationPoints[parts[0]] = point;
            }
            if (srm.StationPoints.Count == 0)
            {
                throw new InvalidDataException($"设备 {id} 缺少 stationpoint 站台动作点映射。");
            }
            foreach (string station in srm.PickStations.Concat(srm.PutStations))
            {
                if (!srm.StationPoints.ContainsKey(station))
                {
                    throw new InvalidDataException($"设备 {id} 的取放站台 {station} 没有 stationpoint 动作点。");
                }
            }
            if (srm.PickStations.Count == 0 || srm.PutStations.Count == 0)
            {
                notes.Add($"设备 {id} 的 pickstation/putstation 未完整提供；需在生成配置中补全取放站台。");
            }
            string[] avoidance = srm.AvoidanceRaw.Split(';', StringSplitOptions.TrimEntries);
            if (avoidance.Length >= 6 && srm.StationPoints.TryGetValue(avoidance[4], out int nearPoint))
            {
                srm.AvoidancePoint.ActionPoint = (byte)nearPoint;
            }
            else
            {
                notes.Add($"设备 {id} 的近端避让站台未映射动作点，avoidancePoint 使用默认值，火警模拟前请补充。");
            }

            var device = new DeviceConfig
            {
                Id = id, Name = Common("field3") is { Length: > 0 } name ? name : id,
                Ip = ip, Protocol = options.SrmTransport, ProtocolType = "SRM", ProtocolTypeRaw = rawProtocol,
                DeviceType = "Srm", Belong = Common("field2"), Srm = srm,
            };
            if (options.SrmTransport == "Socket")
            {
                var ports = new SocketPortsConfig();
                if (rawProtocol.Contains("Socket", StringComparison.OrdinalIgnoreCase))
                {
                    int[] declared = group.Select(r => Number(Cell(r, "rport"), id, "rport")).Distinct().ToArray();
                    if (declared.Length != 3 || !new[] { ports.Command, ports.Status, ports.Alarm }.All(declared.Contains))
                    {
                        throw new InvalidDataException($"设备 {id} 的 Socket 导出需包含 rport=2000/4000/6000 三行。");
                    }
                }
                else
                {
                    notes.Add($"设备 {id} 转为 Socket，使用指令/状态/报警端口 2000/4000/6000。");
                }
                device.SocketPorts = ports;
            }
            else
            {
                var source = options.S7;
                device.S7 = new S7OptionsConfig
                {
                    Port = source.Port, MaxPduLength = source.MaxPduLength,
                    Command = new() { DbNumber = source.Command.DbNumber, ByteOffset = source.Command.ByteOffset },
                    Status = new() { DbNumber = source.Status.DbNumber, ByteOffset = source.Status.ByteOffset },
                    Alarm = new() { DbNumber = source.Alarm.DbNumber, ByteOffset = source.Alarm.ByteOffset },
                    CommandPayloadOffset = source.CommandPayloadOffset, StatusLength = source.StatusLength, AlarmLength = source.AlarmLength,
                };
                if (rawProtocol.Contains("S7", StringComparison.OrdinalIgnoreCase))
                {
                    device.S7.Command.DbNumber = Number(Common("wdbaddr"), id, "wdbaddr");
                    device.S7.Status.DbNumber = Number(Common("rdbaddr"), id, "rdbaddr");
                    if (Common("rdblength").Length > 0)
                    {
                        device.S7.StatusLength = Number(Common("rdblength"), id, "rdblength");
                    }
                    if (Common("wdblength").Length > 0)
                    {
                        int declaredLength = Number(Common("wdblength"), id, "wdblength");
                        if (declaredLength is not (23 or 26))
                        {
                            throw new InvalidDataException($"设备 {id} 的 wdblength 应为 23 或 26 字节。");
                        }
                        notes.Add($"设备 {id} 导出写入区长度 {declaredLength}；指令布局采用所选的 {device.S7.CommandLength} 字节（负载偏移 {device.S7.CommandPayloadOffset}）。");
                    }
                    notes.Add($"设备 {id} 沿用导出的指令/状态 DB；报警 DB 使用生成页指定值。");
                }
                else
                {
                    notes.Add($"设备 {id} 转为 S7，使用指定的 DB 映射与指令布局；WCS 也需切换为 S7。");
                }
            }
            devices.Add(device);
            notes.Add($"设备 {id}：合并 {group.Count()} 行，{forks} 个货叉，{srm.StationPoints.Count} 个动作点。");
        }

        var config = new SimulatorConfig { Server = new() { Listen = [] }, Devices = devices };
        ConfigLoadResult validation = ConfigLoader.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidDataException("生成配置校验失败：" + string.Join("；", validation.Errors)
                + " 多台设备端口冲突时可选择独立回环 IP。");
        }
        notes.Add($"已生成 {devices.Count} 台 NTI 堆垛机（{options.SrmTransport}）。");
        return config;
    }

    private static string Normalize(string value)
        => value.Equals("null", StringComparison.OrdinalIgnoreCase) ? string.Empty : value.Trim();

    private static List<string> Stations(string value)
        => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();

    private static int Number(string value, string device, string column)
        => int.TryParse(value, out int result) ? result
            : throw new InvalidDataException($"设备 {device} 的 {column} 必须是整数。");

    private static uint ParseLoopbackStart(string value)
    {
        string text = value.Trim();
        string[] parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(p => !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            || byte.Parse(parts[0], CultureInfo.InvariantCulture) != 127)
        {
            throw new InvalidDataException("起始回环 IP 必须是 127.x.x.x 范围内的完整 IPv4 地址，例如 127.0.0.20。");
        }
        return BinaryPrimitives.ReadUInt32BigEndian(parts.Select(p => byte.Parse(p, CultureInfo.InvariantCulture)).ToArray());
    }

    private static string Loopback(uint start, int index)
    {
        ulong value = (ulong)start + (uint)index;
        if (value > 0x7FFFFFFF)
        {
            throw new InvalidDataException("从指定起始地址分配的设备 IP 超出 127.0.0.0～127.255.255.255 回环范围，请调整起始地址。");
        }
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return new IPAddress(bytes).ToString();
    }
}
