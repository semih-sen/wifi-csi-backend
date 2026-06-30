using System.Runtime.InteropServices;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Infrastructure.Mqtt;

/// <summary>Outcome of decoding one binary CSI MQTT payload.</summary>
public enum CsiBinaryParseStatus
{
    /// <summary>Header + I/Q decoded into a <see cref="CsiData"/>.</summary>
    Ok,
    /// <summary>Payload shorter than the fixed header.</summary>
    TooShort,
    /// <summary>Magic bytes did not match — not a CSI frame (or wrong topic).</summary>
    BadMagic,
    /// <summary>Magic matched but the version byte is not <see cref="CsiBinaryProtocol.Version"/>.</summary>
    UnsupportedVersion,
    /// <summary>Header decoded but the declared I/Q block runs past the payload end.</summary>
    Truncated,
}

/// <summary>
/// The V2 binary MQTT wire contract (ESP32 ↔ Backend, Seam: ingestion).
///
/// Two RX × raw I/Q × 100 Hz cannot ride JSON, so V2 replaces the JSON payload with
/// a versioned little-endian binary frame. The listener asserts the magic + version
/// and rejects mismatches loudly (drift between firmware and backend is observable,
/// not silent garbage).
///
/// <code>
/// Offset  Size  Field
/// 0       2     Magic            = 'C','R'   (0x43 0x52)
/// 2       1     Version          = 2
/// 3       6     DeviceMac        big-endian (MSB first, as printed)
/// 9       4     SeqNo            uint32, little-endian (echoed from the TX ping)
/// 13      2     Rssi             int16,  little-endian
/// 15      1     SubcarrierCount  uint8 = N
/// 16      2·N   I/Q              per subcarrier: [imag(int8), real(int8)]
/// </code>
///
/// Total payload = <see cref="HeaderSize"/> + 2·N bytes. Integers are little-endian
/// (ESP32 native); the MAC is stored big-endian so the packed <see cref="long"/>
/// reads in the same order it is printed.
/// </summary>
public static class CsiBinaryProtocol
{
    public const byte Magic0 = (byte)'C';
    public const byte Magic1 = (byte)'R';
    public const byte Version = 2;

    /// <summary>Fixed header length in bytes (everything before the I/Q block).</summary>
    public const int HeaderSize = 16;

    // Field offsets within the header.
    private const int OffsetVersion = 2;
    private const int OffsetMac = 3;
    private const int OffsetSeq = 9;
    private const int OffsetRssi = 13;
    private const int OffsetScCount = 15;

    /// <summary>
    /// Decodes one binary payload into a <see cref="CsiData"/>. Allocates exactly the
    /// frame object plus its <c>sbyte[]</c> I/Q buffer (equivalent to the old JSON
    /// path), copied out of the transient MQTT buffer so the frame outlives the span.
    /// </summary>
    public static CsiBinaryParseStatus TryParse(
        ReadOnlySpan<byte> payload, long timestampTicks, out CsiData? data)
    {
        data = null;

        if (payload.Length < HeaderSize)
            return CsiBinaryParseStatus.TooShort;
        if (payload[0] != Magic0 || payload[1] != Magic1)
            return CsiBinaryParseStatus.BadMagic;
        if (payload[OffsetVersion] != Version)
            return CsiBinaryParseStatus.UnsupportedVersion;

        long mac =
            ((long)payload[OffsetMac] << 40) |
            ((long)payload[OffsetMac + 1] << 32) |
            ((long)payload[OffsetMac + 2] << 24) |
            ((long)payload[OffsetMac + 3] << 16) |
            ((long)payload[OffsetMac + 4] << 8) |
            payload[OffsetMac + 5];

        uint seq = (uint)(
            payload[OffsetSeq] |
            (payload[OffsetSeq + 1] << 8) |
            (payload[OffsetSeq + 2] << 16) |
            (payload[OffsetSeq + 3] << 24));

        short rssi = (short)(payload[OffsetRssi] | (payload[OffsetRssi + 1] << 8));

        int subcarriers = payload[OffsetScCount];
        int iqLen = subcarriers * 2;
        if (payload.Length < HeaderSize + iqLen)
            return CsiBinaryParseStatus.Truncated;

        var raw = new sbyte[iqLen];
        // int8 I/Q: a reinterpret cast of the byte span (same width, no per-element loop).
        MemoryMarshal.Cast<byte, sbyte>(payload.Slice(HeaderSize, iqLen)).CopyTo(raw);

        data = new CsiData
        {
            TimestampTicks = timestampTicks,
            RawCsiData = raw,
            RawDataLength = iqLen,
            Rssi = rssi,
            DeviceMac = mac,
            SeqNo = seq,
        };
        return CsiBinaryParseStatus.Ok;
    }

    /// <summary>
    /// Parses "AA:BB:CC:DD:EE:FF" (colons/dashes/spaces optional) into the same packed
    /// big-endian <see cref="long"/> used by <see cref="CsiData.DeviceMac"/>. Returns
    /// false for anything that is not exactly 12 hex nibbles.
    /// </summary>
    public static bool TryParseMac(string? mac, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(mac))
            return false;

        long v = 0;
        int nibbles = 0;
        foreach (char c in mac)
        {
            if (c is ':' or '-' or ' ')
                continue;
            int d = FromHex(c);
            if (d < 0 || nibbles >= 12)
                return false;
            v = (v << 4) | (uint)d;
            nibbles++;
        }

        if (nibbles != 12)
            return false;

        value = v;
        return true;
    }

    /// <summary>Formats a packed MAC back to "AA:BB:CC:DD:EE:FF" (for logs/diagnostics).</summary>
    public static string FormatMac(long mac) =>
        $"{(byte)(mac >> 40):X2}:{(byte)(mac >> 32):X2}:{(byte)(mac >> 24):X2}:" +
        $"{(byte)(mac >> 16):X2}:{(byte)(mac >> 8):X2}:{(byte)mac:X2}";

    private static int FromHex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
