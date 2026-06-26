using System.Text.Json.Serialization;

namespace CsiRadar.Backend.Infrastructure.Mqtt;

/// <summary>
/// Data Transfer Object representing the JSON payload sent by the ESP32
/// over MQTT. Matches the typical ESP-IDF CSI callback JSON format.
///
/// Example payload from ESP32:
/// <code>
/// {
///   "mac": "AA:BB:CC:DD:EE:FF",
///   "rssi": -45,
///   "channel": 6,
///   "noise_floor": -90,
///   "timestamp": 123456789,
///   "len": 128,
///   "data": [4, -2, 6, 1, 8, -3, ...]
/// }
/// </code>
///
/// The "data" array contains interleaved I/Q (imaginary, real) pairs
/// for each subcarrier. No processing is done here — raw values are
/// passed straight into the Channel buffer.
/// </summary>
public sealed class CsiPayloadDto
{
    /// <summary>
    /// Source MAC address of the transmitting ESP32.
    /// </summary>
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    /// <summary>
    /// Received Signal Strength Indicator.
    /// </summary>
    [JsonPropertyName("rssi")]
    public int Rssi { get; set; }

    /// <summary>
    /// Wi-Fi channel number.
    /// </summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>
    /// Noise floor in dBm.
    /// </summary>
    [JsonPropertyName("noise_floor")]
    public int NoiseFloor { get; set; }

    /// <summary>
    /// ESP32 microsecond timestamp from the CSI callback.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>
    /// Length of the CSI data array (number of I/Q values).
    /// </summary>
    [JsonPropertyName("len")]
    public int Len { get; set; }

    /// <summary>
    /// Raw interleaved CSI I/Q data: [imag_0, real_0, imag_1, real_1, ...].
    /// ESP-IDF's <c>wifi_csi_info_t</c> emits each sample as a signed 8-bit
    /// integer (int8_t, −128..127), so this is modeled as <see cref="sbyte"/>[]
    /// to avoid a 4× memory inflation on the hot path. System.Text.Json
    /// deserializes the JSON number array directly into sbyte[]; out-of-range
    /// values raise a <see cref="System.Text.Json.JsonException"/> that the
    /// listener counts as a deserialization error.
    /// </summary>
    [JsonPropertyName("data")]
    public sbyte[]? Data { get; set; }
}
