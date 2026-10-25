using AutoShutdown.Shared;
using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AutoShutdown.DeploymentService;

public sealed partial class InstallWorker
{
    private async Task ProcessApplicationAsync(HttpClient http, DeviceConnection profile, LocalInstall record, CancellationToken token)
    {
        var job = record.Job;
        var package = job.Package;
        if (job.DeviceId != profile.DeviceId || !string.Equals(job.ComputerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || !ApplicationPackagePolicy.IsApproved(package)) throw new InvalidDataException("Invalid application job or client identity.");
        if (record.Stage is "Starting" or "Installing")
        {
            using var running = FindApplicationProcess(record);
            if (running is not null) await running.WaitForExitAsync(token);
            await FinishAsync(http, record, new(job.Id, InstallState.NeedsReview,
                "Service recovered an interrupted installation. The installer will not be repeated automatically; inspect its log and installed application.",
                running?.ExitCode, ProbeInstalledApplication(package)), token);
            return;
        }
        var installer = _ledger.Installer(job);
        await DownloadAsync(http, job, installer, token);
        await ReportAsync(http, new(job.Id, InstallState.Verifying, "Installer received; checking the Admin-approved SHA-256 and size."), token);
        await using (var input = File.OpenRead(installer))
        {
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            if (!string.Equals(digest, package.Sha256, StringComparison.OrdinalIgnoreCase) || input.Length != package.Length)
                throw new InvalidDataException("Downloaded application checksum or size is incorrect.");
        }
        var installed = ProbeApplication(package);
        if (ApplicationPackagePolicy.VerifiedResult(package, installed.Version, installed.Evidence))
        {
            await FinishAsync(http, record, new(job.Id, InstallState.AlreadyInstalled,
                "Package verified; the application is already installed and verified for this machine.",
                null, installed.Version, installed.Evidence), token);
            return;
        }
        await ReportAsync(http, new(job.Id, InstallState.Installing, "Installing the selected application in unattended mode."), token);
        record = record with { Stage = "Starting" };
        await _ledger.SaveAsync(record);
        await journal.AppendAsync("APPLICATION_STARTING", $"Job {job.Id}; {package.Name}; SHA-256 {package.Sha256}.");
        var start = BuildApplicationStartInfo(job, installer, _ledger.Folder(job.Id));
        using var process = Process.Start(start) ?? throw new IOException("Application installer could not start.");
        record = record with { Stage = "Installing", ProcessId = process.Id, ProcessStartedUtc = process.StartTime.ToUniversalTime() };
        try { await _ledger.SaveAsync(record); }
        catch
        {
            // Do not clear the shutdown hold while an installer continues after a ledger-write failure.
            await process.WaitForExitAsync(token);
            throw;
        }
        await process.WaitForExitAsync(token);
        var successfulExit = ApplicationPackagePolicy.SuccessfulExit(package, process.ExitCode);
        installed = ProbeApplication(package);
        // Some installers commit registry metadata shortly after their launcher exits.
        for (var attempt = 0; successfulExit && !ApplicationPackagePolicy.VerifiedResult(package, installed.Version, installed.Evidence) && attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            installed = ProbeApplication(package);
        }
        var version = installed.Version;
        var state = !successfulExit ? InstallState.Failed
            : ApplicationPackagePolicy.VerifiedResult(package, version, installed.Evidence) ? InstallState.Installed : InstallState.NeedsReview;
        var details = state == InstallState.Installed
            ? $"{package.Name} installed. Verified machine installation ({package.VerificationMode}), version {version}."
            : state == InstallState.NeedsReview ? "Installer returned success, but the expected installed name/version could not be verified. Inspect the client before retrying."
            : $"Installer failed with exit code {process.ExitCode}.";
        if (process.ExitCode == 3010) details += " A restart is required; Nova did not restart the PC.";
        await FinishAsync(http, record, new(job.Id, state, details, process.ExitCode, version, installed.Evidence), token);
    }

    public static ProcessStartInfo BuildApplicationStartInfo(InstallJob job, string installer, string workingDirectory)
    {
        if (!ApplicationPackagePolicy.IsGeneral(job.Package) || !ApplicationPackagePolicy.IsApproved(job.Package))
            throw new InvalidDataException("Invalid application profile.");
        var msi = job.Package.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo { FileName = msi ? Path.Combine(Environment.SystemDirectory, "msiexec.exe") : installer,
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
        if (!msi) start.Arguments = job.Package.SilentArguments;
        else
        {
            foreach (var argument in new[] { "/i", installer, "/qn", "/norestart", "/L*v", Path.Combine(workingDirectory, "installer.log") })
                start.ArgumentList.Add(argument);
            foreach (Match match in Regex.Matches(job.Package.SilentArguments, @"([A-Z][A-Z0-9_]*)=(""[^""]*""|[^\s""]+)"))
                start.ArgumentList.Add(match.Groups[1].Value + "=" + match.Groups[2].Value.Trim('"'));
            start.ArgumentList.Add("REBOOT=ReallySuppress");
        }
        return start;
    }

    public sealed record ApplicationProbe(string Version, InstallationEvidence? Evidence = null);
    public static string ProbeInstalledApplication(ApprovedPackage package) => ProbeApplication(package).Version;
    public static ApplicationProbe ProbeApplication(ApprovedPackage package)
    {
        if (package.VerificationMode == ApplicationPackagePolicy.MsiProduct)
        {
            var state = WindowsInstallerMetadata.MachineProductProperty(package.InstalledProductCode, "State");
            var version = WindowsInstallerMetadata.MachineProductProperty(package.InstalledProductCode, "VersionString");
            if (state != "5" || version != package.InstalledDisplayVersion) return new("");
            return new(version, new(package.VerificationMode,
                WindowsInstallerMetadata.MachineProductProperty(package.InstalledProductCode, "InstalledProductName"),
                WindowsInstallerMetadata.MachineProductProperty(package.InstalledProductCode, "Publisher"), package.InstalledProductCode));
        }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var applications = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (applications is null) continue;
            foreach (var key in applications.GetSubKeyNames())
            {
                using var application = applications.OpenSubKey(key);
                var name = application?.GetValue("DisplayName") as string ?? "";
                var version = application?.GetValue("DisplayVersion") as string ?? "";
                var publisher = application?.GetValue("Publisher") as string ?? "";
                if (!string.Equals(name, package.InstalledDisplayName, StringComparison.OrdinalIgnoreCase)
                    || !ApplicationPackagePolicy.IsVersion(version)) continue;
                if (package.VerificationMode == ApplicationPackagePolicy.Exact && version != package.InstalledDisplayVersion) continue;
                if (package.InstalledPublisher.Length != 0 && !ApplicationPackagePolicy.SamePublisher(publisher, package.InstalledPublisher)) continue;
                var executableVerified = package.VerificationMode == ApplicationPackagePolicy.BraveMachine && VerifyBraveExecutable(version);
                var evidence = new InstallationEvidence(package.VerificationMode, name, publisher, ExecutableVerified: executableVerified);
                if (ApplicationPackagePolicy.VerifiedResult(package, version, evidence)) return new(version, evidence);
            }
        }
        return new("");
    }
    private static bool VerifyBraveExecutable(string registryVersion)
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        foreach (var root in roots.Where(x => x.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
            if (!File.Exists(path) || !WindowsInstallerMetadata.IsSignedBy(path, InstallerCatalog.BravePublisher)) continue;
            var metadata = FileVersionInfo.GetVersionInfo(path);
            if (InstallerCatalog.BraveVersionsMatch(registryVersion, metadata.ProductVersion)
                || InstallerCatalog.BraveVersionsMatch(registryVersion, metadata.FileVersion)) return true;
        }
        return false;
    }

    private Process? FindApplicationProcess(LocalInstall record)
    {
        if (record.ProcessId <= 0 || !record.ProcessStartedUtc.HasValue) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(record.ProcessId);
            var expected = record.Job.Package.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.SystemDirectory, "msiexec.exe") : _ledger.Installer(record.Job);
            if (!process.HasExited && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)
                && Math.Abs((process.StartTime.ToUniversalTime() - record.ProcessStartedUtc.Value.UtcDateTime).TotalSeconds) < 1)
                return process;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        process?.Dispose();
        return null;
    }
}
