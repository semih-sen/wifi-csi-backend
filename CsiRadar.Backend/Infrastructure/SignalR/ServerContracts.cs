using System.Text.Json.Serialization;

namespace CsiRadar.Backend.Infrastructure.SignalR;

/// <summary>
/// The wire-contract version for the SignalR seam (Seam B). Bumped whenever an
/// event/DTO/method shape changes incompatibly. The frontend reads it via
/// <c>GetServerInfo</c> on connect and surfaces a mismatch instead of guessing.
/// Keep in lock-step with CONTRACTS.md.
/// </summary>
public static class ContractInfo
{
    // 1.1: RecordingStatus gained `subject`; StartRecording gained a subject arg.
    // 1.2: added the `CalibrationState` event + Calibrate method; ServerInfo gained
    //      `isCalibrating` / `baselineActive`.
    // 1.3: StartRecording gained a `durationMs` arg (server-side auto-stop);
    //      RecordingStatus gained `stopAtUnixMs`.
    // 1.4: retired `ReceiveCsiData`/`CsiFrame` (V1 single-RX graph); added the per-RX
    //      `ReceiveDspFrame` event + `DspFrameDto`; ServerInfo gained viz metadata
    //      (`subcarriers`, `dopplerBins`, `dopplerCadenceHz`).
    public const string Version = "1.4";
}

/// <summary>
/// <c>ReceiveStatus</c> payload — a confirmed automation status change.
///
/// Historically this event was emitted as an anonymous PascalCase object
/// (<c>{ Status, Timestamp }</c>), the one casing exception on the wire. It is now a
/// typed, camelCase DTO like every other event so the frontend needs no special case.
/// </summary>
public sealed class StatusChangedDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("timestampMs")]
    public long TimestampMs { get; set; }
}

/// <summary>
/// Return value of the <c>GetServerInfo</c> hub method (Seam B handshake). Carries the
/// contract version plus the live processing parameters so the frontend can
/// self-configure (graph/inference panels) instead of hardcoding 64/100, and detect
/// a contract mismatch on connect.
/// </summary>
public sealed class ServerInfoDto
{
    [JsonPropertyName("contractVersion")]
    public string ContractVersion { get; set; } = ContractInfo.Version;

    [JsonPropertyName("windowSize")]
    public int WindowSize { get; set; }

    [JsonPropertyName("slideStep")]
    public int SlideStep { get; set; }

    [JsonPropertyName("sampleRateHz")]
    public double SampleRateHz { get; set; }

    /// <summary>Subcarrier count the model expects (the C dimension of the input).</summary>
    [JsonPropertyName("subcarrierCount")]
    public int SubcarrierCount { get; set; }

    /// <summary>True when a model + label map are loaded and inference is live.</summary>
    [JsonPropertyName("modelLoaded")]
    public bool ModelLoaded { get; set; }

    /// <summary>Class labels in output-channel order (empty when no model is loaded).</summary>
    [JsonPropertyName("classes")]
    public IReadOnlyList<string> Classes { get; set; } = [];

    /// <summary>True while a baseline calibration is currently in progress.</summary>
    [JsonPropertyName("isCalibrating")]
    public bool IsCalibrating { get; set; }

    /// <summary>True once an empty-room baseline is captured and being subtracted live.</summary>
    [JsonPropertyName("baselineActive")]
    public bool BaselineActive { get; set; }

    // ── V2 viz metadata (contract 1.4) — lets the DSP panels size their canvases from
    //    the server contract instead of hardcoding 64/33/10. ──

    /// <summary>Subcarriers per RX in a <c>ReceiveDspFrame</c> amplitude vector (DSP layer).</summary>
    [JsonPropertyName("subcarriers")]
    public int Subcarriers { get; set; }

    /// <summary>Doppler magnitude bins per RX (<c>dopplerMean</c> length) — one-sided DC…Nyquist.</summary>
    [JsonPropertyName("dopplerBins")]
    public int DopplerBins { get; set; }

    /// <summary>Throttled broadcast cadence of <c>ReceiveDspFrame</c>, in Hz.</summary>
    [JsonPropertyName("dopplerCadenceHz")]
    public double DopplerCadenceHz { get; set; }
}

/// <summary>
/// <c>CalibrationState</c> payload — pushed whenever baseline calibration starts or
/// finishes so the UI can show a "Calibrating…" warning and a persistent
/// "Baseline: Active" badge. Also returned (as fields) by <c>GetServerInfo</c> so a
/// late-joining / reconnecting client renders the correct state immediately.
/// </summary>
public sealed class CalibrationStateDto
{
    [JsonPropertyName("isCalibrating")]
    public bool IsCalibrating { get; set; }

    [JsonPropertyName("baselineActive")]
    public bool BaselineActive { get; set; }

    /// <summary>
    /// True if the most recent attempt failed — almost always because no CSI frames
    /// were flowing (the sensor isn't streaming), so there was nothing to average.
    /// </summary>
    [JsonPropertyName("failed")]
    public bool Failed { get; set; }

    /// <summary>Frames in the active/just-finished calibration capture.</summary>
    [JsonPropertyName("framesRequested")]
    public int FramesRequested { get; set; }

    [JsonPropertyName("timestampMs")]
    public long TimestampMs { get; set; }
}
