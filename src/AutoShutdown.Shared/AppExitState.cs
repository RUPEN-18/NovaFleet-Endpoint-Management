namespace AutoShutdown.Shared;

// Per-process lifecycle state. Windows session shutdown must not be mistaken
// for the user closing an idle-monitor window to cancel its pending countdown.
public static class AppExitState
{
    private static int _closing;
    private static int _sessionEnding;
    public static bool IsClosing => Volatile.Read(ref _closing) != 0;
    public static bool IsSessionEnding => Volatile.Read(ref _sessionEnding) != 0;
    public static void BeginClosing() => Interlocked.Exchange(ref _closing, 1);
    public static void BeginSessionEnding()
    {
        Interlocked.Exchange(ref _sessionEnding, 1);
        BeginClosing();
    }
    public static bool CancelIdleOnWindowClose(bool pending, bool sessionEnding) => pending && !sessionEnding;
    public static bool CanUpdateUi(bool windowClosing, bool dispatcherStopping) => !windowClosing && !dispatcherStopping;
}
