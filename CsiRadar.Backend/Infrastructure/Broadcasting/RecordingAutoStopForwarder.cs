using CsiRadar.Backend.Application.Recording;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Infrastructure.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Infrastructure.Broadcasting;

/// <summary>
/// Broadcasts <c>RecordingState</c> when a recording auto-stops on its scheduled
/// duration. Manual start/stop are broadcast by <see cref="RadarHub"/>; the auto-stop
/// happens on a timer inside <see cref="RecordingService"/> (no SignalR there), so this
/// forwarder bridges its <see cref="RecordingService.AutoStopped"/> event to clients —
/// the same decoupling pattern used for calibration state.
/// </summary>
public sealed class RecordingAutoStopForwarder : IHostedService
{
    private readonly RecordingService _recording;
    private readonly IHubContext<RadarHub> _hub;
    private readonly ILogger<RecordingAutoStopForwarder> _logger;

    public RecordingAutoStopForwarder(
        RecordingService recording,
        IHubContext<RadarHub> hub,
        ILogger<RecordingAutoStopForwarder> logger)
    {
        _recording = recording;
        _hub = hub;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _recording.AutoStopped += OnAutoStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _recording.AutoStopped -= OnAutoStopped;
        return Task.CompletedTask;
    }

    private void OnAutoStopped(RecordingStatus status) => _ = BroadcastAsync(status);

    private async Task BroadcastAsync(RecordingStatus status)
    {
        try
        {
            await _hub.Clients.All.SendAsync("RecordingState", status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast auto-stop RecordingState.");
        }
    }
}
