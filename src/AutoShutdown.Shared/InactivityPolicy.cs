namespace AutoShutdown.Shared;

public static class InactivityPolicy
{
    public static bool ShouldShutdown(GlobalSettings settings, TimeSpan idle, bool paused, bool installing) =>
        settings.MonitoringEnabled && !paused && !installing && double.IsFinite(settings.ShutdownHours)
        && settings.ShutdownHours > 0 && idle.TotalHours >= settings.ShutdownHours;
}
