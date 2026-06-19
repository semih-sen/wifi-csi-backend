namespace CsiRadar.Backend.Core.Configuration;

/// <summary>
/// Configuration options for the MQTT connection.
/// Bound from appsettings.json section "Mqtt".
/// </summary>
public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    /// <summary>
    /// Hostname or IP address of the Mosquitto MQTT broker.
    /// </summary>
    public string BrokerHost { get; set; } = "localhost";

    /// <summary>
    /// Port number for the MQTT broker (default: 1883 for non-TLS).
    /// </summary>
    public int BrokerPort { get; set; } = 1883;

    /// <summary>
    /// MQTT topic to subscribe to for raw CSI data from the ESP32.
    /// </summary>
    public string CsiTopic { get; set; } = "sensor/csi/raw";

    /// <summary>
    /// MQTT topic to publish automation status to (for Home Assistant).
    /// </summary>
    public string AutomationTopic { get; set; } = "home/radar/automation";

    /// <summary>
    /// MQTT topic to publish radar status to.
    /// </summary>
    public string StatusTopic { get; set; } = "home/radar/status";

    /// <summary>
    /// Client ID for the MQTT connection.
    /// </summary>
    public string ClientId { get; set; } = "CsiRadar-Backend";

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Reconnection delay in seconds when the broker connection is lost.
    /// </summary>
    public int ReconnectDelaySeconds { get; set; } = 5;
}
