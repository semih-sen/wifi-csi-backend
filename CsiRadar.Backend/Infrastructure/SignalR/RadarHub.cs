using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
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

    private readonly IRecordingService _recording;
 
    public RadarHub(IRecordingService recording)
    {
        _recording = recording;
    }
 
    public override async Task OnConnectedAsync()
    {
        // Bring a freshly-connected client in sync with the current recorder state.
        await Clients.Caller.SendAsync("RecordingState", _recording.Status);
        await base.OnConnectedAsync();
    }
 
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
 
    // ═══════════════════════════════════════════════════════════════
    //  RECORDING CONTROL (client → server)
    // ═══════════════════════════════════════════════════════════════
 
    /// <summary>
    /// Begins a labelled recording session. Returns the resulting status to the
    /// caller and broadcasts the new state to every connected client so multiple
    /// frontends stay in sync.
    /// </summary>
    public async Task<RecordingStatus> StartRecording(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            label = "unlabeled";
 
        RecordingStatus status = _recording.Start(label.Trim());
        await Clients.All.SendAsync("RecordingState", status);
        return status;
    }
 
    /// <summary>Stops the active recording session and broadcasts the final state.</summary>
    public async Task<RecordingStatus> StopRecording()
    {
        RecordingStatus status = _recording.Stop();
        await Clients.All.SendAsync("RecordingState", status);
        return status;
    }
 
    /// <summary>Returns the current recorder status without changing it.</summary>
    public RecordingStatus GetRecordingStatus() => _recording.Status;


    /// <summary>
    /// Called when a new client connects to the hub.
    /// </summary>
   
}
