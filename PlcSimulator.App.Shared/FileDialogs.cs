namespace PlcSimulator.App.Shared;

/// <summary>
/// 文件对话框的构造入口。**所有文件对话框都要走这里。**
/// <para>
/// 原因：Vista 风格的文件对话框（`AutoUpgradeEnabled = true`，也是默认值）在本机会卡住
/// 不返回——现象是点了浏览按钮之后进程直接无响应、对话框也不出现
/// （`dotnet-stack` 抓到主线程停在 <c>IFileDialog.Show</c> 里出不来）。旧式对话框实测正常。
/// </para>
/// <para>
/// 抽成工厂就是为了别再漏：这条约束写在一处，加新对话框时不会忘。
/// </para>
/// </summary>
public static class FileDialogs
{
    /// <summary>选一个已存在的文件。</summary>
    public static OpenFileDialog Open(string title, string filter) => new()
    {
        Title = title,
        Filter = filter,
        AutoUpgradeEnabled = false,
    };

    /// <summary>选一个保存位置。</summary>
    public static SaveFileDialog Save(string title, string filter) => new()
    {
        Title = title,
        Filter = filter,
        AutoUpgradeEnabled = false,
    };
}
