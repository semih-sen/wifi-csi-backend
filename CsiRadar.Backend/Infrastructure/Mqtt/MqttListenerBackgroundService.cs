using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Infrastructure.Mqtt;

/// <summary>
/// Background service that manages the MQTT connection lifecycle.
/// Acts as the PRODUCER in the Producer-Consumer pipeline:
///   1. Connects to the Mosquitto broker on startup (with retry)
///   2. Subscribes to the CSI topic (sensor/csi/raw)
///   3. On each message received, the MqttClientService parses the payload
///      and pushes it into the Channel&lt;CsiData&gt; buffer with zero processing
///   4. Gracefully disconnects on shutdown via CancellationToken
///
/// CRITICAL: The MQTT event handler must be ultra-lean — no Task.Run,
/// no I/O, no math. Just parse and enqueue.
/// </summary>
public sealed class MqttListenerBackgroundService : BackgroundService
{
    private readonly IMqttClientService _mqttClient;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttListenerBackgroundService> _logger;

    public MqttListenerBackgroundService(
        IMqttClientService mqttClient,
        IOptions<MqttOptions> options,
        ILogger<MqttListenerBackgroundService> logger)
    {
        _mqttClient = mqttClient;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Initial connection with retry ──
        // The broker might not be available immediately on container startup.
        // Retry until connected or cancellation is requested.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation(
                    "MQTT Listener starting. Connecting to broker at {Host}:{Port}...",
                    _options.BrokerHost, _options.BrokerPort);

                await _mqttClient.ConnectAsync(stoppingToken);

                _logger.LogInformation(
                    "MQTT Listener connected and subscribed to '{Topic}'. Awaiting messages...",
                    _options.CsiTopic);

                // Connection succeeded — keep the service alive until shutdown.
                // The MqttClientService handles auto-reconnection internally.
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during graceful shutdown — exit the loop
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "MQTT initial connection failed. Retrying in {Delay}s...",
                    _options.ReconnectDelaySeconds);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_options.ReconnectDelaySeconds),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MQTT Listener stopping. Disconnecting from broker...");

        await _mqttClient.DisconnectAsync(cancellationToken);

        await base.StopAsync(cancellationToken);

        _logger.LogInformation("MQTT Listener stopped gracefully.");
    }
}
