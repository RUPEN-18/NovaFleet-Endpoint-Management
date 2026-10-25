using Microsoft.Extensions.Hosting;
using AutoShutdown.Shared;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class ScheduleWorker : BackgroundService
{
    private readonly AdminSecurityStore _security;
    private readonly StateStore _store;
    private readonly ActionDispatcher _dispatcher;

    public ScheduleWorker(StateStore store, ActionDispatcher dispatcher, AdminSecurityStore security)
    {
        _security=security;
        _store = store;
        _dispatcher = dispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(); }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task CheckAsync()
    {
        var now = DateTimeOffset.Now;
        var day = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "Mon",
            DayOfWeek.Tuesday => "Tue",
            DayOfWeek.Wednesday => "Wed",
            DayOfWeek.Thursday => "Thu",
            DayOfWeek.Friday => "Fri",
            DayOfWeek.Saturday => "Sat",
            _ => "Sun"
        };

        var schedules = await _store.GetSchedulesAsync();
        var changed = false;

        foreach (var schedule in schedules.Where(s => s.Enabled && s.Days.Contains(day, StringComparer.OrdinalIgnoreCase)))
        {
            if (!TimeSpan.TryParse(schedule.Time, out var time)) continue;
            var occurrence = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hours, time.Minutes, 0, now.Offset);
            var delta = now - occurrence;
            if (delta < TimeSpan.Zero || delta >= TimeSpan.FromMinutes(2)) continue;

            var key = $"{now:yyyy-MM-dd}|{schedule.Time}";
            if (string.Equals(schedule.LastRunKey, key, StringComparison.Ordinal)) continue;

            schedule.LastRunKey = key;
            schedule.LastRun = now;
            changed = true;

            try
            {
                var actor=await _security.IdentityAsync(schedule.AdminId)??throw new UnauthorizedAccessException("Re-save this schedule under an authorized administrator account.");
                await _dispatcher.DispatchAsync(new AdminActionRequest
                {
                    Target = schedule.Target,
                    Kind = schedule.Action,
                    Title = schedule.Action == ActionKind.TextMessage ? "Scheduled message from " + actor.Label : string.Empty,
                    Text = schedule.Message
                }, "SCHEDULER", actor);
            }
            catch (Exception ex)
            {
                await _store.AppendLogAsync(new LogEntry
                {
                    Source = "SCHEDULER",
                    Event = "SCHEDULE_SKIPPED",
                    Target = schedule.Target.ToString(),
                    Details = $"{schedule.Name}: {ex.Message}"
                });
            }
        }

        if (changed) await _store.SetSchedulesAsync(schedules);
    }
}
