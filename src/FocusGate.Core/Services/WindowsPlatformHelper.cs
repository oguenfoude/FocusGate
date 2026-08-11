using System.Runtime.InteropServices;

namespace FocusGate.Core.Services;

public static class WindowsPlatformHelper
{
    // --- 1. Prevent Windows System Sleep & USB Suspension ---
    [Flags]
    private enum ExecutionState : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    /// <summary>
    /// Prevents Windows OS from going to sleep, standby, or suspending USB modems.
    /// </summary>
    public static void PreventSystemSleep()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            SetThreadExecutionState(ExecutionState.ES_CONTINUOUS | ExecutionState.ES_SYSTEM_REQUIRED | ExecutionState.ES_AWAYMODE_REQUIRED);
        }
        catch { }
    }

    // --- 2. Disable Windows Console QuickEdit Mode (Anti-Freeze on Mouse Click) ---
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>
    /// Disables Windows QuickEdit mode so clicking or selecting inside the terminal window never freezes process execution.
    /// </summary>
    public static void DisableConsoleQuickEdit()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = GetStdHandle(STD_INPUT_HANDLE);
            if (handle != IntPtr.Zero && GetConsoleMode(handle, out var mode))
            {
                mode &= ~ENABLE_QUICK_EDIT_MODE;
                mode |= ENABLE_EXTENDED_FLAGS;
                SetConsoleMode(handle, mode);
            }
        }
        catch { }
    }
}
