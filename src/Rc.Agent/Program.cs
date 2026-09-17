using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rc.Agent;

internal static class Program
{
    private const string MutexName = "rcagent.single-instance";

    private static readonly IntPtr DpiAwarenessContextPerMonitorV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [STAThread]
    private static void Main(string[] args)
    {
        var consoleRequested = args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
        if (consoleRequested && AllocConsole())
        {
            RedirectConsole();
            Log.ConsoleEnabled = true;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isNew);
        if (!isNew)
        {
            Log.Warn("Another instance of rcagent is already running; exiting.");
            return;
        }

        EnablePerMonitorDpiAwareness();
        InstallExceptionHandlers();

        var config = AgentConfig.LoadOrCreate();
        Log.Info($"rcagent starting (agentId={config.AgentId}, url={config.ResolveRelayUrl()}).");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var context = new AgentContext(config);
        Application.Run(context);

        Log.Info("rcagent stopped.");
    }

    private static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            // The application manifest already requests PerMonitorV2; this call is a best-effort
            // fallback for hosts that ignore the manifest. It fails harmlessly when already set.
            SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorV2);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set DPI awareness programmatically: {ex.Message}");
        }
    }

    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI-thread exception.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled domain exception.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };
    }

    private static void RedirectConsole()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}
