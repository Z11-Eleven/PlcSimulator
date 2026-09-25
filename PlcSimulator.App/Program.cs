namespace PlcSimulator.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 没给路径时由调试台自己决定：先用上次在界面上打开过的配置，再退回默认路径。
        Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
    }
}
