namespace PlcSimulator.App.Shared;

/// <summary>
/// 会话的启停入口。主窗口与子窗口都走这里，行为才一致：
/// 失败弹框、失败后把半启动态收拾干净。
/// </summary>
public static class SessionCommands
{
    /// <summary>启动。失败时弹框，并补一次停止。</summary>
    public static async Task<bool> StartAsync(SimulatorSession session, IWin32Window owner, Action<string> report)
    {
        try
        {
            await session.StartAsync().ConfigureAwait(true);
            report($"运行中：{session.DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            // 宿主是先起 Modbus、再起 Socket：Socket 抛错时 Modbus 已经起来了，
            // IsRunning 为真但异常已经抛出。补一次停止，别把半启动态留给下一个人。
            await SafeStopAsync(session).ConfigureAwait(true);

            MessageBox.Show(
                owner,
                $"启动失败：{ex.Message}\n\n{session.DisplayName}",
                "错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            report($"启动失败：{session.DisplayName}");
            return false;
        }
    }

    public static async Task<bool> StopAsync(SimulatorSession session, Action<string> report)
    {
        try
        {
            await session.StopAsync().ConfigureAwait(true);
            report($"已停止：{session.DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            report($"停止失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>按当前状态启动或停止。</summary>
    public static async Task<bool> ToggleAsync(SimulatorSession session, IWin32Window owner, Action<string> report)
        => session.IsRunning
            ? await StopAsync(session, report).ConfigureAwait(true)
            : await StartAsync(session, owner, report).ConfigureAwait(true);

    /// <summary>复位全部设备（站台与堆垛机都走宿主那一份实现）。</summary>
    public static void Reset(SimulatorSession session, Action<string> report)
    {
        session.Host.ResetAll();
        report($"已复位：{session.DisplayName}");
    }

    private static async Task SafeStopAsync(SimulatorSession session)
    {
        try
        {
            await session.StopAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 收拾残局时再抛就没意义了。
        }
    }
}
