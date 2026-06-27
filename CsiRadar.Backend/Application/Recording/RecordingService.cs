using System.Threading.Channels;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.Recording;

/// <summary>
/// Owns the recording state machine and the channel that feeds the background
/// writer. See <see cref="IRecordingService"/> for the contract.
///
/// Threading model:
///   - <see cref="Start"/>/<see cref="Stop"/> are called from arbitrary threads
///     (SignalR hub invocations) and serialized by <c>_gate</c>. They publish the
///     active session via a single volatile reference (<c>_active</c>).
///   - <see cref="Capture"/> runs on the single consumer thread; it does one
///     volatile read of <c>_active</c> and returns immediately when idle.
///
/// Channel discipline (integrity over loss — the opposite of the broadcast path):
///   - <see cref="BoundedChannelFullMode.Wait"/> + non-blocking <c>TryWrite</c>:
///     a full channel yields <c>false</c> (the consumer is never blocked) and the
///     frame is counted as dropped. Any drop flags the session incomplete in its
///     manifest, so the training set is never silently corrupted.
///   - Buffers on this channel are never pooled (a dropped envelope is simply GC'd),
///     avoiding the ArrayPool×drop leak.
/// </summary>
public sealed class RecordingService : IRecordingService
{
    private readonly RecordingOptions _options;
    private readonly ProcessingOptions _processing;
    private readonly ICsiStreamProcessor _processor;
    private readonly ILogger<RecordingService> _logger;

    private readonly Channel<RecordingEnvelope> _channel;
    private readonly object _gate = new();

    private volatile RecordingSession? _active;
    private long _sessionSeed;

    public RecordingService(
        IOptions<RecordingOptions> options,
        IOptions<ProcessingOptions> processing,
        ICsiStreamProcessor processor,
        ILogger<RecordingService> logger)
    {
        _options = options.Value;
        _processing = processing.Value;
        _processor = processor;
        _logger = logger;

        var channelOptions = new BoundedChannelOptions(Math.Max(16, _options.ChannelCapacity))
        {
            // TryWrite returns false (counted) instead of blocking when full.
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false, // consumer (frames) + hub thread (control)
            SingleReader = true,  // the background writer
        };
        _channel = Channel.CreateBounded<RecordingEnvelope>(channelOptions);
    }

    /// <summary>Reader endpoint for the background writer (same assembly).</summary>
    internal ChannelReader<RecordingEnvelope> Reader => _channel.Reader;

    /// <summary>Dropped-frame count of the active session (0 if idle). For shutdown finalization.</summary>
    internal long CurrentDropped
    {
        get
        {
            var s = _active;
            return s is null ? 0 : Interlocked.Read(ref s.Dropped);
        }
    }

    /// <inheritdoc />
    public bool IsRecording => _active is not null;

    /// <inheritdoc />
    public RecordingStatus Status
    {
        get
        {
            var s = _active;
            return s is null ? RecordingStatus.Idle : Snapshot(s);
        }
    }

    /// <inheritdoc />
    public RecordingStatus Start(string label, string subject = "")
    {
        lock (_gate)
        {
            var existing = _active;
            if (existing is not null)
            {
                _logger.LogWarning(
                    "Start ignored: session {Id} is already recording.", existing.Id);
                return Snapshot(existing);
            }

            long id = Interlocked.Increment(ref _sessionSeed);
            var info = new RecordingSessionInfo
            {
                SessionId = id,
                Label = label,
                Subject = subject ?? string.Empty,
                SampleRateHz = _processing.SamplingRateHz,
                LowPassCutoffHz = _processing.LowPassCutoffHz,
                FilterOrder = _processing.FilterOrder,
                WindowSize = _processing.WindowSize,
                SlideStep = _processing.SlideStep,
                CaptureRaw = _options.CaptureRaw,
                BaselineApplied = _processor.IsCalibrated,
                StartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            var session = new RecordingSession(id, info);

            // Enqueue the Start control BEFORE publishing the session, so the
            // consumer can only enqueue frames after Start is already in the queue.
            var startEnvelope = new RecordingEnvelope
            {
                Kind = RecordingCommandKind.Start,
                SessionId = id,
                SessionInfo = info,
            };
            if (!_channel.Writer.TryWrite(startEnvelope))
            {
                _logger.LogError(
                    "Recording channel saturated at Start — cannot begin session {Id}.", id);
                return RecordingStatus.Idle;
            }

            _active = session; // volatile publish
            _logger.LogInformation(
                "Recording started: session {Id}, label '{Label}', subject '{Subject}', baselineApplied={Baseline}.",
                id, label, info.Subject, info.BaselineApplied);
            return Snapshot(session);
        }
    }

    /// <inheritdoc />
    public RecordingStatus Stop()
    {
        lock (_gate)
        {
            var session = _active;
            if (session is null)
            {
                _logger.LogWarning("Stop ignored: no active recording.");
                return RecordingStatus.Idle;
            }

            // Stop the consumer from enqueuing first, then submit the Stop control.
            _active = null;

            long captured = Interlocked.Read(ref session.Captured);
            long dropped = Interlocked.Read(ref session.Dropped);

            var stopEnvelope = new RecordingEnvelope
            {
                Kind = RecordingCommandKind.Stop,
                SessionId = session.Id,
                DroppedFrames = dropped,
            };
            if (!_channel.Writer.TryWrite(stopEnvelope))
            {
                _logger.LogError(
                    "Recording channel saturated at Stop — session {Id} may not finalize cleanly.",
                    session.Id);
            }

            _logger.LogInformation(
                "Recording stopped: session {Id}, captured {Captured}, dropped {Dropped}.",
                session.Id, captured, dropped);

            return Snapshot(session) with { IsRecording = false };
        }
    }

    /// <inheritdoc />
    public void Capture(ReadOnlySpan<float> filtered, CsiData source)
    {
        var session = _active; // single volatile read
        if (session is null)
            return;            // idle: cheapest possible path

        int sc = filtered.Length;
        if (sc == 0)
            return;

        var copy = new float[sc];
        filtered.CopyTo(copy);

        sbyte[]? raw = null;
        if (_options.CaptureRaw)
        {
            int rawLen = Math.Min(source.RawDataLength, source.RawCsiData.Length);
            raw = new sbyte[rawLen];
            Array.Copy(source.RawCsiData, raw, rawLen);
        }

        var envelope = new RecordingEnvelope
        {
            Kind = RecordingCommandKind.Frame,
            SessionId = session.Id,
            TimestampMs = source.TimestampTicks / TimeSpan.TicksPerMillisecond,
            Rssi = source.Rssi,
            Filtered = copy,
            Raw = raw,
        };

        if (_channel.Writer.TryWrite(envelope))
            Interlocked.Increment(ref session.Captured);
        else
            Interlocked.Increment(ref session.Dropped); // full channel — session will be flagged incomplete
    }

    private static RecordingStatus Snapshot(RecordingSession s) => new()
    {
        IsRecording = true,
        SessionId = s.Id,
        Label = s.Info.Label,
        Subject = s.Info.Subject,
        FramesCaptured = Interlocked.Read(ref s.Captured),
        FramesDropped = Interlocked.Read(ref s.Dropped),
        StartedAtUnixMs = s.Info.StartedAtUnixMs,
    };
}