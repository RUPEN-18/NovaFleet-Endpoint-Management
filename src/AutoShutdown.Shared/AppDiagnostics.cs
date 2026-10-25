using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoShutdown.Shared;

public static class AppDiagnostics
{
    private static readonly object Gate = new();
    public static string LogPath(string app) => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "AutoShutdown.Nova", app, "errors.log");
    public static void Record(string app, string operation, Exception exception) => Write(app, operation, exception.ToString());
    public static void Write(string app, string operation, string details)
    {
        try
        {
            // Hub exception text can include connection URLs. Do not retain pairing secrets.
            details = Regex.Replace(details, @"(?i)([?&](?:key|token)=)[^&\s""']+", "$1[redacted]");
            details = Regex.Replace(details, @"\b[0-9a-fA-F]{64}\b", "[redacted]");
            if (details.Length > 32000) details = details[..32000];
            lock (Gate)
            {
                var path = LogPath(app);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                    { atUtc = DateTimeOffset.UtcNow, operation, details, processId = Environment.ProcessId }) + Environment.NewLine);
            }
        }
        catch (Exception ex) { Debug.WriteLine("Nova diagnostic write failed: " + ex.Message); }
    }
    public static void FinishExit(string app, Func<Task> cleanup)
    {
        try
        {
            // Connection teardown runs without a WPF SynchronizationContext. Give it a
            // bounded opportunity to finish; never display a modal dialog during Windows exit.
            Task.Run(cleanup).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Record(app, "EXIT_CLEANUP", ex); }
    }
}
