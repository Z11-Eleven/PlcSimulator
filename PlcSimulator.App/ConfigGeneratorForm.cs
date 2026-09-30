using PlcSimulator.App.Views;

namespace PlcSimulator.App;

/// <summary>
/// 「配置生成」的对话框壳。
/// <para>
/// 可读取 wcs_opcitem（输送线点位表）或 wcs_equipmentinfo（堆垛机设备表），入口放在主窗口：
/// 头一次上手时一份配置都还没有，而子窗口是以已有配置为前提才能开的——放在子窗口里的话，
/// 最需要它的时候反而用不上。顺带输送机项目也不必去引用 Import 这个 Exe 项目。
/// </para>
/// </summary>
internal sealed class ConfigGeneratorForm : Form
{
    private readonly ConfigImportView _view = new();

    public ConfigGeneratorForm(string csvPath, string outputPath)
    {
        Text = "配置生成";
        MinimumSize = new Size(720, 480);
        Size = new Size(880, 640);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        _view.Dock = DockStyle.Fill;
        _view.PrefillPaths(csvPath, outputPath);

        Controls.Add(_view);
    }

    /// <summary>生成完成时触发，参数是生成出来的配置路径。</summary>
    public event EventHandler<string>? ConfigGenerated
    {
        add => _view.ConfigGenerated += value;
        remove => _view.ConfigGenerated -= value;
    }

    /// <summary>当前填的输入/输出路径，主窗口据此记住上次的选择。</summary>
    public (string CsvPath, string OutputPath) ReadPaths() => _view.ReadPaths();
}
