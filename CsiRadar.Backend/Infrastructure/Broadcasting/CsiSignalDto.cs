using System.Text.Json.Serialization;

namespace CsiRadar.Backend.Infrastructure.Broadcasting;

/// <summary>
/// Lightweight DTO pushed to SignalR clients for live CSI graph visualization.
/// Carries only the processed amplitudes and metadata — never the raw I/Q data.
///
/// Flows through the loss-tolerant broadcast channel and is drained to SignalR by
/// <see cref="BroadcastBackgroundService"/>, decoupling the inference-critical
/// consumer loop from slow-client back-pressure.
/// </summary>
public sealed class CsiSignalDto
{
    [JsonPropertyName("timestampMs")]
    public long TimestampMs { get; set; }

    [JsonPropertyName("rssi")]
    public int Rssi { get; set; }

    [JsonPropertyName("subcarrierCount")]
    public int SubcarrierCount { get; set; }

    [JsonPropertyName("amplitudes")]
    public float[] Amplitudes { get; set; } = [];
}
