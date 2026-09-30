namespace PlcSimulator.Core.Protocols;

/// <summary>
/// 堆垛机（NTI over Socket）的字节布局。
/// <para>
/// 读（状态区）的偏移取自 WCS 的 <c>NTIScBLL.BindScInfoNTI</c>，
/// 写（指令区）的偏移取自 <c>NTIScBLL.SendCodeNTI</c>。
/// 与输送线不同，Socket 链路上**没有任何字节序变换**——偏移就是线缆上的字节位置，
/// 也没有校验和与帧头校验。
/// </para>
/// <para>
/// 三个区段（指令 / 状态 / 报警）在数据区里的基址是模拟器的内部约定：
/// Socket 协议没有「地址」概念，改基址不会改变 WCS 看到的任何一个字节。
/// 真正决定成败的是每种帧的**线长**与**帧内偏移**。
/// </para>
/// </summary>
public static class SrmLayout
{
    // ---- 区段长度 ----

    /// <summary>指令负载长度（WCS 发来的 23 字节）。</summary>
    public const int CommandAreaLength = 23;

    /// <summary>状态区长度，取自数据库 wcs_equipmentinfo.rdblength。</summary>
    public const int StatusAreaLength = 171;

    /// <summary>报警位图长度，WCS 在 6000 端口按前 100 字节扫描。</summary>
    public const int AlarmAreaLength = 100;

    // ---- 三个区段在模拟器数据区里的基址 ----
    // 纯内部约定：Socket 协议没有「地址」概念，改基址不会改变 WCS 看到的任何一个字节，
    // 取整只是为了十六进制转储与点位表好读。

    public const int CommandAreaBase = 0;
    public const int StatusAreaBase = 32;
    public const int AlarmAreaBase = 208;

    // ---- 帧 ----

    /// <summary>指令帧线长：2 字节前导 + 23 字节负载（NTIScBLL.cs:2250-2254）。</summary>
    public const int CommandFrameLength = 26;

    /// <summary>指令负载在帧内的起始偏移。</summary>
    public const int CommandPayloadOffset = 2;

    /// <summary>
    /// 状态帧线长。WCS 的解析器只读到偏移 22，因此 ≥23 即可满足；
    /// 真机的实际长度未确证（代码里那个 74 出自一段从未被使用的日志字符串），故做成配置项。
    /// </summary>
    public const int DefaultStatusFrameLength = 74;

    /// <summary>报警帧线长。WCS 扫描前 100 字节。</summary>
    public const int DefaultAlarmFrameLength = 100;

    /// <summary>
    /// 状态帧的最小可用长度：解析器最大偏移是 22，即至少要 23 字节。
    /// </summary>
    public const int MinStatusFrameLength = 23;

    /// <summary>
    /// 可接受的对接协议别名。数据库 wcs_equipmentinfo.protocoltype 里的原值是 <c>Socket_NTI</c>，
    /// 配置里也允许写简短的 <c>SRM</c>。
    /// </summary>
    public static IReadOnlyList<string> ProtocolTypeAliases { get; } = ["SRM", "Socket_NTI", "S7_NTI", "NTI_S7"];

    // ---- 字段表 ----

    /// <summary>状态区（WCS 读）的字段。偏移即帧内偏移。</summary>
    public static IReadOnlyList<FieldDescriptor> StatusFields { get; } =
    [
        new("reserved", 0, FieldKind.U8),
        new("fork2tasknum", 2, FieldKind.U16),
        new("fork1tasknum", 4, FieldKind.U16),
        new("report", 6, FieldKind.U8),
        new("mode", 7, FieldKind.U8),
        new("forkstatus", 8, FieldKind.U8),
        new("activefork", 9, FieldKind.U8),
        new("actionpoint", 10, FieldKind.U8),
        new("aisle", 11, FieldKind.U8),
        new("row", 12, FieldKind.U8),
        new("column", 13, FieldKind.U8),
        new("cell", 14, FieldKind.U8),
        new("level", 15, FieldKind.U8),
        new("depth", 16, FieldKind.U8),
        new("photo", 18, FieldKind.U8),
        new("face1", 20, FieldKind.U8),
        new("face2", 21, FieldKind.U8),
        new("fire", 22, FieldKind.U8),
    ];

    /// <summary>指令区（WCS 写）的字段。偏移相对 23 字节负载，不含帧前导。</summary>
    public static IReadOnlyList<FieldDescriptor> CommandFields { get; } =
    [
        new("fork2tasknum", 0, FieldKind.U16),
        new("fork1tasknum", 2, FieldKind.U16),
        new("commandtype", 4, FieldKind.U8),
        new("fork1goodstype", 5, FieldKind.U8),
        new("fork2goodstype", 6, FieldKind.U8),
        new("forkno", 7, FieldKind.U8),
        new("actionpoint", 8, FieldKind.U8),
        new("aisle", 9, FieldKind.U8),
        new("row", 10, FieldKind.U8),
        new("column", 11, FieldKind.U8),
        new("cell", 12, FieldKind.U8),
        new("level", 13, FieldKind.U8),
        new("depth", 14, FieldKind.U8),
        new("fireflag", 19, FieldKind.U8),
        new("face1", 21, FieldKind.U8),
        new("face2", 22, FieldKind.U8),
    ];

    public static bool TryGetStatusField(string name, out FieldDescriptor field)
        => LayoutLookup.TryFind(StatusFields, name, out field);

    public static bool TryGetCommandField(string name, out FieldDescriptor field)
        => LayoutLookup.TryFind(CommandFields, name, out field);
}

/// <summary>
/// 指令类型（WCS → 堆垛机）。取值见 WCS 的 <c>NTIScBLL.cs:248-258</c>。
/// </summary>
public static class SrmCommandType
{
    /// <summary>清除指令。取货与放货完成后 WCS 用它收尾。</summary>
    public const byte NoFunc = 0;

    /// <summary>移动/定位指令。火警避让走它，任务号固定 1199。</summary>
    public const byte PosGet = 16;

    /// <summary>盘点/探货指令。</summary>
    public const byte Stock = 48;

    /// <summary>取货指令。</summary>
    public const byte GetC = 112;

    /// <summary>放货指令。</summary>
    public const byte PutC = 128;

    /// <summary>火警避让移动时使用的固定任务号。</summary>
    public const ushort AvoidanceTaskNum = 1199;
}

/// <summary>
/// 作业状态（堆垛机 → WCS），写在状态区偏移 6。
/// 取值见 WCS 的 <c>NTIScBLL.cs:248-258</c>。
/// </summary>
public static class SrmFunctionReport
{
    /// <summary>空闲。</summary>
    public const byte Idle = 0;

    /// <summary>定位/移动中（纯移动指令 CR_POS_GET 的执行态）。</summary>
    public const byte PosGetRunning = 17;

    /// <summary>定位/移动完成。WCS 读到它即对移动指令回一条清除。</summary>
    public const byte PosGetDone = 25;

    /// <summary>盘点/探货执行中。WCS 只把 57 当完成判据，中间值只用于显示。</summary>
    public const byte StockRunning = 48;

    /// <summary>取货前的移动中。</summary>
    public const byte GetPosRunning = 113;

    /// <summary>取货完成。WCS 读到它即下发放货指令。</summary>
    public const byte GetDone = 121;

    /// <summary>放货前的移动中。</summary>
    public const byte PutPosRunning = 129;

    /// <summary>放货完成。WCS 读到它即下发清除指令并结任务。</summary>
    public const byte PutDone = 137;

    /// <summary>探货完成。</summary>
    public const byte StockDone = 57;
}

/// <summary>
/// 工作模式位域，写在状态区偏移 7。
/// </summary>
public static class SrmFunctionMode
{
    public const byte Auto = 0x01;
    public const byte SemiAuto = 0x02;
    public const byte Manual = 0x04;
    public const byte SoftStop = 0x08;

    /// <summary>
    /// 就绪。**作业过程中必须保持置位**：WCS 用
    /// <c>[0]==1 &amp;&amp; [4]==1 &amp;&amp; [7]==0</c> 判设备可用，
    /// 作业时清零会让 WCS 把正在干活的堆垛机判成不可用。
    /// </summary>
    public const byte Ready = 0x10;

    public const byte Fault = 0x80;

    /// <summary>正常待机：自动 + 就绪。</summary>
    public const byte Standby = Auto | Ready;
}

/// <summary>
/// 货叉状态位域，写在状态区偏移 8。位 0 为货叉 1，位 1 为货叉 2。
/// </summary>
public static class SrmForkStatusBits
{
    public const byte Fork1 = 0x01;
    public const byte Fork2 = 0x02;
}
