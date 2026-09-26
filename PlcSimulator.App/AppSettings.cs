using System.Text.Json;

namespace PlcSimulator.App;

/// <summary>
/// 调试台的界面状态（上次用过的路径），存在 <c>%APPDATA%\PlcSimulator\settings.json</c>。
/// <para>
/// 只记路径，不记导入参数（IP 覆盖、延时之类）：那些每次生成时按需填，
/// 若在下次启动时"复活"反而容易把过期的值带进新配置。
/// </para>
/// </summary>
internal sealed class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlcSimulator");

    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// 上次打开的那几份配置；下次启动命令行没给路径时全部自动加载。
    /// <para>
    /// 调试台支持同时挂多份（比如输送线一份、堆垛机一份），所以这里记的是列表而不是单个路径。
    /// </para>
    /// </summary>
    public List<string> OpenConfigPaths { get; set; } = [];

    /// <summary>
    /// 旧版只记得住一份配置。这个字段只为读得懂老设置文件而留：
    /// <see cref="Load"/> 会把它迁进 <see cref="OpenConfigPaths"/> 后清空，不再写回。
    /// </summary>
    public string? LastConfigPath { get; set; }

    /// <summary>「配置生成」页上次选的 CSV 导出文件。</summary>
    public string? LastCsvPath { get; set; }

    /// <summary>「配置生成」页上次的输出路径。</summary>
    public string? LastOutputPath { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return new AppSettings();
            }

            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile))
                ?? new AppSettings();

            // 老设置文件里只有单个 LastConfigPath：不迁移的话，用户升级后
            // 上次打开的配置会悄悄"丢失"，退回默认那份。
            if (settings.OpenConfigPaths.Count == 0 && !string.IsNullOrWhiteSpace(settings.LastConfigPath))
            {
                settings.OpenConfigPaths.Add(settings.LastConfigPath);
                settings.LastConfigPath = null;
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 设置文件坏了不该拦住调试台启动，按默认值继续。
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(
                SettingsFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不下只影响下次启动的便利性，不值得打断用户。
        }
    }
}
