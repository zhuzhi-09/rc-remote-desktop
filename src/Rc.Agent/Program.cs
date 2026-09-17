using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Rc.Agent;

internal static class Program
{
    private const string MutexName = "rcagent.single-instance";
    private const string AutostartFlag = "--autostart";

    private static readonly IntPtr DpiAwarenessContextPerMonitorV2 = new(-4);

    private enum AutostartAction
    {
        Status,
        On,
        Off,
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [STAThread]
    private static int Main(string[] args)
    {
        var consoleRequested = args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
        if (consoleRequested && AllocConsole())
        {
            RedirectConsole();
            Log.ConsoleEnabled = true;
        }

        // Scriptable auto-start management: handled before any UI (and before the single-instance
        // mutex, so a status query works while the agent is running).
        if (TryHandleAutostart(args, out var autostartExitCode))
        {
            return autostartExitCode;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isNew);
        if (!isNew)
        {
            Log.Warn("Another instance of rcagent is already running; exiting.");
            return 0;
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
        return 0;
    }

    /// <summary>
    /// Handles <c>--autostart status|on|off</c> (value as a separate argument or via
    /// <c>--autostart=...</c>) and the <c>--autostart-status</c> / <c>--autostart-on</c> /
    /// <c>--autostart-off</c> aliases. Returns false when no recognized auto-start invocation is
    /// present so the caller falls through to the normal startup path unchanged.
    /// </summary>
    private static bool TryHandleAutostart(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!TryParseAutostart(args, out var action))
        {
            return false;
        }

        UseUtf8ForRedirectedOutput();

        switch (action)
        {
            case AutostartAction.Status:
                WriteConsoleLine(Autostart.IsEnabled()
                    ? $"autostart: on ({Autostart.CurrentTarget()})"
                    : "autostart: off");
                return true;

            case AutostartAction.On:
                if (!Autostart.TrySet(true, out var enableError))
                {
                    WriteConsoleLine("autostart: failed: " + enableError);
                    exitCode = 1;
                    return true;
                }

                WriteConsoleLine($"autostart: on ({Autostart.CurrentTarget()})");
                return true;

            case AutostartAction.Off:
                if (!Autostart.TrySet(false, out var disableError))
                {
                    WriteConsoleLine("autostart: failed: " + disableError);
                    exitCode = 1;
                    return true;
                }

                WriteConsoleLine("autostart: off");
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseAutostart(string[] args, out AutostartAction action)
    {
        action = AutostartAction.Status;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, AutostartFlag, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length && TryParseAutostartValue(args[i + 1], out action);
            }

            if (arg.StartsWith(AutostartFlag + "=", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseAutostartValue(arg[(AutostartFlag.Length + 1)..], out action);
            }

            if (string.Equals(arg, "--autostart-status", StringComparison.OrdinalIgnoreCase))
            {
                action = AutostartAction.Status;
                return true;
            }

            if (string.Equals(arg, "--autostart-on", StringComparison.OrdinalIgnoreCase))
            {
                action = AutostartAction.On;
                return true;
            }

            if (string.Equals(arg, "--autostart-off", StringComparison.OrdinalIgnoreCase))
            {
                action = AutostartAction.Off;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseAutostartValue(string value, out AutostartAction action)
    {
        action = AutostartAction.Status;

        if (string.Equals(value, "status", StringComparison.OrdinalIgnoreCase))
        {
            action = AutostartAction.Status;
            return true;
        }

        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            action = AutostartAction.On;
            return true;
        }

        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            action = AutostartAction.Off;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Scripts and CI pipes expect UTF-8, while the default for a WinExe is the system code page.
    /// Only applies when stdout is redirected; a real console keeps its own code page.
    /// </summary>
    private static void UseUtf8ForRedirectedOutput()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
        }
        catch (Exception)
        {
            // Encoding is a nicety; the command result is still reported through the exit code.
        }
    }

    /// <summary>
    /// Writes one line of CLI output. The process is a WinExe, so this never allocates a console:
    /// output appears only when one already exists (launched from a terminal or with --console) or
    /// when stdout is redirected by a script/pipe.
    /// </summary>
    private static void WriteConsoleLine(string line)
    {
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero && !Console.IsOutputRedirected)
            {
                return;
            }

            Console.WriteLine(line);
        }
        catch (IOException)
        {
            // The exit code still reports the result even if the console went away.
        }
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
