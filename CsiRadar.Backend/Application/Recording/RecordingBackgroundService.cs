using CsiRadar.Backend.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.Recording;

/// <summary>
/// Drains the recording channel and persists sessions to disk, fully off the
/// inference-critical consumer thread. Start/Frame/Stop arrive in FIFO order, so:
///   - Start  → open a new payload file (header deferred to the first frame).
///   - Frame  → append (only for the currently open session; a late frame whose
///              id no longer matches is dropped — the microsecond Stop race).
///   - Stop   → flush, close, and emit the manifest.
///
/// On shutdown any still-open session is finalized as <c>interrupted</c> so a
/// partial file is never left without a manifest.
/// </summary>
public sealed class RecordingBackgroundService : BackgroundService
{
    private readonly RecordingService _recording;
    private readonly RecordingOptions _options;
    private readonly ILogger<RecordingBackgroundService> _logger;

    private CsiRecordingFileWriter? _current;
    private long _currentSessionId = -1;

    public RecordingBackgroundService(
        RecordingService recording,
        IOptions<RecordingOptions> options,
        ILogger<RecordingBackgroundService> logger)
    {
        _recording = recording;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Recording writer started. Output directory: '{Dir}'.", _options.OutputDirectory);

        try
        {
            await foreach (var env in _recording.Reader.ReadAllAsync(stoppingToken))
            {
                switch (env.Kind)
                {
                    case RecordingCommandKind.Start: HandleStart(env); break;
                    case RecordingCommandKind.Frame: HandleFrame(env); break;
                    case RecordingCommandKind.Stop:  HandleStop(env);  break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }
        finally
        {
            if (_current is not null)
            {
                _logger.LogWarning(
                    "Shutdown with session {Id} still open — finalizing as interrupted.",
                    _currentSessionId);
                SafeClose(interrupted: true, droppedFrames: _recording.CurrentDropped);
            }
        }

        _logger.LogInformation("Recording writer stopped.");
    }

    private void HandleStart(RecordingEnvelope env)
    {
        if (_current is not null)
        {
            // A Start without an intervening Stop — should not happen; finalize defensively.
            _logger.LogWarning(
                "Start for session {New} while {Old} is open — finalizing the old session as interrupted.",
                env.SessionId, _currentSessionId);
            SafeClose(interrupted: true, droppedFrames: 0);
        }

        try
        {
            _current = new CsiRecordingFileWriter(
                _options.OutputDirectory, env.SessionInfo!, _options.FlushEveryNFrames);
            _currentSessionId = env.SessionId;

            _logger.LogInformation(
                "Recording file opened for session {Id} (label '{Label}').",
                env.SessionId, env.SessionInfo!.Label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open recording file for session {Id}.", env.SessionId);
            _current = null;
            _currentSessionId = -1;
        }
    }

    private void HandleFrame(RecordingEnvelope env)
    {
        if (_current is null || env.SessionId != _currentSessionId || env.Filtered is null)
            return; // no open session, a late frame for a closed session, or a malformed frame

        try
        {
            _current.WriteFrame(
                env.Filtered,
                env.Raw ?? ReadOnlySpan<sbyte>.Empty,
                env.TimestampMs,
                env.Rssi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write failure on session {Id}.", _currentSessionId);
        }
    }

    private void HandleStop(RecordingEnvelope env)
    {
        if (_current is null || env.SessionId != _currentSessionId)
            return; // already finalized / unknown session

        long frames = _current.FrameCount;
        SafeClose(interrupted: false, droppedFrames: env.DroppedFrames);
        _logger.LogInformation(
            "Recording finalized: session {Id}, frames written {Frames}, dropped {Dropped}.",
            env.SessionId, frames, env.DroppedFrames);
    }

    private void SafeClose(bool interrupted, long droppedFrames)
    {
        var writer = _current;
        if (writer is null)
            return;

        try
        {
            writer.CloseSession(interrupted, droppedFrames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize session {Id}.", _currentSessionId);
        }
        finally
        {
            writer.Dispose();
            _current = null;
            _currentSessionId = -1;
        }
    }
}