using Microsoft.Win32;

namespace Rc.Agent;

/// <summary>
/// Per-user "start with Windows" support backed by the HKCU Run key. Using the current-user hive
/// keeps the setting administrator-free; the machine hive, the Task Scheduler and any COM shortcut
/// creation are deliberately not used. Legacy launchers left in the user's Startup folder by older
/// versions are cleaned up so the agent can never end up starting twice.
/// </summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RcAgent";

    private static readonly string[] LegacyStartupFiles = { "rcagent.lnk", "rcagent.cmd" };

    /// <summary>
    /// True when the Run value exists and its command resolves to this executable. A stale value
    /// that points at a different path (the agent was moved) counts as disabled.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            var target = Normalize(CurrentTarget());
            return target.Length > 0
                && string.Equals(target, ExecutablePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The raw command currently stored in the Run value, or "" when the value is absent.</summary>
    public static string CurrentTarget()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Enables or disables auto-start. Never throws: on failure it returns false and sets
    /// <paramref name="error"/> to a human-readable Chinese message. In both directions any legacy
    /// Startup-folder launcher is deleted.
    /// </summary>
    public static bool TrySet(bool enabled, out string error)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                error = "无法打开当前用户的启动项注册表键（HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run）。";
                return false;
            }

            if (enabled)
            {
                var path = ExecutablePath();
                if (path.Length == 0)
                {
                    error = "无法确定 rcagent.exe 的完整路径，未能启用开机自动启动。";
                    return false;
                }

                // Quoted so a path containing spaces is still a valid Run command.
                key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            error = enabled
                ? $"无法启用开机自动启动：{ex.Message}"
                : $"无法关闭开机自动启动：{ex.Message}";
            return false;
        }

        try
        {
            RemoveLegacyStartupLaunchers();
        }
        catch (Exception ex)
        {
            error = $"开机自动启动已{(enabled ? "启用" : "关闭")}，但清理旧的启动文件夹启动项失败：{ex.Message}";
            return false;
        }

        error = "";
        return true;
    }

    private static void RemoveLegacyStartupLaunchers()
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrEmpty(startup))
        {
            return;
        }

        foreach (var name in LegacyStartupFiles)
        {
            var path = Path.Combine(startup, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Strips surrounding whitespace and, if present, one pair of surrounding quotes.</summary>
    private static string Normalize(string command)
    {
        var value = command.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Trim();
        }

        return value;
    }

    /// <summary>
    /// The running executable. <see cref="Environment.ProcessPath"/> stays correct for a
    /// self-contained single-file publish; the WinForms fallback covers exotic hosts.
    /// </summary>
    private static string ExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Application.ExecutablePath;
        }
        catch
        {
            return "";
        }
    }
}
