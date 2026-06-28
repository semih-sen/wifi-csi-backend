using CsiRadar.Backend.Application.Processing;
using CsiRadar.Backend.Infrastructure.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Infrastructure.Broadcasting;

/// <summary>
/// Forwards <see cref="CalibrationCoordinator"/> state changes to SignalR clients as
/// <c>CalibrationState</c> events. The consumer thread only flips the coordinator's
/// state (lock-free, no network); this service owns the <see cref="IHubContext{T}"/>
/// and pushes the update, so the inference-critical loop never touches SignalR.
///
/// Calibration transitions are rare (start + finish per calibration), so the push is
/// fire-and-forget with error logging rather than a dedicated channel/pump.
/// </summary>
public sealed class CalibrationBroadcastBackgroundService : IHostedService
{
    private readonly CalibrationCoordinator _calibration;
    private readonly IHubContext<RadarHub> _hub;
    private readonly ILogger<CalibrationBroadcastBackgroundService> _logger;

    public CalibrationBroadcastBackgroundService(
        CalibrationCoordinator calibration,
        IHubContext<RadarHub> hub,
        ILogger<CalibrationBroadcastBackgroundService> logger)
    {
        _calibration = calibration;
        _hub = hub;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _calibration.StateChanged += OnStateChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _calibration.StateChanged -= OnStateChanged;
        return Task.CompletedTask;
    }

    // Raised on the consumer thread — capture the snapshot synchronously, then push
    // off-thread so we never block the consumer on the network.
    private void OnStateChanged()
    {
        CalibrationStateDto dto = RadarHub.BuildCalibrationState(_calibration);
        _ = BroadcastAsync(dto);
    }

    private async Task BroadcastAsync(CalibrationStateDto dto)
    {
        try
        {
            await _hub.Clients.All.SendAsync("CalibrationState", dto);
            _logger.LogInformation(
                "CalibrationState broadcast: isCalibrating={Calibrating}, baselineActive={Active}.",
                dto.IsCalibrating, dto.BaselineActive);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast CalibrationState.");
        }
    }
}
