using System.Diagnostics;

namespace Rc.Agent;

/// <summary>
/// Minimal, dependency-free, thread-safe file logger. Writes next to the executable and caps the
/// live file at ~2 MB before rotating it to <c>agent.log.1</c>. Logging must never take the agent
/// down, so every I/O failure is swallowed.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string LogPath { get; } = Path.Combine(AppContext.BaseDirectory, "agent.log");

    /// <summary>Set when a debug console is attached (--console) so lines are mirrored to stdout.</summary>
    public static bool ConsoleEnabled { get; set; }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        if (exception is not null)
        {
            Debug.WriteLine(exception.ToString());
        }

        if (ConsoleEnabled)
        {
            try
            {
                Console.WriteLine(exception is null ? line : line + Environment.NewLine + exception);
            }
            catch
            {
                // No usable console; ignore.
            }
        }

        lock (Gate)
        {
            try
            {
                Rotate();
                var text = exception is null
                    ? line + Environment.NewLine
                    : line + Environment.NewLine + exception + Environment.NewLine;
                File.AppendAllText(LogPath, text);
            }
            catch
            {
                // Logging is best-effort only.
            }
        }
    }

    private static void Rotate()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length <= MaxBytes)
        {
            return;
        }

        var backup = LogPath + ".1";
        try
        {
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
            File.Move(LogPath, backup);
        }
        catch
        {
            // If rotation fails we keep appending; the size cap is a soft limit.
        }
    }
}
