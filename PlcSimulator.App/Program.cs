using PlcSimulator.App;

ApplicationConfiguration.Initialize();

// 全局兜底：原先 UI 线程一抛异常进程就直接消失，用户只看到窗口没了、毫无线索。
// 只订阅事件、不改 UnhandledExceptionMode——默认的 Automatic 模式下订阅就会生效，
// 显式设成 CatchException 反而会改变 WinForms 的既有行为。
Application.ThreadException += (_, e) => ReportFatal(e.Exception);
AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

// 命令行可以一次给多份配置，各自开一个模拟器窗口。
Application.Run(new MainForm(args));

static void ReportFatal(Exception? exception)
{
    string message = exception?.Message ?? "未知错误";

    try
    {
        MessageBox.Show(
            $"发生未处理的错误：\n\n{message}\n\n{exception?.StackTrace}",
            "错误",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
    catch (Exception)
    {
        // 连弹框都失败就没别的办法了。
    }
}
