using System.Text.Json;
using System.Text.Json.Serialization;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using CsiRadar.Backend.Infrastructure.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Infrastructure.Broadcasting;

/// <summary>
/// Implements <see cref="IBroadcastService"/> using SignalR for inference-result
/// streaming and MQTT for Home Assistant automation triggers.
///
/// High-frequency CSI graph frames are handled separately by
/// <see cref="BroadcastBackgroundService"/> (channel-drained), not here.
///
/// SignalR client methods emitted here:
///   - "ReceiveInference": Receives ONNX inference results (predicted labels)
///   - "ReceiveStatus": Receives confirmed status changes
///
/// Automation:
///   - Publishes to the configured MQTT automation topic when a confirmed
///     state is detected for the required consecutive count.
/// </summary>
public sealed class BroadcastService : IBroadcastService
{
    private readonly IHubContext<RadarHub> _hubContext;
    private readonly IMqttClientService _mqttClient;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<BroadcastService> _logger;

    // ── Automation debounce state ──
    private string _lastAutomationStatus = string.Empty;

    public BroadcastService(
        IHubContext<RadarHub> hubContext,
        IMqttClientService mqttClient,
        IOptions<MqttOptions> mqttOptions,
        ILogger<BroadcastService> logger)
    {
        _hubContext = hubContext;
        _mqttClient = mqttClient;
        _mqttOptions = mqttOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task BroadcastInferenceResultAsync(InferenceResult result, CancellationToken cancellationToken)
    {
        var payload = new InferenceResultDto
        {
            PredictedLabel = result.PredictedLabel,
            Confidence = result.Confidence,
            Scores = result.Scores,
            TimestampMs = result.TimestampTicks / TimeSpan.TicksPerMillisecond
        };

        await _hubContext.Clients.All.SendAsync(
            "ReceiveInference",
            payload,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task TriggerAutomationAsync(string status, CancellationToken cancellationToken)
    {
        // ── Deduplicate: only publish if the status has changed ──
        if (string.Equals(_lastAutomationStatus, status, StringComparison.Ordinal))
            return;

        _lastAutomationStatus = status;

        _logger.LogInformation(
            "Automation trigger: status changed to '{Status}'. " +
            "Publishing to MQTT topic '{Topic}'.",
            status, _mqttOptions.AutomationTopic);

        // ── Notify SignalR clients of the confirmed status change ──
        // Typed, camelCase DTO — no PascalCase exception on the wire (Seam B.1).
        await _hubContext.Clients.All.SendAsync(
            "ReceiveStatus",
            new StatusChangedDto
            {
                Status = status,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            cancellationToken);

        // ── Publish to MQTT for Home Assistant ──
        var mqttPayload = JsonSerializer.Serialize(new
        {
            status,
            timestamp = DateTimeOffset.UtcNow,
            source = "CsiRadar"
        });

        await _mqttClient.PublishAsync(
            _mqttOptions.AutomationTopic,
            mqttPayload,
            cancellationToken);
    }

    // ═══════════════════════════════════════════════════════════════
    //  DTOs for SignalR serialization (lightweight, frontend-facing)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Lightweight DTO sent to SignalR clients for inference results.
    /// </summary>
    private sealed class InferenceResultDto
    {
        [JsonPropertyName("predictedLabel")]
        public string PredictedLabel { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public float Confidence { get; set; }

        [JsonPropertyName("scores")]
        public Dictionary<string, float> Scores { get; set; } = [];

        [JsonPropertyName("timestampMs")]
        public long TimestampMs { get; set; }
    }
}
