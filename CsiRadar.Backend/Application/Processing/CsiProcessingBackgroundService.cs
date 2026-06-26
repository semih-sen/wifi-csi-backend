using System.Buffers;
using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using CsiRadar.Backend.Infrastructure.Broadcasting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Background service that acts as the CONSUMER in the Producer-Consumer pipeline.
///
/// Stream model (per frame, exactly once):
///   1. Read a raw CSI frame from Channel&lt;CsiData&gt; via ReadAllAsync.
///   2. Demodulate + baseline-subtract + IIR-filter it via <see cref="ICsiStreamProcessor"/>
///      and append the filtered frame to a flat <see cref="CsiRingBuffer"/>.
///   3. Tap the filtered frame into <see cref="IRecordingService"/> (a no-op when
///      not recording) so the training set is the exact signal the model sees.
///   4. Once a full window has accumulated and a SlideStep has elapsed, snapshot
///      the ring (subcarrier-major) into a pooled buffer for ML inference and
///      enqueue the latest filtered frame onto the loss-tolerant broadcast channel.
///
/// The filter runs once per frame, so overlapping windows are free and the IIR
/// state is never corrupted. The consumer never awaits the network or the disk:
/// graph frames go onto <see cref="SignalBroadcastChannelManager"/> and recording
/// frames onto the recording channel, both via non-blocking writes — a slow
/// SignalR client or a stalled disk cannot stall ingestion.
///
/// ONNX inference (Step 4) is wired but disabled until the trained model lands.
/// </summary>
public sealed class CsiProcessingBackgroundService : BackgroundService
{
    private readonly CsiDataChannelManager _channelManager;
    private readonly ICsiStreamProcessor _processor;
    private readonly IOnnxModelEvaluator _modelEvaluator;
    private readonly SignalBroadcastChannelManager _broadcastChannel;
    private readonly IRecordingService _recordingService;
    private readonly ProcessingOptions _options;
    private readonly ILogger<CsiProcessingBackgroundService> _logger;

    // ── Diagnostics ──
    private long _windowsProcessed;
    private long _framesConsumed;

    public CsiProcessingBackgroundService(
        CsiDataChannelManager channelManager,
        ICsiStreamProcessor processor,
        IOnnxModelEvaluator modelEvaluator,
        SignalBroadcastChannelManager broadcastChannel,
        IRecordingService recordingService,
        IOptions<ProcessingOptions> options,
        ILogger<CsiProcessingBackgroundService> logger)
    {
        _channelManager = channelManager;
        _processor = processor;
        _modelEvaluator = modelEvaluator;
        _broadcastChannel = broadcastChannel;
        _recordingService = recordingService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int windowSize = Math.Max(1, _options.WindowSize);
        int slideStep = _options.SlideStep > 0 ? Math.Min(_options.SlideStep, windowSize) : windowSize;

        _logger.LogInformation(
            "CSI Processing pipeline started. WindowSize={WindowSize}, SlideStep={SlideStep}, " +
            "LowPass={CutoffHz} Hz, SampleRate={SampleRate} Hz.",
            windowSize, slideStep, _options.LowPassCutoffHz, _options.SamplingRateHz);

        // Per-stream state, lazily built once the first frame reveals the subcarrier count.
        CsiRingBuffer? ring = null;
        float[] frameBuf = [];           // reused per frame — NOT pooled
        int currentSc = 0;
        int framesSinceWindow = 0;

        try
        {
            await foreach (var csiData in _channelManager.Reader.ReadAllAsync(stoppingToken))
            {
                Interlocked.Increment(ref _framesConsumed);

                int rawLen = Math.Min(csiData.RawDataLength, csiData.RawCsiData.Length);
                if (rawLen < 2)
                    continue;
                int sc = rawLen / 2;

                // (Re)build per-stream buffers on the first frame or a width change.
                if (ring is null || sc != currentSc)
                {
                    if (ring is not null)
                        _logger.LogWarning(
                            "Subcarrier count changed {Old} -> {New}; restarting window accumulation.",
                            currentSc, sc);

                    currentSc = sc;
                    frameBuf = new float[sc];
                    ring = new CsiRingBuffer(sc, CsiRingBuffer.NextPowerOfTwo(windowSize));
                    framesSinceWindow = 0;
                }

                // Filter this frame exactly once.
                int n = _processor.ProcessFrame(csiData, frameBuf);
                if (n == 0)
                    continue;

                // ── Recording tap (no-op when idle; non-blocking when active) ──
                // Records the full per-frame filtered stream — the exact signal the
                // model consumes — so offline windowing reproduces inference 1:1.
                _recordingService.Capture(frameBuf.AsSpan(0, n), csiData);

                // Append to the ring for windowed inference / broadcast.
                ring.Write(frameBuf.AsSpan(0, n));
                framesSinceWindow++;

                // Emit a window only once it is full AND a slide step has elapsed.
                if (ring.Written < windowSize || framesSinceWindow < slideStep)
                    continue;

                framesSinceWindow = 0;
                Interlocked.Increment(ref _windowsProcessed);

                int flatLen = n * windowSize;
                float[] snapshot = ArrayPool<float>.Shared.Rent(flatLen);
                try
                {
                    // Subcarrier-major window snapshot (model input layout).
                    ring.SnapshotSubcarrierMajor(windowSize, snapshot.AsSpan(0, flatLen));

                    // ── Broadcast the latest filtered frame (non-blocking, loss-tolerant) ──
                    EnqueueBroadcast(csiData, frameBuf, n);

                    // ── ONNX Inference (Step 4 — disabled until the model is available) ──
                    // The pooled snapshot is the model input and MUST be consumed
                    // before the finally below returns it to the pool.
                    // var inferenceResult = _modelEvaluator.Predict(snapshot.AsSpan(0, flatLen));
                    // (route the result + automation through the broadcast pump / debounce — Phase 4)
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error processing window #{WindowNumber}.",
                        Interlocked.Read(ref _windowsProcessed));
                    // Continue — one bad window must not kill the pipeline.
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(snapshot);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }

        _logger.LogInformation(
            "CSI Processing pipeline stopped. Frames consumed: {Frames}, Windows processed: {Windows}.",
            Interlocked.Read(ref _framesConsumed),
            Interlocked.Read(ref _windowsProcessed));
    }

    /// <summary>
    /// Copies the latest filtered amplitudes into a fresh DTO and enqueues it on
    /// the loss-tolerant broadcast channel. Runs at most once per SlideStep
    /// (~1–2 Hz), so this small allocation is off the hot path. Never awaits the
    /// network; a full channel simply drops the oldest pending frame.
    /// </summary>
    private void EnqueueBroadcast(CsiData latest, float[] frameBuf, int subcarrierCount)
    {
        var amplitudes = new float[subcarrierCount];
        Array.Copy(frameBuf, amplitudes, subcarrierCount);

        var dto = new CsiSignalDto
        {
            TimestampMs = latest.TimestampTicks / TimeSpan.TicksPerMillisecond,
            Rssi = latest.Rssi,
            SubcarrierCount = subcarrierCount,
            Amplitudes = amplitudes
        };

        _broadcastChannel.Writer.TryWrite(dto);
    }
}