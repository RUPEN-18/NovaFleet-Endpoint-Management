using System.Runtime.InteropServices;

namespace AutoShutdown.Client.Services;

/// <summary>
/// Keeps Windows awake while System B monitoring is active so the client can
/// continue counting idle time, show the idle overlay, receive commands, and
/// perform an automatic shutdown. When monitoring is disabled or the app exits,
/// normal Windows power-management behavior is restored.
/// </summary>
public static class SleepGuard
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    private static bool _active;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    public static bool IsActive => _active;

    public static void SetActive(bool active)
    {
        if (_active == active) return;

        if (active)
        {
            // Prevent automatic system sleep and display power-off while the
            // Nova monitoring policy is active.
            var result = SetThreadExecutionState(
                ExecutionState.Continuous |
                ExecutionState.SystemRequired |
                ExecutionState.DisplayRequired);

            if (result != 0)
                _active = true;
        }
        else
        {
            // ES_CONTINUOUS by itself clears the previously requested state.
            SetThreadExecutionState(ExecutionState.Continuous);
            _active = false;
        }
    }

    public static void Release() => SetActive(false);
}
