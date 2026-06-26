using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Core.Interfaces;

/// <summary>
/// Abstraction for inference/automation broadcasting.
/// Pushes inference results to connected clients via SignalR (WebSocket) and
/// triggers Home Assistant automations via MQTT publishing.
///
/// High-frequency CSI graph frames do NOT go through this interface — they are
/// enqueued onto a loss-tolerant broadcast channel and drained to SignalR by a
/// dedicated pump (<c>BroadcastBackgroundService</c>), so a slow client can never
/// back-pressure the inference-critical consumer loop.
/// </summary>
public interface IBroadcastService
{
    /// <summary>
    /// Broadcasts an inference result to all connected SignalR clients.
    /// </summary>
    Task BroadcastInferenceResultAsync(InferenceResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Triggers a Home Assistant automation by publishing a status
    /// message to the configured MQTT topic (e.g., home/radar/automation).
    /// Called when a confirmed state is detected (e.g., LyingOnCouch for 3+ seconds).
    /// </summary>
    Task TriggerAutomationAsync(string status, CancellationToken cancellationToken);
}
