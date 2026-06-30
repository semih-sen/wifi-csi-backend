using CsiRadar.Backend.Infrastructure.Mqtt;
using Xunit;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Pins the V2 binary MQTT wire contract. The encoder here is hand-rolled (not the
/// production parser) so the byte layout is asserted by the test, mirroring how the
/// firmware must emit it.
/// </summary>
public class CsiBinaryProtocolTests
{
    /// <summary>Builds a v2 binary payload with the documented little-endian layout.</summary>
    private static byte[] Encode(long mac, uint seq, short rssi, sbyte[] iq)
    {
        int n = iq.Length / 2;
        var buf = new byte[CsiBinaryProtocol.HeaderSize + iq.Length];
        buf[0] = CsiBinaryProtocol.Magic0;
        buf[1] = CsiBinaryProtocol.Magic1;
        buf[2] = CsiBinaryProtocol.Version;
        // MAC big-endian (MSB first)
        buf[3] = (byte)(mac >> 40);
        buf[4] = (byte)(mac >> 32);
        buf[5] = (byte)(mac >> 24);
        buf[6] = (byte)(mac >> 16);
        buf[7] = (byte)(mac >> 8);
        buf[8] = (byte)mac;
        // seqNo little-endian
        buf[9] = (byte)seq;
        buf[10] = (byte)(seq >> 8);
        buf[11] = (byte)(seq >> 16);
        buf[12] = (byte)(seq >> 24);
        // rssi little-endian
        buf[13] = (byte)rssi;
        buf[14] = (byte)(rssi >> 8);
        buf[15] = (byte)n;
        for (int i = 0; i < iq.Length; i++)
            buf[CsiBinaryProtocol.HeaderSize + i] = unchecked((byte)iq[i]);
        return buf;
    }

    [Fact]
    public void TryParse_ValidFrame_RoundTripsAllFields()
    {
        long mac = 0xAABBCCDDEEF0;
        var iq = new sbyte[] { 1, -2, 3, -4, 127, -128 }; // 3 subcarriers
        byte[] payload = Encode(mac, seq: 42, rssi: -57, iq);

        var status = CsiBinaryProtocol.TryParse(payload, timestampTicks: 123, out var data);

        Assert.Equal(CsiBinaryParseStatus.Ok, status);
        Assert.NotNull(data);
        Assert.Equal(mac, data!.DeviceMac);
        Assert.Equal(42u, data.SeqNo);
        Assert.Equal(-57, data.Rssi);
        Assert.Equal(123, data.TimestampTicks);
        Assert.Equal(6, data.RawDataLength);
        Assert.Equal(iq, data.RawCsiData);
    }

    [Fact]
    public void TryParse_BadMagic_IsRejected()
    {
        byte[] payload = Encode(1, 1, 0, new sbyte[] { 0, 0 });
        payload[0] = (byte)'X';

        var status = CsiBinaryProtocol.TryParse(payload, 0, out var data);

        Assert.Equal(CsiBinaryParseStatus.BadMagic, status);
        Assert.Null(data);
    }

    [Fact]
    public void TryParse_WrongVersion_IsAssertedLoudly()
    {
        byte[] payload = Encode(1, 1, 0, new sbyte[] { 0, 0 });
        payload[2] = 99; // future/foreign protocol version

        var status = CsiBinaryProtocol.TryParse(payload, 0, out _);

        Assert.Equal(CsiBinaryParseStatus.UnsupportedVersion, status);
    }

    [Fact]
    public void TryParse_ShorterThanHeader_IsTooShort()
    {
        var status = CsiBinaryProtocol.TryParse(new byte[8], 0, out _);
        Assert.Equal(CsiBinaryParseStatus.TooShort, status);
    }

    [Fact]
    public void TryParse_DeclaredIqRunsPastEnd_IsTruncated()
    {
        byte[] payload = Encode(1, 1, 0, new sbyte[] { 1, 2, 3, 4 }); // declares 2 subcarriers
        // Chop a byte off the I/Q block so the declared length overruns the buffer.
        Array.Resize(ref payload, payload.Length - 1);

        var status = CsiBinaryProtocol.TryParse(payload, 0, out _);

        Assert.Equal(CsiBinaryParseStatus.Truncated, status);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:F0", 0xAABBCCDDEEF0)]
    [InlineData("aa-bb-cc-dd-ee-f1", 0xAABBCCDDEEF1)]
    [InlineData("AABBCCDDEEFF", 0xAABBCCDDEEFF)]
    public void TryParseMac_AcceptsCommonFormats(string text, long expected)
    {
        Assert.True(CsiBinaryProtocol.TryParseMac(text, out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-mac")]
    [InlineData("AA:BB:CC:DD:EE")]   // too few
    [InlineData("AA:BB:CC:DD:EE:FF:00")] // too many
    public void TryParseMac_RejectsInvalid(string text)
    {
        Assert.False(CsiBinaryProtocol.TryParseMac(text, out _));
    }

    [Fact]
    public void FormatMac_RoundTripsParseMac()
    {
        Assert.True(CsiBinaryProtocol.TryParseMac("12:34:56:78:9A:BC", out long mac));
        Assert.Equal("12:34:56:78:9A:BC", CsiBinaryProtocol.FormatMac(mac));
    }
}
