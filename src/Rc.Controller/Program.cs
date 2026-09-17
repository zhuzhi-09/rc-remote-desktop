namespace Rc.Controller;

internal static class Program
{
    private static readonly object LogLock = new();

    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) => LogException(e.Exception, "UI 线程");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogException(e.ExceptionObject as Exception, "后台线程");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogException(e.Exception, "任务");
            e.SetObserved();
        };

        var config = ControllerConfig.Load();
        try
        {
            Application.Run(new MainForm(config));
        }
        catch (Exception ex)
        {
            LogException(ex, "主窗口");
        }
    }

    /// <summary>Appends an exception to <c>controller-error.log</c>; never throws.</summary>
    internal static void LogException(Exception? exception, string source)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {exception}{Environment.NewLine}";
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "controller-error.log"), line);
            }
        }
        catch
        {
            // Logging must never take the application down.
        }
    }
}
