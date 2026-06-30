using System.Buffers;
using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Application.Processing;
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
///   - Decodes incoming binary payloads via the versioned <see cref="CsiBinaryProtocol"/>
///   - Writes decoded per-RX CsiData into the BoundedChannel via TryWrite (non-blocking)
///   - Publishes messages for Home Assistant automation triggers
///   - Auto-reconnects on unexpected disconnections
///
/// HOT-PATH CONTRACT:
///   The <see cref="OnMessageReceivedAsync"/> handler is the hottest code path
///   in the entire system (~200 invocations/sec across two RX). It MUST:
///     ✓ Decode the fixed binary layout (no reflection, no string parsing)
///     ✓ Call TryWrite (never awaitable WriteAsync)
///     ✓ Return Task.CompletedTask (cached, zero-alloc)
///     ✗ Never call Task.Run
///     ✗ Never perform I/O, logging, or math (the version-drift log fires once only)
///     ✗ Never allocate beyond the CsiData object + its I/Q buffer
/// </summary>
public sealed class MqttClientService : IMqttClientService, IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly CsiDataChannelManager _channelManager;
    private readonly IngestionDiagnostics _diagnostics;
    private readonly ILogger<MqttClientService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly MqttClientFactory _factory = new();

    private IMqttClient? _mqttClient;
    private MqttClientOptions? _clientOptions;
    private MqttClientSubscribeOptions? _subscribeOptions;

    // Latched so a firmware/backend protocol-version drift is reported once, loudly,
    // without spamming the hot path at the frame rate.
    private int _versionMismatchLogged;

    public MqttClientService(
        IOptions<MqttOptions> options,
        CsiDataChannelManager channelManager,
        IngestionDiagnostics diagnostics,
        ILogger<MqttClientService> logger)
    {
        _options = options.Value;
        _channelManager = channelManager;
        _diagnostics = diagnostics;
        _logger = logger;
    }

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

        var s = _diagnostics.Snapshot();
        _logger.LogInformation(
            "MQTT client stopped. Stats: Received={Received}, Decoded={Decoded}, Dropped={Dropped}, " +
            "BadMagic={BadMagic}, VersionMismatch={VersionMismatch}, Truncated={Truncated}.",
            s.MessagesReceived, s.Decoded, s.Dropped, s.BadMagic, s.VersionMismatch, s.Truncated);
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
    /// HOT PATH — Called ~200 times/second (two RX at 100 Hz).
    ///
    /// Pipeline:
    ///   1. Read the payload (zero-copy ReadOnlySpan for the single-segment case)
    ///   2. Decode the versioned binary layout via <see cref="CsiBinaryProtocol.TryParse"/>
    ///      (asserts magic + version; copies the int8 I/Q out of the transient buffer)
    ///   3. TryWrite the decoded per-RX frame into the raw BoundedChannel
    ///   4. Tally the decode outcome on <see cref="IngestionDiagnostics"/>
    ///   5. Return Task.CompletedTask (cached singleton, zero-alloc)
    ///
    /// Alignment by seqNo happens one stage downstream (the alignment service), off
    /// this thread — the listener stays ultra-lean.
    ///
    /// NEVER add: Task.Run, per-message logging, I/O, LINQ, string formatting. The
    /// one-shot version-drift warning is the only logging path.
    /// </summary>
    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        _diagnostics.IncMessagesReceived();

        var payload = e.ApplicationMessage.Payload;
        if (payload.IsEmpty)
            return Task.CompletedTask;

        long timestampTicks = DateTime.UtcNow.Ticks;

        CsiBinaryParseStatus status;
        CsiData? csiData;
        if (payload.IsSingleSegment)
        {
            status = CsiBinaryProtocol.TryParse(payload.FirstSpan, timestampTicks, out csiData);
        }
        else
        {
            // Multi-segment fallback (rare for small CSI payloads): coalesce into a
            // contiguous span. Small frames go on the stack; oversized ones rent.
            int len = (int)payload.Length;
            byte[]? rented = len <= 512 ? null : ArrayPool<byte>.Shared.Rent(len);
            Span<byte> tmp = rented is null ? stackalloc byte[len] : rented.AsSpan(0, len);
            payload.CopyTo(tmp);
            status = CsiBinaryProtocol.TryParse(tmp, timestampTicks, out csiData);
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }

        switch (status)
        {
            case CsiBinaryParseStatus.Ok:
                // DropOldest: TryWrite only fails if the writer has been completed.
                if (_channelManager.Writer.TryWrite(csiData!))
                    _diagnostics.IncDecoded();
                else
                    _diagnostics.IncDropped();
                break;

            case CsiBinaryParseStatus.BadMagic:
                _diagnostics.IncBadMagic();
                break;

            case CsiBinaryParseStatus.UnsupportedVersion:
                _diagnostics.IncVersionMismatch();
                // Loud-once: protocol drift between firmware and backend must be visible.
                if (Interlocked.Exchange(ref _versionMismatchLogged, 1) == 0)
                    _logger.LogError(
                        "Binary CSI payload version mismatch (backend expects v{Version}). " +
                        "Firmware/backend protocol drift — frames are being rejected. " +
                        "Suppressing further version logs.", CsiBinaryProtocol.Version);
                break;

            case CsiBinaryParseStatus.TooShort:
            case CsiBinaryParseStatus.Truncated:
                _diagnostics.IncTruncated();
                break;
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
