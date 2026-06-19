using Microsoft.AspNetCore.SignalR;

namespace CsiRadar.Backend.Infrastructure.SignalR;

/// <summary>
/// SignalR Hub for real-time CSI radar data streaming.
/// Connected clients (Web/Mobile/Flutter) receive:
///   - Processed CSI signal data for live graph visualization
///   - ONNX inference results (predicted activity labels)
///
/// Client methods:
///   - ReceiveCsiData: Called when new processed CSI data is available
///   - ReceiveInference: Called when a new inference result is produced
///   - ReceiveStatus: Called when a confirmed status change occurs
/// </summary>
public sealed class RadarHub : Hub
{
    /// <summary>
    /// Called when a new client connects to the hub.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        // TODO: Log connection, add to tracking group if needed
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called when a client disconnects from the hub.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // TODO: Log disconnection, clean up resources
        await base.OnDisconnectedAsync(exception);
    }
}
