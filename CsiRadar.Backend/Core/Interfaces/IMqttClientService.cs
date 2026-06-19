using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Core.Interfaces;

/// <summary>
/// Abstraction for the MQTT client service.
/// Responsible for connecting to the Mosquitto broker, subscribing to CSI topics,
/// and pushing raw payloads into the Channel&lt;CsiData&gt; buffer.
/// Implementation must NOT perform any heavy processing in the MQTT event handler.
/// </summary>
public interface IMqttClientService
{
    /// <summary>
    /// Establishes a connection to the MQTT broker and begins subscribing
    /// to the configured CSI topic (e.g., sensor/csi/raw).
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gracefully disconnects from the MQTT broker.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a message to the specified MQTT topic.
    /// Used for Home Assistant automation triggers (e.g., home/radar/automation).
    /// </summary>
    Task PublishAsync(string topic, string payload, CancellationToken cancellationToken);
}
