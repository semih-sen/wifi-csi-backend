using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Recording;

/// <summary>
/// Writes a single recording session to disk: a compact little-endian binary
/// payload (<c>.csibin</c>) plus a JSON sidecar manifest (<c>.json</c>).
///
/// Binary layout (all little-endian; .NET <see cref="BinaryWriter"/> is LE on every
/// platform, matching numpy's default):
///
///   HEADER (once):
///     magic            : 4  bytes  = "CSI1"
///     formatVersion    : int32
///     subcarrierCount  : int32
///     sampleRateHz     : float64
///     captureRaw       : uint8    (0/1)
///     labelLength      : int32
///     label            : labelLength UTF-8 bytes
///     sessionId        : int64
///     startedAtUnixMs  : int64
///
///   FRAME (repeated):
///     timestampMs      : int64
///     rssi             : int32
///     amplitudes       : subcarrierCount × float32
///     [ if captureRaw ]
///       rawLength      : int32
///       raw            : rawLength × int8
///
/// With <c>captureRaw = false</c> the per-frame stride is fixed (12 + 4·sc bytes),
/// so the body maps directly onto a packed numpy structured dtype.
///
/// The header is written lazily on the first frame, when the subcarrier count is
/// first known. Not thread-safe — driven solely by the single-reader writer loop.
/// </summary>
internal sealed class CsiRecordingFileWriter : IDisposable
{
    private static readonly byte[] Magic = "CSI1"u8.ToArray();
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly RecordingSessionInfo _info;
    private readonly string _binPath;
    private readonly string _manifestPath;
    private readonly int _flushEvery;

    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;

    private bool _headerWritten;
    private int _subcarrierCount;
    private long _frameCount;
    private long _widthMismatchFrames;

    public long FrameCount => _frameCount;
    public int SubcarrierCount => _subcarrierCount;

    public CsiRecordingFileWriter(string outputDirectory, RecordingSessionInfo info, int flushEveryNFrames)
    {
        _info = info;
        _flushEvery = Math.Max(1, flushEveryNFrames);

        Directory.CreateDirectory(outputDirectory);
        string stem = BuildStem(info);
        _binPath = Path.Combine(outputDirectory, stem + ".csibin");
        _manifestPath = Path.Combine(outputDirectory, stem + ".json");

        // CreateNew: never silently clobber an existing session file.
        _stream = new FileStream(
            _binPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: false);
    }

    /// <summary>Appends one filtered frame (and optionally its raw payload).</summary>
    public void WriteFrame(ReadOnlySpan<float> filtered, ReadOnlySpan<sbyte> raw, long timestampMs, int rssi)
    {
        if (!_headerWritten)
        {
            _subcarrierCount = filtered.Length;
            WriteHeader(_subcarrierCount);
            _headerWritten = true;
        }
        else if (filtered.Length != _subcarrierCount)
        {
            // Subcarrier width changed mid-session — the consumer treats this as a
            // stream restart; we cannot append a ragged record. Count and skip.
            _widthMismatchFrames++;
            return;
        }

        _writer.Write(timestampMs);
        _writer.Write(rssi);
        for (int i = 0; i < filtered.Length; i++)
            _writer.Write(filtered[i]);

        if (_info.CaptureRaw)
        {
            _writer.Write(raw.Length);
            for (int i = 0; i < raw.Length; i++)
                _writer.Write(raw[i]); // BinaryWriter.Write(sbyte) → one byte
        }

        _frameCount++;
        if (_frameCount % _flushEvery == 0)
            _stream.Flush();
    }

    private void WriteHeader(int subcarrierCount)
    {
        _writer.Write(Magic);                 // 4 bytes, no length prefix
        _writer.Write(FormatVersion);         // int32
        _writer.Write(subcarrierCount);       // int32
        _writer.Write(_info.SampleRateHz);    // float64
        _writer.Write((byte)(_info.CaptureRaw ? 1 : 0));

        byte[] label = Encoding.UTF8.GetBytes(_info.Label);
        _writer.Write(label.Length);          // int32
        _writer.Write(label);                 // raw bytes

        _writer.Write(_info.SessionId);       // int64
        _writer.Write(_info.StartedAtUnixMs); // int64
    }

    /// <summary>
    /// Flushes and closes the payload, then writes the manifest. The session is
    /// <c>complete</c> only if no frames were dropped or skipped and it was not
    /// interrupted — the contract the Python loader relies on.
    /// </summary>
    public void CloseSession(bool interrupted, long droppedFrames)
    {
        try { _stream.Flush(); } catch { /* best-effort */ }
        _writer.Dispose(); // closes the underlying stream

        var manifest = new RecordingManifest
        {
            SessionId = _info.SessionId,
            Label = _info.Label,
            BinaryFile = Path.GetFileName(_binPath),
            Format = "csibin-v1",
            SubcarrierCount = _subcarrierCount,
            FrameCount = _frameCount,
            DroppedFrames = droppedFrames,
            SkippedFrames = _widthMismatchFrames,
            Complete = !interrupted && droppedFrames == 0 && _widthMismatchFrames == 0,
            Interrupted = interrupted,
            SampleRateHz = _info.SampleRateHz,
            LowPassCutoffHz = _info.LowPassCutoffHz,
            FilterOrder = _info.FilterOrder,
            WindowSize = _info.WindowSize,
            SlideStep = _info.SlideStep,
            CaptureRaw = _info.CaptureRaw,
            BaselineApplied = _info.BaselineApplied,
            StartedAtUnixMs = _info.StartedAtUnixMs,
            StoppedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        File.WriteAllText(_manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    public void Dispose()
    {
        try { _writer.Dispose(); } catch { /* idempotent */ }
    }

    /// <summary>
    /// Builds a collision-free, path-traversal-safe file stem. The label is
    /// sanitized to <c>[A-Za-z0-9_-]</c> (so a malicious label can neither escape
    /// the output directory nor inject path separators); session id and start time
    /// guarantee uniqueness regardless.
    /// </summary>
    private static string BuildStem(RecordingSessionInfo info)
    {
        ReadOnlySpan<char> label = info.Label;
        var sb = new StringBuilder(label.Length);
        foreach (char c in label)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');

        string safe = sb.ToString();
        if (safe.Length == 0) safe = "unlabeled";
        if (safe.Length > 48) safe = safe[..48];

        return $"{info.SessionId:D6}_{safe}_{info.StartedAtUnixMs}";
    }
}

/// <summary>
/// JSON sidecar describing one recording session — the index the model team globs
/// to assemble the training set. <see cref="Complete"/> is the integrity gate:
/// sessions with dropped/skipped frames or that were interrupted are flagged so
/// they can be excluded.
/// </summary>
internal sealed class RecordingManifest
{
    public long SessionId { get; init; }
    public string Label { get; init; } = string.Empty;
    public string BinaryFile { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;

    public int SubcarrierCount { get; init; }
    public long FrameCount { get; init; }
    public long DroppedFrames { get; init; }
    public long SkippedFrames { get; init; }

    public bool Complete { get; init; }
    public bool Interrupted { get; init; }

    public double SampleRateHz { get; init; }
    public double LowPassCutoffHz { get; init; }
    public int FilterOrder { get; init; }
    public int WindowSize { get; init; }
    public int SlideStep { get; init; }

    public bool CaptureRaw { get; init; }
    public bool BaselineApplied { get; init; }

    public long StartedAtUnixMs { get; init; }
    public long StoppedAtUnixMs { get; init; }
}