using PlcSimulator.Core.Protocols;
using PlcSimulator.Devices.Srm;

namespace PlcSimulator.Devices.Tests;

public class SrmMachineTests
{
    [Fact]
    public void NewMachine_ReportsStandbyModeAndIdle()
    {
        (SrmDeviceRuntime runtime, _) = SrmFixtures.CreateMachine();

        Assert.Equal(SrmFunctionReport.Idle, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Equal(SrmFunctionMode.Standby, SrmFixtures.ReadFunctionMode(runtime));
        Assert.Equal(0, SrmFixtures.ReadStatusByte(runtime, 8));   // 两个货叉都无货
        Assert.Equal(0, SrmFixtures.ReadStatusByte(runtime, 22));  // 无火警
    }

    [Fact]
    public void ApplyCommand_Getc_Reports113Then121_AndMarksForkLoaded()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(
            travelDelayMs: 100, actionDelayMs: 100);

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.GetC, taskNum: 1001, actionPoint: 2));
        machine.Tick(SrmFixtures.Tick);

        Assert.Equal(SrmFunctionReport.GetPosRunning, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Equal(1001, SrmFixtures.ReadStatusU16(runtime, 4));

        SrmFixtures.Advance(machine, 250);

        Assert.Equal(SrmFunctionReport.GetDone, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Equal(SrmForkStatusBits.Fork1, SrmFixtures.ReadStatusByte(runtime, 8));
        Assert.Equal(1, SrmFixtures.ReadStatusByte(runtime, 18));   // 光电：有货
    }

    [Fact]
    public void ApplyCommand_PutcAfterPickDone_Reports129Then137_AndClearsForkLoaded()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(
            travelDelayMs: 100, actionDelayMs: 100);

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.GetC, taskNum: 1001, actionPoint: 2));
        Assert.True(SrmFixtures.AdvanceUntil(machine, SrmFunctionReport.GetDone));

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.PutC, taskNum: 1001, actionPoint: 3));
        machine.Tick(SrmFixtures.Tick);

        Assert.Equal(SrmFunctionReport.PutPosRunning, SrmFixtures.ReadFunctionReport(runtime));

        SrmFixtures.Advance(machine, 250);

        Assert.Equal(SrmFunctionReport.PutDone, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Equal(0, SrmFixtures.ReadStatusByte(runtime, 8));    // 货叉已卸空
        Assert.Equal(0, SrmFixtures.ReadStatusByte(runtime, 18));
    }

    [Fact]
    public void ApplyCommand_NoFunc_ReturnsToIdle_AndClearsTaskNum()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(
            travelDelayMs: 100, actionDelayMs: 100);

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.GetC, taskNum: 1001, actionPoint: 2));
        Assert.True(SrmFixtures.AdvanceUntil(machine, SrmFunctionReport.GetDone));

        machine.SubmitCommand(SrmFixtures.ClearCommand());
        machine.Tick(SrmFixtures.Tick);

        Assert.Equal(SrmFunctionReport.Idle, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Equal(0, SrmFixtures.ReadStatusU16(runtime, 4));
        Assert.Equal(0, SrmFixtures.ReadStatusU16(runtime, 2));
        Assert.Equal(0, SrmFixtures.ReadStatusByte(runtime, 9));    // 当前动作货叉归零
    }

    [Fact]
    public void ApplyCommand_Stock_ReportsStockDone()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(
            travelDelayMs: 100, actionDelayMs: 100);

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.Stock, taskNum: 3001, actionPoint: 4));
        machine.Tick(SrmFixtures.Tick);

        Assert.Equal(SrmFunctionReport.StockRunning, SrmFixtures.ReadFunctionReport(runtime));

        SrmFixtures.Advance(machine, 250);

        Assert.Equal(SrmFunctionReport.StockDone, SrmFixtures.ReadFunctionReport(runtime));
    }

    [Fact]
    public void ApplyCommand_PosGet_ReportsPosGetDone()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(
            travelDelayMs: 100, actionDelayMs: 100);

        machine.SubmitCommand(SrmFixtures.Command(
            SrmCommandType.PosGet, taskNum: SrmCommandType.AvoidanceTaskNum, actionPoint: 5));
        machine.Tick(SrmFixtures.Tick);

        Assert.Equal(SrmFunctionReport.PosGetRunning, SrmFixtures.ReadFunctionReport(runtime));

        SrmFixtures.Advance(machine, 250);

        Assert.Equal(SrmFunctionReport.PosGetDone, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Equal(5, SrmFixtures.ReadStatusByte(runtime, 10));   // 已移到目标动作点
    }

    [Fact]
    public void ApplyCommand_Fork2_DoesNotDisturbFork1()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(
            travelDelayMs: 100, actionDelayMs: 100);

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.GetC, taskNum: 2002, forkNo: 2, actionPoint: 4));
        Assert.True(SrmFixtures.AdvanceUntil(machine, SrmFunctionReport.GetDone));

        // 货叉 2 载货、货叉 1 仍空闲；两个工位任务号各自独立。
        Assert.Equal(SrmForkStatusBits.Fork2, SrmFixtures.ReadStatusByte(runtime, 8));
        Assert.Equal(0, SrmFixtures.ReadStatusU16(runtime, 4));
        Assert.Equal(2002, SrmFixtures.ReadStatusU16(runtime, 2));
    }

    [Fact]
    public void ApplyCommand_GetcWhileForkHoldsCargo_IsIgnored()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(
            travelDelayMs: 100, actionDelayMs: 100);

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.GetC, taskNum: 1001, actionPoint: 2));
        Assert.True(SrmFixtures.AdvanceUntil(machine, SrmFunctionReport.GetDone));

        // 取货完成后应等 WCS 的放货指令，此时再发取货要被忽略而不是重开一轮。
        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.GetC, taskNum: 9999, actionPoint: 5));
        machine.Tick(SrmFixtures.Tick);

        Assert.Equal(SrmFunctionReport.GetDone, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Equal(1001, SrmFixtures.ReadStatusU16(runtime, 4));
        Assert.Contains("已忽略", machine.LastEvent);
    }

    [Fact]
    public void ApplyCommand_UnknownType_IsIgnoredWithoutException()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine();

        machine.SubmitCommand(SrmFixtures.Command(commandType: 0x7F));
        machine.Tick(SrmFixtures.Tick);

        Assert.Equal(SrmFunctionReport.Idle, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Contains("未识别", machine.LastEvent);
    }

    [Fact]
    public void Manual_True_ClearsAutoAndReadyBits()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine();

        machine.Manual = true;

        // 打手动后 WCS 会判设备不可用：自动位与就绪位都要清掉。
        byte mode = SrmFixtures.ReadFunctionMode(runtime);
        Assert.Equal(0, mode & SrmFunctionMode.Auto);
        Assert.Equal(0, mode & SrmFunctionMode.Ready);
        Assert.Equal(SrmFunctionMode.Manual, mode & SrmFunctionMode.Manual);
    }

    [Fact]
    public void MarkFaulted_SetsFaultBit()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine();

        machine.MarkFaulted("测试故障");

        Assert.Equal(SrmFunctionMode.Fault, SrmFixtures.ReadFunctionMode(runtime));
    }

    [Fact]
    public void Tick_DuringTask_KeepsReadyBit()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(
            travelDelayMs: 10_000, actionDelayMs: 10_000);

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.GetC, taskNum: 1001));
        machine.Tick(SrmFixtures.Tick);

        Assert.Equal(SrmFunctionReport.GetPosRunning, SrmFixtures.ReadFunctionReport(runtime));

        // 作业中 WCS 仍要读到「自动 + 就绪」，否则它会把正在干活的堆垛机判成设备不可用。
        Assert.Equal(SrmFunctionMode.Standby, SrmFixtures.ReadFunctionMode(runtime));
    }

    [Fact]
    public void Reset_RestoresConfiguredInitialValues()
    {
        (SrmDeviceRuntime runtime, SrmMachine machine) = SrmFixtures.CreateMachine(auto: false);

        Assert.Equal(SrmFunctionMode.Manual, SrmFixtures.ReadFunctionMode(runtime));

        machine.SubmitCommand(SrmFixtures.Command(SrmCommandType.GetC, taskNum: 1001));
        Assert.True(SrmFixtures.AdvanceUntil(machine, SrmFunctionReport.GetDone));

        machine.Reset();

        Assert.Equal(SrmFunctionReport.Idle, SrmFixtures.ReadFunctionReport(runtime));
        Assert.Equal(SrmFunctionMode.Manual, SrmFixtures.ReadFunctionMode(runtime));
        Assert.Equal(0, SrmFixtures.ReadStatusU16(runtime, 4));
        Assert.Equal(0, SrmFixtures.ReadStatusByte(runtime, 8));
    }

    [Fact]
    public void OnCommandFrame_GivenFullFrame_WritesPayloadAndQueuesCommand()
    {
        (SrmDeviceRuntime runtime, _) = SrmFixtures.CreateMachine();

        SrmCommand command = SrmFixtures.Command(SrmCommandType.GetC, taskNum: 1001, actionPoint: 2);
        byte[] frame = new byte[SrmLayout.CommandFrameLength];
        SrmFrames.BuildCommandFrame(command, frame);

        runtime.OnCommandFrame(frame);

        byte[] payload = new byte[SrmLayout.CommandAreaLength];
        Assert.True(runtime.Space.TryReadBytes(SrmLayout.CommandAreaBase, payload));

        Assert.Equal(SrmCommandType.GetC, payload[4]);
        Assert.Equal(0x03, payload[2]);   // 任务号 1001 = 0x03E9，大端
        Assert.Equal(0xE9, payload[3]);
        Assert.Equal(2, payload[8]);      // 动作点
        Assert.Equal(1, runtime.PendingCommandCount);
    }

    [Fact]
    public void OnCommandFrame_GivenShortFrame_IsIgnored()
    {
        (SrmDeviceRuntime runtime, _) = SrmFixtures.CreateMachine();

        byte[] tooShort = new byte[SrmLayout.CommandFrameLength - 1];

        runtime.OnCommandFrame(tooShort);

        Assert.Equal(0, runtime.PendingCommandCount);
    }
}
