using System.Text.Json;
using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace CsiRadar.Backend.Infrastructure.Mqtt;

/// <summary>
/// Production implementation of <see cref="IMqttClientService"/> using MQTTnet v5.
///
/// Responsibilities:
///   - Connects to the Mosquitto broker and subscribes to the CSI topic
///   - Deserializes incoming JSON payloads using source-generated System.Text.Json
///   - Writes parsed CsiData into the BoundedChannel via TryWrite (non-blocking)
///   - Publishes messages for Home Assistant automation triggers
///   - Auto-reconnects on unexpected disconnections
///
/// HOT-PATH CONTRACT:
///   The <see cref="OnMessageReceivedAsync"/> handler is the hottest code path
///   in the entire system (~100 invocations/sec). It MUST:
///     ✓ Use source-generated JSON (no reflection)
///     ✓ Call TryWrite (never awaitable WriteAsync)
///     ✓ Return Task.CompletedTask (cached, zero-alloc)
///     ✗ Never call Task.Run
///     ✗ Never perform I/O, logging, or math
///     ✗ Never allocate beyond the CsiData object itself
/// </summary>
public sealed class MqttClientService : IMqttClientService, IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly CsiDataChannelManager _channelManager;
    private readonly ILogger<MqttClientService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly MqttClientFactory _factory = new();

    private IMqttClient? _mqttClient;
    private MqttClientOptions? _clientOptions;
    private MqttClientSubscribeOptions? _subscribeOptions;

    // ── Diagnostics counters (lock-free, read via Interlocked) ──
    private long _messagesReceived;
    private long _messagesEnqueued;
    private long _messagesDropped;
    private long _deserializationErrors;

    public MqttClientService(
        IOptions<MqttOptions> options,
        CsiDataChannelManager channelManager,
        ILogger<MqttClientService> logger)
    {
        _options = options.Value;
        _channelManager = channelManager;
        _logger = logger;
    }

    /// <summary>
    /// Current diagnostic counters for monitoring.
    /// </summary>
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);
    public long MessagesEnqueued => Interlocked.Read(ref _messagesEnqueued);
    public long MessagesDropped => Interlocked.Read(ref _messagesDropped);
    public long DeserializationErrors => Interlocked.Read(ref _deserializationErrors);

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cts.Token);

        _mqttClient = _factory.CreateMqttClient();

        // ── Wire event handlers BEFORE connecting ──
        // MQTTnet v5 uses Func<TArgs, Task> delegates (not C# events).
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        _mqttClient.ConnectedAsync += OnConnectedAsync;

        // ── Build connection options ──
        _clientOptions = _factory.CreateClientOptionsBuilder()
            .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
            .WithClientId(_options.ClientId)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds))
            .Build();

        // ── Build subscription options ──
        _subscribeOptions = _factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(_options.CsiTopic))
            .Build();

        _logger.LogInformation(
            "Connecting to MQTT broker at {Host}:{Port} with client ID '{ClientId}'...",
            _options.BrokerHost, _options.BrokerPort, _options.ClientId);

        await _mqttClient.ConnectAsync(_clientOptions, linkedCts.Token);

        // Subscribe immediately after connection
        await _mqttClient.SubscribeAsync(_subscribeOptions, linkedCts.Token);

        _logger.LogInformation(
            "MQTT connected and subscribed to topic '{Topic}'.",
            _options.CsiTopic);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        // Signal the internal CTS to stop reconnection attempts
        await _cts.CancelAsync();

        if (_mqttClient?.IsConnected == true)
        {
            _logger.LogInformation("Disconnecting from MQTT broker...");

            var disconnectOptions = _factory.CreateClientDisconnectOptionsBuilder()
                .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                .Build();

            try
            {
                await _mqttClient.DisconnectAsync(disconnectOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during MQTT disconnect (non-fatal).");
            }
        }

        _logger.LogInformation(
            "MQTT client stopped. Stats: Received={Received}, Enqueued={Enqueued}, Dropped={Dropped}, Errors={Errors}",
            MessagesReceived, MessagesEnqueued, MessagesDropped, DeserializationErrors);
    }

    /// <inheritdoc />
    public async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        if (_mqttClient?.IsConnected != true)
        {
            _logger.LogWarning("Cannot publish to '{Topic}': MQTT client is not connected.", topic);
            return;
        }

        var message = _factory.CreateApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqttClient.PublishAsync(message, cancellationToken);

        _logger.LogDebug("Published message to '{Topic}': {Payload}", topic, payload);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EVENT HANDLERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// HOT PATH — Called ~100 times/second.
    ///
    /// Pipeline:
    ///   1. Read PayloadSegment (zero-copy from MQTTnet's internal buffer)
    ///   2. Deserialize via source-generated JSON (no reflection, minimal alloc)
    ///   3. Map DTO → CsiData (lightweight object creation)
    ///   4. TryWrite into BoundedChannel (non-blocking, lock-free)
    ///   5. Return Task.CompletedTask (cached singleton, zero-alloc)
    ///
    /// NEVER add: Task.Run, logging, I/O, LINQ, string formatting, exceptions
    /// in the normal flow. The catch block is the ONLY exception path.
    /// </summary>
    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        Interlocked.Increment(ref _messagesReceived);

        // ── Step 1: Extract raw bytes from ReadOnlySequence ──
        // MQTTnet v5 exposes Payload as ReadOnlySequence<byte>.
        // For single-segment sequences (the common case at small payloads),
        // we get a zero-copy ReadOnlySpan directly.
        var payload = e.ApplicationMessage.Payload;
        if (payload.IsEmpty)
            return Task.CompletedTask;

        try
        {
            // ── Step 2: Source-gen JSON deserialization ──
            // Single-segment fast path avoids any allocation.
            CsiPayloadDto? dto;
            if (payload.IsSingleSegment)
            {
                dto = JsonSerializer.Deserialize(
                    payload.FirstSpan,
                    CsiJsonContext.Default.CsiPayloadDto);
            }
            else
            {
                // Multi-segment fallback (rare for small CSI payloads).
                // Utf8JsonReader natively supports ReadOnlySequence<byte>.
                var reader = new Utf8JsonReader(payload);
                dto = JsonSerializer.Deserialize(
                    ref reader,
                    CsiJsonContext.Default.CsiPayloadDto);
            }

            if (dto?.Data is null)
                return Task.CompletedTask;

            // ── Step 3: Map DTO → Domain entity (no processing) ──
            var csiData = new CsiData
            {
                TimestampTicks = DateTime.UtcNow.Ticks,
                RawCsiData = dto.Data,
                RawDataLength = dto.Len > 0 ? dto.Len : dto.Data.Length,
                Rssi = dto.Rssi,
                SourceMac = dto.Mac
            };

            // ── Step 4: Non-blocking channel write ──
            // With BoundedChannelFullMode.DropOldest, TryWrite always
            // succeeds unless the channel writer has been completed.
            if (_channelManager.Writer.TryWrite(csiData))
            {
                Interlocked.Increment(ref _messagesEnqueued);
            }
            else
            {
                Interlocked.Increment(ref _messagesDropped);
            }
        }
        catch (JsonException)
        {
            // Malformed JSON from ESP32 — count but don't log per-message
            // to avoid I/O in the hot path.
            Interlocked.Increment(ref _deserializationErrors);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the MQTT client successfully connects to the broker.
    /// </summary>
    private Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        _logger.LogInformation("MQTT client connected to broker.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the MQTT client disconnects (expected or unexpected).
    /// Implements automatic reconnection with a fixed delay.
    /// </summary>
    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        // If we intentionally cancelled, don't reconnect
        if (_cts.IsCancellationRequested)
            return;

        _logger.LogWarning(
            "MQTT disconnected unexpectedly. Reason: {Reason}, WasConnected: {WasConnected}. " +
            "Reconnecting in {Delay}s...",
            e.Reason, e.ClientWasConnected, _options.ReconnectDelaySeconds);

        // ── Reconnection loop with fixed delay ──
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.ReconnectDelaySeconds),
                    _cts.Token);

                if (_mqttClient is null || _clientOptions is null || _subscribeOptions is null)
                    return;

                await _mqttClient.ConnectAsync(_clientOptions, _cts.Token);
                await _mqttClient.SubscribeAsync(_subscribeOptions, _cts.Token);

                _logger.LogInformation(
                    "MQTT reconnected successfully. Resumed subscription to '{Topic}'.",
                    _options.CsiTopic);
                return;
            }
            catch (OperationCanceledException)
            {
                // Shutdown was requested during reconnect — exit gracefully
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "MQTT reconnection attempt failed. Retrying in {Delay}s...",
                    _options.ReconnectDelaySeconds);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  DISPOSAL
    // ═══════════════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
            await _cts.CancelAsync();

        if (_mqttClient is not null)
        {
            _mqttClient.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
            _mqttClient.DisconnectedAsync -= OnDisconnectedAsync;
            _mqttClient.ConnectedAsync -= OnConnectedAsync;
            _mqttClient.Dispose();
        }

        _cts.Dispose();
    }
}
