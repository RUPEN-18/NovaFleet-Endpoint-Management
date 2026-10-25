using System.Net.Http;
using System.IO;
using AutoShutdown.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;

namespace AutoShutdown.Client.Services;

public sealed class ClientAgent : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly LocalSettingsCache _cache;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private HubConnection? _hub;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private int _disposeStarted;

    public string ComputerName { get; } = Environment.MachineName;
    public GlobalSettings Settings { get; private set; } = new();
    public bool Connected => _hub?.State == HubConnectionState.Connected;
    public bool RemotePaused { get; private set; }
    public bool ShutdownScheduled { get; private set; }
    public string ShutdownReason { get; private set; } = string.Empty;
    public bool IdleScreenVisible { get; set; }

    public event Func<CommandEnvelope, Task>? TextMessageReceived;
    public event Func<CommandEnvelope, Task>? ImageMessageReceived;
    public event Action? StateChanged;
    public event Action<GlobalSettings>? SettingsChanged;

    public ClientAgent(ClientOptions options, LocalSettingsCache cache)
    {
        _options = options;
        _cache = cache;
        var server = new Uri(options.ServerUrl.TrimEnd('/') + "/");
        if (server.Scheme == Uri.UriSchemeHttps)
            _http = DeploymentTransport.Create(server, options.CertificateSha256);
        else if (server.Scheme == Uri.UriSchemeHttp && server.IsLoopback)
            _http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(12) };
        else throw new InvalidDataException("Remote Client connections require pinned HTTPS. Import the connection exported from Admin.");
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.Add("X-AutoShutdown-Key", options.ApiKey);
        if (options.DeviceId.Length > 0) _http.DefaultRequestHeaders.Add("X-Nova-Device-Id", options.DeviceId);
    }

    public async Task StartAsync()
    {
        Settings = await _cache.LoadAsync();
        // Recovery baseline: enable automatic shutdown only after a fresh server response.
        Settings = Settings with { MonitoringEnabled = false };
        RemotePaused = false;
        await TryRefreshSettingsAsync();

        _hub = new HubConnectionBuilder()
            .WithUrl($"{_options.ServerUrl.TrimEnd('/')}/hub/client?client={Uri.EscapeDataString(ComputerName)}&key={Uri.EscapeDataString(_options.ApiKey)}&deviceId={Uri.EscapeDataString(_options.DeviceId)}", options =>
            {
                if (!string.IsNullOrEmpty(_options.CertificateSha256))
                {
                    // One pinned HTTP handler covers negotiation and every long-polling request.
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = previous =>
                    {
                        previous.Dispose();
                        return DeploymentTransport.CreateHandler(new Uri(_options.ServerUrl), _options.CertificateSha256);
                    };
                }
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _hub.On<CommandEnvelope>("Command", ProcessCommandAsync);
        _hub.On<DeploymentProbe>("DeploymentProbe", ProcessDeploymentProbeAsync);
        _hub.On<GlobalSettings>("SettingsChanged", ApplySettingsAsync);

        _hub.Reconnected += async _ =>
        {
            await RefreshHubSettingsAsync();
            StateChanged?.Invoke();
        };
        _hub.Closed += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };
        _hub.Reconnecting += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };

        try { await _hub.StartAsync(); await RefreshHubSettingsAsync(); } catch { }
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTask = HeartbeatLoopAsync(_heartbeatCts.Token);
        StateChanged?.Invoke();
    }

    public async Task TryRefreshSettingsAsync()
    {
        try
        {
            var response = await _http.GetAsync("api/settings");
            if (!response.IsSuccessStatusCode) { if(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden){Settings=Settings with{MonitoringEnabled=false};StateChanged?.Invoke();}return; }
            var settings = await response.Content.ReadFromJsonAsync<GlobalSettings>(_json);
            if (settings is null) return;
            await ApplySettingsAsync(settings);
        }
        catch { }
    }

    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private async Task ApplySettingsAsync(GlobalSettings settings)
    {
        await _settingsGate.WaitAsync();
        try
        {
            Settings = settings;
            await _cache.SaveAsync(settings);
            SettingsChanged?.Invoke(settings);
            StateChanged?.Invoke();
        }
        finally { _settingsGate.Release(); }
    }
    private async Task RefreshHubSettingsAsync()
    {
        if(_hub?.State!=HubConnectionState.Connected)return;
        await ApplySettingsAsync(await _hub.InvokeAsync<GlobalSettings>("GetSettings"));
    }
    private DateTimeOffset _policyRefresh=DateTimeOffset.MinValue;
    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_hub?.State == HubConnectionState.Disconnected)
                {
                    try { await _hub.StartAsync(cancellationToken); await RefreshHubSettingsAsync(); } catch { }
                }

                if (_hub?.State == HubConnectionState.Connected)
                {
                    if(DateTimeOffset.UtcNow-_policyRefresh>TimeSpan.FromSeconds(30)){await RefreshHubSettingsAsync();_policyRefresh=DateTimeOffset.UtcNow;}
                    var effectiveMonitoring = Settings.MonitoringEnabled && !RemotePaused;
                    await _hub.InvokeAsync("Heartbeat", new ClientHeartbeat
                    {
                        DeviceId = _options.DeviceId,
                        ComputerName = ComputerName,
                        IdleSeconds = IdleMonitor.GetIdleTime().TotalSeconds,
                        MonitoringEnabled = effectiveMonitoring,
                        RemotePaused = RemotePaused,
                        IdleScreenEnabled = Settings.IdleScreenEnabled,
                        IdleScreenVisible = IdleScreenVisible,
                        ShutdownScheduled = ShutdownScheduled,
                        ShutdownReason = ShutdownReason,
                        ClientVersion = "0.5.5-recovered"
                    }, cancellationToken);
                }
            }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessDeploymentProbeAsync(DeploymentProbe probe)
    {
        if (!string.Equals(probe.TargetComputer, ComputerName, StringComparison.OrdinalIgnoreCase)
            || DateTimeOffset.UtcNow >= probe.ExpiresAt || !Guid.TryParseExact(probe.JobId, "N", out _)) return;
        // Acknowledge transport only. Monitoring/pause settings do not block this test.
        try
        {
            if (_hub is not null)
                await _hub.InvokeAsync("DeploymentProbeAck", new DeploymentProbeAck { JobId = probe.JobId });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deployment connection test acknowledgement failed: {ex.Message}");
            // The server records TimedOut if the acknowledgement did not reach it.
        }
    }

    private async Task ProcessCommandAsync(CommandEnvelope envelope)
    {
        if (AppExitState.IsClosing) return;
        if (!string.Equals(envelope.TargetComputer, ComputerName, StringComparison.OrdinalIgnoreCase)) return;
        if (DateTimeOffset.Now > envelope.ExpiresAt)
        {
            await AckAsync(envelope, false, "Command expired before execution.");
            return;
        }

        try
        {
            switch (envelope.Kind)
            {
                case ActionKind.Pause:
                    RemotePaused = true;
                    StateChanged?.Invoke();
                    await AckAsync(envelope, true, "Monitoring paused by Admin.");
                    break;

                case ActionKind.Resume:
                    RemotePaused = false;
                    StateChanged?.Invoke();
                    await AckAsync(envelope, true, "Pause cleared by Admin. Monitoring follows the current settings.");
                    break;

                case ActionKind.Shutdown:
                    if (DeploymentActivity.IsInstalling())
                    {
                        await AckAsync(envelope, false, "Shutdown deferred while a software installation is active.");
                        break;
                    }
                    var scheduled = ScheduleShutdown("admin");
                    await AckAsync(envelope, scheduled, scheduled
                        ? $"Shutdown scheduled with {Settings.ShutdownWarningSeconds}s warning."
                        : "Windows did not accept the shutdown request. Check Client errors.log.");
                    break;

                case ActionKind.CancelShutdown:
                    var cancelled = CancelShutdown();
                    await AckAsync(envelope, cancelled, cancelled ? "Shutdown cancelled." : "Windows did not cancel the shutdown. Check Client errors.log.");
                    break;

                case ActionKind.TextMessage:
                    if (TextMessageReceived is not null) await TextMessageReceived.Invoke(envelope);
                    await AckAsync(envelope, true, "Text message displayed.");
                    break;

                case ActionKind.ImageMessage:
                    if (string.IsNullOrWhiteSpace(envelope.MediaId)) throw new InvalidOperationException("Image message is missing media id.");
                    if (ImageMessageReceived is not null) await ImageMessageReceived.Invoke(envelope);
                    await AckAsync(envelope, true, "Image message displayed.");
                    break;
            }
        }
        catch (Exception ex)
        {
            await AckAsync(envelope, false, ex.Message);
        }
    }

    private async Task AckAsync(CommandEnvelope envelope, bool success, string details)
    {
        try
        {
            if (_hub?.State != HubConnectionState.Connected) return;
            await _hub.InvokeAsync("Ack", new CommandAck
            {
                CommandId = envelope.Id,
                ComputerName = ComputerName,
                Success = success,
                Details = details
            });
        }
        catch { }
    }


    public async Task SendLogAsync(string eventName, string details)
    {
        if (AppExitState.IsClosing) return;
        try
        {
            if (_hub?.State == HubConnectionState.Connected)
                await _hub.InvokeAsync("ClientLog", eventName, details);
        }
        catch { }
    }

    public async Task<byte[]> DownloadMediaAsync(string mediaId)
    {
        using var response = await _http.GetAsync($"media/{Uri.EscapeDataString(mediaId)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    public bool ScheduleShutdown(string reason)
    {
        if (AppExitState.IsClosing || DeploymentActivity.IsInstalling()) return false;
        if (ShutdownScheduled && !CancelShutdown()) return false;
        var seconds = Math.Max(1, Settings.ShutdownWarningSeconds);
        if (!StartShutdownProcess($"/s /t {seconds} /c \"Automatic shutdown managed by Admin\"")) return false;
        ShutdownScheduled = true;
        ShutdownReason = reason;
        _ = SendLogAsync(reason == "idle" ? "IDLE_SHUTDOWN_SCHEDULED" : "REMOTE_SHUTDOWN_SCHEDULED", $"Warning={Settings.ShutdownWarningSeconds}s");
        StateChanged?.Invoke();
        return true;
    }

    public bool CancelShutdown()
    {
        if (AppExitState.IsSessionEnding) return false;
        if (!StartShutdownProcess("/a")) return false;
        var previousReason = ShutdownReason;
        ShutdownScheduled = false;
        ShutdownReason = string.Empty;
        _ = SendLogAsync("SHUTDOWN_CANCELLED", string.IsNullOrWhiteSpace(previousReason) ? "No active reason" : previousReason);
        if (!AppExitState.IsClosing) StateChanged?.Invoke();
        return true;
    }

    private static bool StartShutdownProcess(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = arguments, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true
            });
            if (process is null || !process.WaitForExit(3000))
            {
                AppDiagnostics.Write("SystemB", "SHUTDOWN_COMMAND", "Windows shutdown command did not finish within three seconds.");
                return false;
            }
            if (process.ExitCode != 0)
            {
                AppDiagnostics.Write("SystemB", "SHUTDOWN_COMMAND", $"Exit code {process.ExitCode}: {process.StandardError.ReadToEnd()} {process.StandardOutput.ReadToEnd()}");
                // ERROR_NO_SHUTDOWN_IN_PROGRESS: cancelling is already satisfied.
                return arguments == "/a" && process.ExitCode == 1116;
            }
            return true;
        }
        catch (Exception ex) { AppDiagnostics.Record("SystemB", "SHUTDOWN_COMMAND", ex); return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _heartbeatCts?.Cancel();
        try
        {
            if (_heartbeatTask is not null)
            {
                try { await _heartbeatTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            var hub = _hub;
            _hub = null;
            if (hub is not null)
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await hub.StopAsync(stopTimeout.Token).ConfigureAwait(false); }
                catch (Exception ex) { AppDiagnostics.Record("SystemB", "STOP_CONNECTION", ex); }
                await hub.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _http.Dispose();
            _heartbeatCts?.Dispose();
        }
    }
}
