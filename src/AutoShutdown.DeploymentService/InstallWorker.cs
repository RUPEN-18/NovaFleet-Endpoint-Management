using AutoShutdown.Shared;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoShutdown.DeploymentService;

public sealed partial class InstallWorker(DeviceSession session, ServiceJournal journal) : BackgroundService
{
    private readonly InstallLedger _ledger = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private string? _lastError;
    private static string ApplicationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                session.Reload();
                var http = session.Client;
                var profile = session.Profile;
                if (http is not null && profile is not null)
                {
                    var records = _ledger.ReadAll();
                    var unfinished = records.FirstOrDefault(x => x.Stage != "Done");
                    if (unfinished is not null)
                        await ProcessAsync(http, profile, unfinished, stoppingToken);
                    else
                    {
                        await InstallLedger.SetActivityAsync(false);
                        foreach (var done in records.Where(x => !x.Reported && x.Result is not null))
                            await SendResultAsync(http, done, stoppingToken);
                        using var response = await http.GetAsync("device/next", stoppingToken);
                        response.EnsureSuccessStatusCode();
                        if (response.StatusCode != HttpStatusCode.NoContent)
                        {
                            var job = await response.Content.ReadFromJsonAsync<InstallJob>(_json, stoppingToken)
                                ?? throw new InvalidDataException("No installation job was returned.");
                            if (records.Any(x => x.Job.Id == job.Id && x.Stage == "Done"))
                                throw new InvalidDataException("The server redelivered a completed job. Its installer will not be executed again.");
                            var record = new LocalInstall { Job = job,
                                Stage = job.State == InstallState.Installing ? "Starting" : "Received" };
                            await _ledger.SaveAsync(record);
                            await ProcessAsync(http, profile, record, stoppingToken);
                        }
                    }
                }
                _lastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_lastError != ex.Message)
                {
                    _lastError = ex.Message;
                    await journal.AppendAsync("DEPLOYMENT_CONNECTION_ERROR", ex.Message);
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessAsync(HttpClient http, DeviceConnection profile, LocalInstall record, CancellationToken token)
    {
        await InstallLedger.SetActivityAsync(true, record.Job.Id, record.Job.Package.Name, record.Job.State.ToString());
        try
        {
            var job = record.Job;
            if (ApplicationPackagePolicy.IsGeneral(job.Package))
            {
                await ProcessApplicationAsync(http, profile, record, token);
                return;
            }
            if (job.DeviceId != profile.DeviceId || !string.Equals(job.ComputerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                || !SevenZipPolicy.IsApproved(job.Package) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new InvalidDataException("This approved x64 job does not belong to this client.");
            if (record.Stage is "Starting" or "Installing")
            {
                // Never rerun an installer after a restart without knowing whether it started.
                using var running = FindRunning(record);
                if (running is not null)
                {
                    await running.WaitForExitAsync(token);
                    await FinishAsync(http, record, new(job.Id, InstallState.NeedsReview,
                        "A previously started installer finished after service recovery. " + InstalledDescription(),
                        running.ExitCode, InstalledVersion()), token);
                }
                else
                    await FinishAsync(http, record, new(job.Id, InstallState.NeedsReview,
                        "Installation was interrupted; its original exit code is unavailable. " + InstalledDescription(),
                        null, InstalledVersion()), token);
                return;
            }

            var official = await SevenZipPolicy.GetOfficialAsync(token);
            if (!string.Equals(official.Sha256, job.Package.Sha256, StringComparison.OrdinalIgnoreCase) || official.Length != job.Package.Length)
                throw new InvalidDataException("The job differs from the vendor's official release digest.");
            var installer = _ledger.Installer(job.Id);
            await DownloadAsync(http, job, installer, token);
            await ReportAsync(http, new(job.Id, InstallState.Verifying, "Installer received; verifying official SHA-256."), token);
            await using (var input = File.OpenRead(installer))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (hash != official.Sha256 || input.Length != official.Length)
                    throw new InvalidDataException("Downloaded installer hash or size is incorrect.");
            }
            RejectLinkedInstallPath();
            if (InstalledVersion() == SevenZipPolicy.Version)
            {
                await FinishAsync(http, record, new(job.Id, InstallState.AlreadyInstalled,
                    "Transfer and SHA-256 verified. 7-Zip 26.04 was already present; no repeat installation was needed.",
                    null, SevenZipPolicy.Version), token);
                return;
            }
            var existingExecutable = Path.Combine(ApplicationDirectory, "7z.exe");
            if (File.Exists(existingExecutable))
            {
                var existing = FileVersionInfo.GetVersionInfo(existingExecutable);
                if (existing.FileMajorPart > 26 || existing.FileMajorPart == 26 && existing.FileMinorPart > 4)
                    throw new InvalidOperationException("A newer 7-Zip version is present. Automatic downgrade is blocked.");
            }
            await ReportAsync(http, new(job.Id, InstallState.Installing, "Official installer verified. Installing silently."), token);
            record = record with { Stage = "Starting" };
            await _ledger.SaveAsync(record);
            await journal.AppendAsync("INSTALLER_STARTING", $"Job {job.Id}; approved 7-Zip 26.04; silent mode.");
            // The fixed arguments are documented by 7-Zip. No remote commands or user-supplied arguments.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installer, Arguments = $"/S /D=\"{ApplicationDirectory}\"",
                WorkingDirectory = _ledger.Folder(job.Id), UseShellExecute = false, CreateNoWindow = true
            }) ?? throw new IOException("The approved installer could not start.");
            record = record with { Stage = "Installing", ProcessId = process.Id, ProcessStartedUtc = process.StartTime.ToUniversalTime() };
            await _ledger.SaveAsync(record);
            // Cancellation leaves the ledger in Installing; recovery never blindly runs it again.
            await process.WaitForExitAsync(token);
            var version = InstalledVersion();
            var successful = process.ExitCode == 0 && version == SevenZipPolicy.Version;
            await FinishAsync(http, record, new(job.Id, successful ? InstallState.Installed : InstallState.Failed,
                successful ? $"7-Zip 26.04 installed silently. Verified 7z.exe and 7zFM.exe in {ApplicationDirectory}."
                    : $"Installer exit code {process.ExitCode}. {InstalledDescription()}",
                process.ExitCode, version), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A failure after the durable completion must never overwrite the actual result.
            if (_ledger.ReadAll().Any(x => x.Job.Id == record.Job.Id && x.Stage == "Done")) throw;
            using var running = FindRunning(record);
            if (running is not null && !running.HasExited) throw;
            await FinishAsync(http, record, new(record.Job.Id, InstallState.Failed, ex.Message), token);
        }
    }

    private static async Task DownloadAsync(HttpClient http, InstallJob job, string destination, CancellationToken token)
    {
        var temporary = destination + ".download";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"device/package/{job.Id}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[65536]; long total = 0; int count;
            while ((count = await source.ReadAsync(buffer, deadline.Token)) > 0)
            {
                total += count;
                if (total > job.Package.Length) throw new InvalidDataException("Installer response exceeds the approved size.");
                await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
            }
            if (total != job.Package.Length) throw new InvalidDataException("Installer transfer was incomplete.");
        }
        File.Move(temporary, destination, overwrite: true);
    }

    private async Task FinishAsync(HttpClient http, LocalInstall record, InstallReport result, CancellationToken token)
    {
        var done = record with { Stage = "Done", Result = result, Reported = false };
        await _ledger.SaveAsync(done); // Save result before sending; network failures can be retried.
        await InstallLedger.SetActivityAsync(false, record.Job.Id, record.Job.Package.Name, result.State.ToString(), result.Details);
        try { await journal.AppendAsync("INSTALLATION_" + result.State.ToString().ToUpperInvariant(), $"Job {result.JobId}: {result.Details}"); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
        try { await SendResultAsync(http, done, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Debug.WriteLine("Installation result saved for retry: " + ex.Message); }
    }

    private async Task SendResultAsync(HttpClient http, LocalInstall done, CancellationToken token)
    {
        await ReportAsync(http, done.Result!, token);
        await _ledger.SaveAsync(done with { Reported = true });
    }

    private async Task ReportAsync(HttpClient http, InstallReport report, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync("device/report", report, _json, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Admin rejected the status report: " + await response.Content.ReadAsStringAsync(token));
        var record = _ledger.ReadAll().FirstOrDefault(item => item.Job.Id == report.JobId);
        if (record is not null && !SevenZipPolicy.IsTerminal(report.State))
            await InstallLedger.SetActivityAsync(true, report.JobId, record.Job.Package.Name, report.State.ToString(), report.Details);
    }

    private Process? FindRunning(LocalInstall record)
    {
        if (ApplicationPackagePolicy.IsGeneral(record.Job.Package)) return FindApplicationProcess(record);
        var path = _ledger.Installer(record.Job.Id);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SevenZipPolicy.FileName)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)
                    && (record.ProcessId == 0 || process.Id == record.ProcessId
                    && (!record.ProcessStartedUtc.HasValue || Math.Abs((process.StartTime.ToUniversalTime() - record.ProcessStartedUtc.Value.UtcDateTime).TotalSeconds) < 1)))
                    return process;
            }
            catch { }
            process.Dispose();
        }
        return null;
    }

    private static void RejectLinkedInstallPath()
    {
        if (!Directory.Exists(ApplicationDirectory)) return;
        if ((File.GetAttributes(ApplicationDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The installation folder is a link.");
        var directories = new Stack<string>();
        directories.Push(ApplicationDirectory);
        while (directories.Count > 0)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The installation folder contains a linked path.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
            }
        }
    }
    public static string InstalledVersion()
    {
        foreach (var name in new[] { "7z.exe", "7zFM.exe" })
        {
            var path = Path.Combine(ApplicationDirectory, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return "";
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart != 26 || info.FileMinorPart != 4) return "";
        }
        return SevenZipPolicy.Version;
    }
    private static string InstalledDescription() => InstalledVersion() == SevenZipPolicy.Version
        ? "7-Zip 26.04 files are present." : "7-Zip 26.04 could not be verified.";
}
