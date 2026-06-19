using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Core.Interfaces;

/// <summary>
/// Abstraction for the real-time broadcasting service.
/// Pushes processed CSI data and inference results to connected clients
/// via SignalR (WebSocket) and triggers Home Assistant automations
/// via MQTT publishing.
/// </summary>
public interface IBroadcastService
{
    /// <summary>
    /// Broadcasts processed CSI signal data to all connected SignalR clients.
    /// Used for real-time graph visualization on the frontend.
    /// </summary>
    Task BroadcastCsiDataAsync(CsiData data, CancellationToken cancellationToken);

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
