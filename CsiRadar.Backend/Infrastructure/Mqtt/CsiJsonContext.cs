using System.Text.Json.Serialization;

namespace CsiRadar.Backend.Infrastructure.Mqtt;

/// <summary>
/// Source-generated JSON serializer context for zero-reflection,
/// AOT-compatible, allocation-reduced deserialization of ESP32 payloads.
///
/// Using source generators avoids runtime reflection and significantly
/// reduces hot-path allocations — critical at 100 Hz ingestion rate.
/// </summary>
[JsonSerializable(typeof(CsiPayloadDto))]
internal partial class CsiJsonContext : JsonSerializerContext;
