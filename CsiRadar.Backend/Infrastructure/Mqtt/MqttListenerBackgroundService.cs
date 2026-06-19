using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Infrastructure.Mqtt;

/// <summary>
/// Background service that manages the MQTT connection lifecycle.
/// Acts as the PRODUCER in the Producer-Consumer pipeline:
///   1. Connects to the Mosquitto broker on startup
///   2. Subscribes to the CSI topic (sensor/csi/raw)
///   3. On each message received, parses the payload and pushes it
///      into the Channel&lt;CsiData&gt; buffer with zero processing
///   4. Gracefully disconnects on shutdown via CancellationToken
///
/// CRITICAL: The MQTT event handler must be ultra-lean — no Task.Run,
/// no I/O, no math. Just parse and enqueue.
/// </summary>
public sealed class MqttListenerBackgroundService : BackgroundService
{
    private readonly IMqttClientService _mqttClient;
    private readonly ILogger<MqttListenerBackgroundService> _logger;

    public MqttListenerBackgroundService(
        IMqttClientService mqttClient,
        ILogger<MqttListenerBackgroundService> logger)
    {
        _mqttClient = mqttClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MQTT Listener starting. Connecting to broker...");

        // TODO: Implement connection logic with retry policy
        await _mqttClient.ConnectAsync(stoppingToken);

        _logger.LogInformation("MQTT Listener connected and subscribed. Awaiting messages...");

        // Keep the service alive until cancellation is requested
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown
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
