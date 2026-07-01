using System.Text.Json.Serialization;

namespace CsiRadar.Backend.Infrastructure.Broadcasting;

/// <summary>
/// Per-RX viz payload inside a <see cref="DspFrameDto"/>. Carries only what the two
/// live canvases need — nothing model-facing.
/// </summary>
public sealed class DspRxDto
{
    /// <summary>0 = RX0 (primary), 1 = RX1 (secondary).</summary>
    [JsonPropertyName("rxIndex")]
    public int RxIndex { get; set; }

    /// <summary><c>|CSI|</c> per subcarrier (length 64). Raw magnitude — non-negative.</summary>
    [JsonPropertyName("amplitude")]
    public float[] Amplitude { get; set; } = [];

    /// <summary>
    /// VIZ-ONLY Doppler column: the mean STFT magnitude across subcarriers for this
    /// RX's latest column (length 33 = <c>StftBins</c>), or empty until the first STFT
    /// window has filled. This aggregation exists purely for display; the model-facing
    /// per-subcarrier Doppler is untouched on the Phase 3 output channel.
    /// </summary>
    [JsonPropertyName("dopplerMean")]
    public float[] DopplerMean { get; set; } = [];
}

/// <summary>
/// <c>ReceiveDspFrame</c> payload (Seam B) — a throttled (10 Hz) tap off the Phase 2
/// per-RX DSP stage for live visualization. Carries derived amplitude + an aggregated
/// Doppler column for each RX, RX0 and RX1 side by side.
///
/// Flows through the loss-tolerant <c>DspBroadcastChannelManager</c> and is drained to
/// SignalR by <c>DspBroadcastBackgroundService</c>, so the DSP stage never awaits the
/// network (same decoupling as the retired V1 graph broadcast).
/// </summary>
public sealed class DspFrameDto
{
    /// <summary>Shared sequence number of the aligned pair these modalities came from.</summary>
    [JsonPropertyName("seqNo")]
    public long SeqNo { get; set; }

    /// <summary>Per-RX viz data, ordered [RX0, RX1].</summary>
    [JsonPropertyName("rx")]
    public DspRxDto[] Rx { get; set; } = [];
}
