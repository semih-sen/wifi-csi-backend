using CsiRadar.Backend.Application.MachineLearning;
using CsiRadar.Backend.Application.Processing;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

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
    private readonly IOnnxModelEvaluator _evaluator;
    private readonly CalibrationCoordinator _calibration;
    private readonly ProcessingOptions _processing;

    public RadarHub(
        IRecordingService recording,
        IOnnxModelEvaluator evaluator,
        CalibrationCoordinator calibration,
        IOptions<ProcessingOptions> processing)
    {
        _recording = recording;
        _evaluator = evaluator;
        _calibration = calibration;
        _processing = processing.Value;
    }

    /// <summary>
    /// Contract handshake (Seam B.3). The frontend calls this on connect to read the
    /// contract version and live processing parameters, then self-configures the
    /// graph/inference panels and surfaces any version mismatch — instead of
    /// hardcoding 64/100 and guessing.
    /// </summary>
    public ServerInfoDto GetServerInfo() => new()
    {
        ContractVersion = ContractInfo.Version,
        WindowSize = _processing.WindowSize,
        SlideStep = _processing.SlideStep,
        SampleRateHz = _processing.SamplingRateHz,
        SubcarrierCount = OnnxInput.Subcarriers,
        ModelLoaded = _evaluator.IsReady,
        Classes = _evaluator.Labels,
    };

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
    /// Requests an empty-room baseline calibration (Phase 4). The processing consumer
    /// captures the next <paramref name="frames"/> frames and recomputes the baseline.
    /// Call this only when the room is known to be empty.
    /// </summary>
    public string Calibrate(int frames = CalibrationCoordinator.DefaultFrames)
    {
        if (frames < 1)
            frames = CalibrationCoordinator.DefaultFrames;
        _calibration.Request(frames);
        return $"Baseline calibration requested for the next {frames} frames.";
    }


    /// <summary>
    /// Called when a new client connects to the hub.
    /// </summary>
   
}
