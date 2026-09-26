using PlcSimulator.Core.Configuration;

namespace PlcSimulator.App.Shared;

/// <summary>从磁盘加载一份配置并建会话。</summary>
public static class SessionFactory
{
    /// <summary>
    /// 读盘 → 校验 → 建会话。任一步失败都返回 false，并给出**可直接展示**的原因。
    /// <para>
    /// 这里不弹框：弹框是界面的事，而这句话主窗口打开配置、子窗口重载都要用，文案得一致。
    /// </para>
    /// </summary>
    public static bool TryLoad(string path, out SimulatorSession session, out string failure)
    {
        session = null!;
        failure = string.Empty;

        ConfigLoadResult result;

        try
        {
            result = ConfigLoader.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            failure = $"加载配置失败：\n{ex.Message}\n\n路径：{System.IO.Path.GetFullPath(path)}";
            return false;
        }

        if (!result.IsValid)
        {
            failure = $"配置校验未通过（{System.IO.Path.GetFileName(path)}）：\n\n"
                + string.Join("\n", result.Errors.Select(static e => "· " + e));
            return false;
        }

        session = new SimulatorSession(path, result);
        return true;
    }
}
