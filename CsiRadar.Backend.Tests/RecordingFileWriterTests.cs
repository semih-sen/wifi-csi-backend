using System.Text;
using System.Text.Json;
using CsiRadar.Backend.Application.Recording;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Round-trips <see cref="CsiRecordingFileWriter"/>: writes a few frames, then
/// re-reads the binary payload byte-for-byte against the documented layout and
/// asserts the manifest's integrity flags. The reader here is an independent,
/// hand-rolled parser (mirroring tools/read_csibin.py) so the format is pinned by
/// the test, not by the writer's own code.
/// </summary>
public class RecordingFileWriterTests
{
    private static readonly byte[] Magic = "CSI1"u8.ToArray();

    private static RecordingSessionInfo Info(string label, bool captureRaw, long startMs = 1_700_000_000_000, string subject = "") => new()
    {
        SessionId = 7,
        Label = label,
        Subject = subject,
        SampleRateHz = 100.0,
        LowPassCutoffHz = 20.0,
        FilterOrder = 4,
        WindowSize = 100,
        SlideStep = 50,
        CaptureRaw = captureRaw,
        BaselineApplied = true,
        StartedAtUnixMs = startMs,
    };

    [Fact]
    public void WritesHeaderAndFrames_FilteredOnly_RoundTrips()
    {
        string dir = NewTempDir();
        try
        {
            var info = Info("Walking", captureRaw: false);

            // 3 frames × 4 subcarriers.
            float[][] frames =
            [
                [1f, 2f, 3f, 4f],
                [5f, 6f, 7f, 8f],
                [9f, 10f, 11f, 12f],
            ];

            string binPath;
            using (var w = new CsiRecordingFileWriter(dir, info, flushEveryNFrames: 1))
            {
                for (int i = 0; i < frames.Length; i++)
                    w.WriteFrame(frames[i], ReadOnlySpan<sbyte>.Empty, timestampMs: 1000 + i, rssi: -40 - i);

                Assert.Equal(3, w.FrameCount);
                Assert.Equal(4, w.SubcarrierCount);
                w.CloseSession(interrupted: false, droppedFrames: 0);
                binPath = Path.Combine(dir, $"{info.SessionId:D6}_Walking_{info.StartedAtUnixMs}.csibin");
            }

            byte[] bytes = File.ReadAllBytes(binPath);
            int off = 0;

            Assert.Equal(Magic, bytes[..4]); off += 4;
            Assert.Equal(2, ReadI32(bytes, ref off));          // version (v2)
            Assert.Equal(4, ReadI32(bytes, ref off));          // subcarrierCount
            Assert.Equal(100.0, ReadF64(bytes, ref off));      // sampleRateHz
            Assert.Equal(0, bytes[off]); off += 1;             // captureRaw flag
            int labelLen = ReadI32(bytes, ref off);
            Assert.Equal("Walking", Encoding.UTF8.GetString(bytes, off, labelLen)); off += labelLen;
            int subjectLen = ReadI32(bytes, ref off);          // v2: subject (empty here)
            Assert.Equal(0, subjectLen); off += subjectLen;
            Assert.Equal(7L, ReadI64(bytes, ref off));         // sessionId
            Assert.Equal(info.StartedAtUnixMs, ReadI64(bytes, ref off));

            for (int i = 0; i < frames.Length; i++)
            {
                Assert.Equal(1000 + i, ReadI64(bytes, ref off));   // timestampMs
                Assert.Equal(-40 - i, ReadI32(bytes, ref off));    // rssi
                for (int s = 0; s < 4; s++)
                    Assert.Equal(frames[i][s], ReadF32(bytes, ref off));
            }

            Assert.Equal(bytes.Length, off); // consumed exactly — no trailing bytes
        }
        finally { //Directory.Delete(dir, recursive: true); 
        }
    }

    [Fact]
    public void CaptureRaw_PersistsRawPayload()
    {
        string dir = NewTempDir();
        try
        {
            var info = Info("Empty", captureRaw: true);
            sbyte[] raw = [4, -2, 6, 1];

            using (var w = new CsiRecordingFileWriter(dir, info, flushEveryNFrames: 1))
            {
                w.WriteFrame([1f, 2f], raw, timestampMs: 42, rssi: -50);
                w.CloseSession(interrupted: false, droppedFrames: 0);
            }

            string binPath = Path.Combine(dir, $"{info.SessionId:D6}_Empty_{info.StartedAtUnixMs}.csibin");
            byte[] bytes = File.ReadAllBytes(binPath);
            int off = 4 + 4 + 4 + 8 + 1;                       // skip to labelLen
            int labelLen = ReadI32(bytes, ref off); off += labelLen;
            int subjectLen = ReadI32(bytes, ref off); off += subjectLen + 8 + 8; // subject + sessionId + startedAt

            Assert.Equal(42L, ReadI64(bytes, ref off));        // timestampMs
            Assert.Equal(-50, ReadI32(bytes, ref off));        // rssi
            Assert.Equal(1f, ReadF32(bytes, ref off));
            Assert.Equal(2f, ReadF32(bytes, ref off));
            Assert.Equal(raw.Length, ReadI32(bytes, ref off)); // rawLength
            foreach (sbyte b in raw)
                Assert.Equal(b, (sbyte)bytes[off++]);
        }
        finally { //Directory.Delete(dir, recursive: true); 
        }
    }

    [Fact]
    public void Subject_RoundTripsInHeaderAndManifest()
    {
        string dir = NewTempDir();
        try
        {
            var info = Info("Walking", captureRaw: false, subject: "Alice");
            using (var w = new CsiRecordingFileWriter(dir, info, flushEveryNFrames: 1))
            {
                w.WriteFrame([1f, 2f, 3f, 4f], ReadOnlySpan<sbyte>.Empty, 1000, -40);
                w.CloseSession(interrupted: false, droppedFrames: 0);
            }

            // Filename folds in the subject for human-friendly gait datasets.
            string stem = $"{info.SessionId:D6}_Walking_Alice_{info.StartedAtUnixMs}";
            byte[] bytes = File.ReadAllBytes(Path.Combine(dir, stem + ".csibin"));

            int off = 4;
            Assert.Equal(2, ReadI32(bytes, ref off));          // version (v2)
            off += 4 + 8 + 1;                                  // subcarrierCount + sampleRateHz + captureRaw
            int labelLen = ReadI32(bytes, ref off);
            Assert.Equal("Walking", Encoding.UTF8.GetString(bytes, off, labelLen)); off += labelLen;
            int subjectLen = ReadI32(bytes, ref off);
            Assert.Equal("Alice", Encoding.UTF8.GetString(bytes, off, subjectLen)); off += subjectLen;
            Assert.Equal(7L, ReadI64(bytes, ref off));         // sessionId follows subject

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, stem + ".json")));
            var root = doc.RootElement;
            Assert.Equal("Alice", root.GetProperty("subject").GetString());
            Assert.Equal("csibin-v2", root.GetProperty("format").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Manifest_FlagsIncomplete_WhenFramesDropped()
    {
        string dir = NewTempDir();
        try
        {
            var info = Info("Walking", captureRaw: false);
            using (var w = new CsiRecordingFileWriter(dir, info, flushEveryNFrames: 1))
            {
                w.WriteFrame([1f, 2f], ReadOnlySpan<sbyte>.Empty, 1, -40);
                w.CloseSession(interrupted: false, droppedFrames: 5); // simulate 5 dropped
            }

            string manifestPath = Path.Combine(dir, $"{info.SessionId:D6}_Walking_{info.StartedAtUnixMs}.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;

            Assert.False(root.GetProperty("complete").GetBoolean());
            Assert.Equal(5, root.GetProperty("droppedFrames").GetInt64());
            Assert.Equal(1, root.GetProperty("frameCount").GetInt64());
            Assert.Equal("Walking", root.GetProperty("label").GetString());
            Assert.Equal(50, root.GetProperty("slideStep").GetInt32());
            Assert.True(root.GetProperty("baselineApplied").GetBoolean());
        }
        finally { //Directory.Delete(dir, recursive: true); 
        }
    }

    [Fact]
    public void UnsafeLabel_IsSanitizedIntoFilename()
    {
        string dir = NewTempDir();
        try
        {
            var info = Info("../../etc/passwd", captureRaw: false);
            using (var w = new CsiRecordingFileWriter(dir, info, flushEveryNFrames: 1))
            {
                w.WriteFrame([1f], ReadOnlySpan<sbyte>.Empty, 1, -40);
                w.CloseSession(interrupted: false, droppedFrames: 0);
            }

            // No path separators escaped the output directory; files live directly under dir.
            string[] produced = Directory.GetFiles(dir);
            Assert.Equal(2, produced.Length); // .csibin + .json
            foreach (string p in produced)
                Assert.Equal(dir, Path.GetDirectoryName(p));
        }
        finally { //Directory.Delete(dir, recursive: true); 
        }
    }

    // ── helpers ──
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csibin_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int ReadI32(byte[] b, ref int o) { int v = BitConverter.ToInt32(b, o); o += 4; return v; }
    private static long ReadI64(byte[] b, ref int o) { long v = BitConverter.ToInt64(b, o); o += 8; return v; }
    private static float ReadF32(byte[] b, ref int o) { float v = BitConverter.ToSingle(b, o); o += 4; return v; }
    private static double ReadF64(byte[] b, ref int o) { double v = BitConverter.ToDouble(b, o); o += 8; return v; }
}