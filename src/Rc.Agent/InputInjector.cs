using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rc.Agent;

/// <summary>
/// Injects synthetic mouse/keyboard input into the interactive desktop via <c>SendInput</c>.
/// The interop structs use the native x64 layout (INPUT is 40 bytes: 4-byte type + 4-byte padding
/// + a 32-byte union), so every call contributes exactly one INPUT to the queue.
/// </summary>
public static class InputInjector
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>Mouse button codes as used by the protocol (0=left, 1=right, 2=middle).</summary>
    public const int ButtonLeft = 0;
    public const int ButtonRight = 1;
    public const int ButtonMiddle = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>Moves the cursor to a normalized position on the primary monitor.</summary>
    public static void MoveAbsolute(double normalizedX, double normalizedY)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = ToAbsolute(normalizedX),
                    dy = ToAbsolute(normalizedY),
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        Send(input);
    }

    public static void MouseDown(int button) => SendMouseButton(button, down: true);

    public static void MouseUp(int button) => SendMouseButton(button, down: false);

    public static void Wheel(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = MOUSEEVENTF_WHEEL,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        Send(input);
    }

    /// <summary>Sends a key press/release using scan codes, which layout-matched apps need.</summary>
    public static void Key(int virtualKey, bool down, bool extended)
    {
        if (virtualKey <= 0 || virtualKey > 0xFF)
        {
            return;
        }

        var scan = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
        var flags = KEYEVENTF_SCANCODE;
        if (extended)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }
        if (!down)
        {
            flags |= KEYEVENTF_KEYUP;
        }

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        Send(input);
    }

    private static void SendMouseButton(int button, bool down)
    {
        var flag = button switch
        {
            ButtonRight => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            ButtonMiddle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        Send(input);
    }

    private static int ToAbsolute(double normalized)
    {
        var value = normalized * 65535.0;
        return Math.Clamp((int)Math.Round(value), 0, 65535);
    }

    private static void Send(INPUT input)
    {
        var inputs = new[] { input };
        var sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            Log.Warn($"SendInput failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }
}
