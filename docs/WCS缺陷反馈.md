# WCS 侧 Modbus 实现问题反馈

> **状态：四条均已由用户在 2026-09-23 修复。**
> 本文保留完整记录，供日后核对或回归时参考。

**反馈对象**：WCS 开发
**代码基线**：`WCS_6.6_20260911/WCS_6.6/WCS/WCS 6.6/`
**发现方式**：按现场协议与 Modbus 标准搭建 PLC 模拟器作为参照系，逐项核对 WCS 行为。
所有结论都可用模拟器独立复现，不依赖真实 PLC。

问题按严重程度排序。

---

## 问题 1：单字段写缺少高低位转换（严重——导致握手无法完成）

**现象**
写入目标站台 `5555`（`0x15B3`），PLC 侧读到 `45845`（`0xB315`），正好是高低字节互换。

**代码位置**

- 单字段写：`CWCS.Core.Library/PLCManager/ConveryPLC.cs:1003-1007`
- 整块写（含转换）：`ConveryPLC.cs:867-872`

**原因**
同一份数据存在两套字节序约定：

| 路径 | 对偏移 ≥ 6 的字节 |
|---|---|
| 整块写 | **做**字交换（`Array.Copy(byt, 6, ...)` 后逐对反转） |
| 单字段写 | **不做**（`ConvertHelper.GetHexBytes` 大端直转） |

NTI 协议实际只走单字段写（`ConveryPLC.cs:764-766`），因此整块写那套交换从不生效。

**影响范围**（按 `WriteType` 的块内字节偏移）

| 字段 | 偏移 | 受影响 |
|---|---|---|
| TaskNum | 0 | 否 |
| GoodType | 2 | 否 |
| FromStation | 4 | 否 |
| **ToStation** | **6** | **是** |
| **Heartbeat** | **8** | **是** |
| **Barcode** | **12-27** | **是** |

**实际后果**
心跳位于偏移 8，同样缺转换。若 PLC 按交换约定存放，写 `2` 会变成 `0x0200`：

```
→ FC=0x06 addr=34 qty=1 → 1001.heartbeat payload=[00 02] 值=2｜交换后=512
[1001] 待清零 - 动作完成，心跳已置 1，等待 WCS 清零
[1001] 待清零 - 等待 WCS 清零超时
```

**握手无法完成**：站台永远停在「待清零」，WCS 侧一直等待，业务流转中断。

**复现**：`PlcSimulator/docs/WCS检验清单.md` 检验 2（有可直接执行的脚本）。

**建议**：单字段写路径对偏移 ≥ 6 的字段做与整块写一致的字交换，或统一两处约定。

---

## 问题 2：站台数据解析偏移（严重——界面数据错位）

**现象**
WCS 界面上相邻站台的状态呈「正常／故障」交替。

**代码位置**：`ConveryPLC.cs:371`

```csharp
byte[] bytetemp = new byte[si.length];
Array.Copy(byt, si.value - startValue, bytetemp, 0, si.length);
```

**原因**
`Array.Copy` 的第二个参数是**字节下标**，而 `si.value - startValue` 是**寄存器差**——少乘了 2。
（`value` 是寄存器地址这一点已由 WCS 实际发出的读地址证实：读范围 `[30, 19260)` 与数据库
`value` 的范围 `30 → 19230` 完全吻合。）

**影响**
WCS 取数位置只有正确位置的一半，每隔一个站台错开一次。

**复现**：用 `PlcSimulator/config/simulator.production.json` 启动模拟器（站台按 Modbus 标准语义
布置：数据位于寄存器 `value`、占 `signaltype` 个寄存器），连接 WCS 后观察站台状态。

---

## 问题 3：状态字 Fault 位判定相反（严重——全员误报故障）

**现场协议**（设备状态字 = 站台字节 28-29 组成的 16 位）

| 位 | 名称 | 含义 |
|---|---|---|
| X8 | StaLoad | 0 = 有货，1 = 无货 |
| X9 | AUTO | 1 = 自动，0 = 手动 |
| X10 | Fault | **1 = 有故障** |
| X11-X14 | CW / CCW / UP / DN | 正转 / 反转 / 上升 / 下降 |
| X0-X7、X15 | Element_9..16 / Element_8 | 备用 |

**代码位置**：`ConveryPLC.cs:414`

```csharp
si.C_TaskInfo.faultflag = statusArr[10] == 0 ? 1 : 0;
```

**原因**
按协议 X10 = 1 表示有故障，此处把 **0** 当成了有故障。

**影响**
正常站台的 X10 = 0，会被判定为「有故障」——**所有正常站台都会被误报为故障**。

**说明**：同一段的 X8（`ConveryPLC.cs:412`）与 X9（`413`）判定与协议一致，无需修改。

---

## 问题 4：WebSocket 推送的载货状态相反（中等——两侧显示不一致）

**代码位置**：`CWCS.WebBridge/Common/BridgeWebSocketClient.cs:90`

```csharp
LoadState = item.checkinfo == 2 ? "unloaded" : "cargo",
```

**原因**
`checkinfo`（`ConveryPLC.cs:408`）中 **2 表示有货、1 表示无货**，此处映射写反了：
`checkinfo == 2`（有货）被映射为 `"unloaded"`（无货）。

**对照**：桌面端同一数据的映射是正确的（`FrmConveryManager.cs:1191`）

```csharp
cs.checkinfo.ToString() == "1" ? "空闲" : "载货"
```

**影响**
同一时刻、同一份数据，Web 推送与桌面端显示相反。

---

## 附：检验方式

模拟器按现场协议与 Modbus 标准实现，可独立复现上述问题，不依赖真实 PLC。

- 启动：`dotnet run --project PlcSimulator/PlcSimulator.Cli -- serve --config PlcSimulator/config/simulator.production.json --log-file PlcSimulator/logs/verify.log`
- 完整检验步骤（含可执行脚本与真实预期输出）：`PlcSimulator/docs/WCS检验清单.md`
- 各问题的详细分析：`PlcSimulator/README.md` → 「待确认的 WCS 侧疑点」
