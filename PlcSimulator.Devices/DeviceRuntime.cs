using PlcSimulator.Core.ByteOrder;
using PlcSimulator.Core.Configuration;
using PlcSimulator.Core.Protocols;
using PlcSimulator.Core.Registers;
using PlcSimulator.Devices.Topology;

namespace PlcSimulator.Devices;

/// <summary>一台设备在运行时的全部状态：数据区、布局模板、字节序策略。</summary>
public sealed class DeviceRuntime
{
    public DeviceRuntime(
        DeviceConfig config,
        IProtocolTemplate protocolTemplate,
        ByteOrderPolicy readPolicy,
        ByteOrderPolicy singleFieldWritePolicy)
    {
        Config = config;
        ProtocolTemplate = protocolTemplate;
        ReadPolicy = readPolicy;
        SingleFieldWritePolicy = singleFieldWritePolicy;

        Space = new DeviceRegisterSpace(
            config.Id,
            config.Blocks.Select(static b => new RegisterBlock(b.Group, b.BaseByteOffset, b.LengthBytes)));

        // 按全部站台建图（含别名站台）：别名站台占着一个坐标位置，建图时去掉它
        // 会把经过该位置的路径整条切断。图只建一次，跑货与路径诊断共用同一张。
        Topology = StationTopology.Build(config.Stations);

        Stations = [.. config.Stations.Select(s => new StationRuntime(this, s))];

        // 现场存在多个站台号共用同一段寄存器的情况：它们其实是同一个物理站台，
        // 只是 WCS 需要用两个图标来显示。这类站台必须共用同一个状态机，
        // 否则同一个任务号会被响应两次、心跳也会被重复置位。
        PhysicalStations = [.. Stations.DistinctBy(static s => (s.ByteOffset, s.Config.LengthBytes))];

        foreach (StationRuntime station in Stations)
        {
            StationRuntime primary = PhysicalStations.First(
                p => p.ByteOffset == station.ByteOffset && p.Config.LengthBytes == station.Config.LengthBytes);

            station.PrimaryStationNo = primary.StationNo;
            station.IsAlias = !ReferenceEquals(primary, station);
        }
    }

    public DeviceConfig Config { get; }

    public IProtocolTemplate ProtocolTemplate { get; }

    /// <summary>模拟器回帧给 WCS 时，把内部表示转成线路表示所用的变换。</summary>
    public ByteOrderPolicy ReadPolicy { get; }

    /// <summary>WCS 单字段写入所用的变换（NTI 下完全不交换，心跳清零走这条路径）。</summary>
    public ByteOrderPolicy SingleFieldWritePolicy { get; }

    /// <summary>
    /// 解读 WCS 写入内容所用的策略，取单字段写口径
    /// （配置里的 <c>singleFieldWriteByteOrderPolicy</c>，默认不做字节交换）。
    /// <para>
    /// 2026-09-23：WCS 侧已修正数据交换规则，写入不再需要高低位转换，
    /// 因此这里回到单字段写口径（恒等）。此前为了忠实暴露 WCS 的缺口，
    /// 曾按整块写口径（偏移 ≥6 交换）解读，会让 <c>to</c> 这类字段读成错值。
    /// </para>
    /// </summary>
    public ByteOrderPolicy IncomingPolicy => SingleFieldWritePolicy;

    public DeviceRegisterSpace Space { get; }

    /// <summary>
    /// 输送拓扑。跑货（<see cref="SimulationEngine.RegisterDevice"/>）与路径诊断共用同一张图，
    /// 免得诊断说的路线与实际走的路线出自两次不同的推导。
    /// </summary>
    public StationTopology Topology { get; }

    public IReadOnlyList<StationRuntime> Stations { get; }

    /// <summary>
    /// 物理上独立的站台。共用一个寄存器区间的站台号只保留一个（按站台号排序的第一个），
    /// 状态机只按这份清单创建。
    /// </summary>
    public IReadOnlyList<StationRuntime> PhysicalStations { get; }

    public StationRuntime? FindStation(string stationNo)
        => Stations.FirstOrDefault(s => string.Equals(s.StationNo, stationNo, StringComparison.Ordinal));
}

/// <summary>
/// 一个站台。
/// <para>
/// **数据区里存的是线路表示**，也就是真实 PLC 寄存器里的字节，
/// 因此协议层收发时不做任何字节序处理，变换全部收敛到本类的字段读写上。
/// 这样协议层保持纯粹，而字节序这个「最大的坑」只有一处实现。
/// </para>
/// <para>
/// 读写字段表是分开的：WCS 读走的值按读布局摆放，WCS 写进来的值按写布局解释
/// （CATL 下这两套偏移并不相同）。
/// </para>
/// </summary>
public sealed class StationRuntime(DeviceRuntime device, StationConfig config)
{
    public DeviceRuntime Device { get; } = device;

    public StationConfig Config { get; } = config;

    public string StationNo => Config.StationNo;

    public string Name => Config.Name;

    public int ByteOffset => Config.ByteOffset;

    public SimulationConfig Simulation => Config.Simulation;

    /// <summary>镜像站台所指向的主站台号；主站台指向自身。</summary>
    public string PrimaryStationNo { get; internal set; } = config.StationNo;

    /// <summary>是否为镜像站台：它和另一个站台号共用同一段寄存器，是同一个物理站台。</summary>
    public bool IsAlias { get; internal set; }

    /// <summary>按 WCS 的**写**布局读取它下发进来的值。</summary>
    public ushort ReadIncomingU16(string fieldName)
    {
        FieldDescriptor field = RequireWriteField(fieldName);
        Span<byte> buffer = stackalloc byte[2];
        Device.Space.TryReadBytes(ByteOffset + field.ByteOffset, buffer);
        UndoWireTransform(buffer, field, Device.IncomingPolicy);
        return (ushort)((buffer[0] << 8) | buffer[1]);
    }

    /// <summary>按 WCS 的**读**布局写入它将读到的值。</summary>
    public void WriteOutgoingU16(string fieldName, ushort value)
    {
        FieldDescriptor field = RequireReadField(fieldName);
        Span<byte> buffer = stackalloc byte[2];
        buffer[0] = (byte)(value >> 8);
        buffer[1] = (byte)(value & 0xFF);
        ApplyWireTransform(buffer, field, Device.ReadPolicy);
        Device.Space.TryWriteBytes(ByteOffset + field.ByteOffset, buffer);
    }

    public void WriteOutgoingU8(string fieldName, byte value)
    {
        FieldDescriptor field = RequireReadField(fieldName);
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        Device.Space.TryWriteBytes(ByteOffset + field.ByteOffset, buffer);
    }

    public byte ReadIncomingU8(string fieldName)
    {
        FieldDescriptor field = RequireWriteField(fieldName);
        Span<byte> buffer = stackalloc byte[1];
        Device.Space.TryReadBytes(ByteOffset + field.ByteOffset, buffer);
        return buffer[0];
    }

    /// <summary>按 WCS 的**写**布局读取条码。</summary>
    public string ReadIncomingAscii(string fieldName)
    {
        FieldDescriptor field = RequireWriteField(fieldName);
        Span<byte> buffer = stackalloc byte[Math.Max(1, field.Length)];
        Device.Space.TryReadBytes(ByteOffset + field.ByteOffset, buffer);
        UndoWireTransform(buffer, field, Device.IncomingPolicy);
        return DecodeAscii(buffer);
    }

    /// <summary>按 WCS 的**读**布局写入条码。</summary>
    public void WriteOutgoingAscii(string fieldName, string value)
    {
        FieldDescriptor field = RequireReadField(fieldName);
        Span<byte> buffer = stackalloc byte[Math.Max(1, field.Length)];
        buffer.Clear();

        int count = Math.Min(value.Length, field.Length);
        for (int i = 0; i < count; i++)
        {
            buffer[i] = (byte)value[i];
        }

        ApplyWireTransform(buffer, field, Device.ReadPolicy);
        Device.Space.TryWriteBytes(ByteOffset + field.ByteOffset, buffer);
    }

    /// <summary>清空 WCS 写入侧的某字段。</summary>
    public void ClearIncoming(string fieldName)
    {
        FieldDescriptor field = RequireWriteField(fieldName);
        Device.Space.TryClearRange(ByteOffset + field.ByteOffset, field.SizeInBytes);
    }

    /// <summary>随货移动的任务字段。心跳是对握手，不属于随货信息，因此不在其列。</summary>
    private static readonly string[] TaskFieldNames = ["tasknum", "goodstype", "from", "to", "barcode"];

    /// <summary>
    /// 接收来自上游站台的货物：把整份任务信息（任务号、条码、货物类型、来源站台、目标站台）
    /// 一并写进来。由上游站台的状态机在货物送达时调用；「有货」状态由状态机自身维护。
    /// </summary>
    public void AcceptCargo(
        ushort taskNum,
        string barcode,
        ushort goodsType,
        string fromStationNo,
        string toStationNo)
    {
        WriteOutgoingNumber("tasknum", taskNum);
        WriteOutgoingAscii("barcode", barcode);
        WriteOutgoingNumber("goodstype", goodsType);

        if (ushort.TryParse(fromStationNo, out ushort from))
        {
            WriteOutgoingNumber("from", from);
        }

        if (ushort.TryParse(toStationNo, out ushort to))
        {
            WriteOutgoingNumber("to", to);
        }
    }

    /// <summary>
    /// 按字段自身宽度写一个数。不能一律按 16 位写：CATL 的读布局里 goodstype 是单字节，
    /// 按 2 字节写会踩坏相邻的那个字节。
    /// </summary>
    private void WriteOutgoingNumber(string fieldName, ushort value)
    {
        if (Device.ProtocolTemplate.TryGetReadField(fieldName, out FieldDescriptor field) && field.SizeInBytes == 1)
        {
            WriteOutgoingU8(fieldName, (byte)value);
            return;
        }

        WriteOutgoingU16(fieldName, value);
    }

    /// <summary>
    /// 清空本站的任务信息。读写两侧都清，确保货物离开后不留残余：
    /// WCS 不会再读到旧的任务号与目标站台，状态机也不会把它当成待执行的任务。
    /// </summary>
    public void ClearTaskFields()
    {
        foreach (string name in TaskFieldNames)
        {
            if (Device.ProtocolTemplate.TryGetReadField(name, out FieldDescriptor readField))
            {
                Device.Space.TryClearRange(ByteOffset + readField.ByteOffset, readField.SizeInBytes);
            }

            if (Device.ProtocolTemplate.TryGetWriteField(name, out FieldDescriptor writeField))
            {
                Device.Space.TryClearRange(ByteOffset + writeField.ByteOffset, writeField.SizeInBytes);
            }
        }
    }

    // ---- 读回模拟器摆放给 WCS 的值，供 GUI 点位表显示（内部表示，非线路字节）----

    public ushort ReadOutgoingU16(string fieldName)
    {
        FieldDescriptor field = RequireReadField(fieldName);
        Span<byte> buffer = stackalloc byte[2];
        Device.Space.TryReadBytes(ByteOffset + field.ByteOffset, buffer);
        ApplyWireTransform(buffer, field, Device.ReadPolicy);
        return (ushort)((buffer[0] << 8) | buffer[1]);
    }

    public byte ReadOutgoingU8(string fieldName)
    {
        FieldDescriptor field = RequireReadField(fieldName);
        Span<byte> buffer = stackalloc byte[1];
        Device.Space.TryReadBytes(ByteOffset + field.ByteOffset, buffer);
        return buffer[0];
    }

    public string ReadOutgoingAscii(string fieldName)
    {
        FieldDescriptor field = RequireReadField(fieldName);
        Span<byte> buffer = stackalloc byte[Math.Max(1, field.Length)];
        Device.Space.TryReadBytes(ByteOffset + field.ByteOffset, buffer);
        ApplyWireTransform(buffer, field, Device.ReadPolicy);
        return DecodeAscii(buffer);
    }

    /// <summary>列出本站台涉及的读写字段并集，读写同名时以读布局为准。</summary>
    public IReadOnlyList<FieldDescriptor> EnumerateFields()
    {
        List<FieldDescriptor> fields = [.. Device.ProtocolTemplate.ReadFields];

        foreach (FieldDescriptor field in Device.ProtocolTemplate.WriteFields)
        {
            if (!fields.Exists(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase)))
            {
                fields.Add(field);
            }
        }

        return fields;
    }

    /// <summary>内部表示 → 线路表示。</summary>
    private static void ApplyWireTransform(Span<byte> buffer, FieldDescriptor field, ByteOrderPolicy policy)
        => SwapIfNeeded(buffer, field, policy);

    /// <summary>
    /// 线路表示 → 内部表示。字交换是自逆的，因此与编码方向同一个操作；
    /// 若将来出现非自逆策略，这里改为调用策略的逆变换即可。
    /// </summary>
    private static void UndoWireTransform(Span<byte> buffer, FieldDescriptor field, ByteOrderPolicy policy)
        => SwapIfNeeded(buffer, field, policy);

    /// <summary>
    /// 字段落在「需要字交换」的区域内时逐对反转。
    /// 字段边界与变换对齐一致（偶数偏移、偶数宽度），因此可以按字段独立施加，不需要整块参与。
    /// </summary>
    private static void SwapIfNeeded(Span<byte> buffer, FieldDescriptor field, ByteOrderPolicy policy)
    {
        if (policy.IsIdentity || !policy.CoversField(field))
        {
            return;
        }

        for (int i = 0; i + 1 < buffer.Length; i += 2)
        {
            (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
        }
    }

    private FieldDescriptor RequireReadField(string fieldName)
    {
        if (Device.ProtocolTemplate.TryGetReadField(fieldName, out FieldDescriptor field))
        {
            return field;
        }

        throw new InvalidOperationException(
            $"对接协议 {Device.ProtocolTemplate.Name} 中没有读字段 \"{fieldName}\"。");
    }

    private FieldDescriptor RequireWriteField(string fieldName)
    {
        if (Device.ProtocolTemplate.TryGetWriteField(fieldName, out FieldDescriptor field))
        {
            return field;
        }

        throw new InvalidOperationException(
            $"对接协议 {Device.ProtocolTemplate.Name} 中没有写字段 \"{fieldName}\"。");
    }

    private static string DecodeAscii(ReadOnlySpan<byte> buffer)
    {
        int end = buffer.Length;
        while (end > 0 && (buffer[end - 1] == 0 || buffer[end - 1] == (byte)' '))
        {
            end--;
        }

        Span<char> chars = stackalloc char[Math.Max(1, end)];
        for (int i = 0; i < end; i++)
        {
            chars[i] = (char)buffer[i];
        }

        return new string(chars[..end]);
    }
}
