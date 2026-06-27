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
    public const string Version = "1.1";
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
}
