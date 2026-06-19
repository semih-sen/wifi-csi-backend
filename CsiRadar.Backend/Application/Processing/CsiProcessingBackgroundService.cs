using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Background service that acts as the CONSUMER in the Producer-Consumer pipeline.
///
/// Workflow:
///   1. Reads CSI frames from Channel&lt;CsiData&gt; via ReadAllAsync
///   2. Accumulates frames into a circular sliding window buffer
///   3. When the window is full, passes it to ISignalProcessor for filtering
///   4. Forwards filtered results to IBroadcastService for SignalR push
///   5. Slides the window forward by SlideStep frames
///
/// The ONNX inference step is skipped in this iteration (Step 3).
/// It will be wired in Step 4 when the Python model is trained.
///
/// This service runs on its own thread, completely decoupled from MQTT ingestion.
/// </summary>
public sealed class CsiProcessingBackgroundService : BackgroundService
{
    private readonly CsiDataChannelManager _channelManager;
    private readonly ISignalProcessor _signalProcessor;
    private readonly IOnnxModelEvaluator _modelEvaluator;
    private readonly IBroadcastService _broadcastService;
    private readonly ProcessingOptions _options;
    private readonly ILogger<CsiProcessingBackgroundService> _logger;

    // ── Diagnostics ──
    private long _windowsProcessed;
    private long _framesConsumed;

    public CsiProcessingBackgroundService(
        CsiDataChannelManager channelManager,
        ISignalProcessor signalProcessor,
        IOnnxModelEvaluator modelEvaluator,
        IBroadcastService broadcastService,
        IOptions<ProcessingOptions> options,
        ILogger<CsiProcessingBackgroundService> logger)
    {
        _channelManager = channelManager;
        _signalProcessor = signalProcessor;
        _modelEvaluator = modelEvaluator;
        _broadcastService = broadcastService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CSI Processing pipeline started. WindowSize={WindowSize}, SlideStep={SlideStep}, " +
            "LowPass={CutoffHz} Hz, SampleRate={SampleRate} Hz.",
            _options.WindowSize, _options.SlideStep,
            _options.LowPassCutoffHz, _options.SamplingRateHz);

        // ── Sliding window buffer ──
        // Pre-allocate the full window. We fill it from index 0..WindowSize-1,
        // then after processing, shift the last (WindowSize - SlideStep) frames
        // to the front and refill the remaining SlideStep slots.
        var windowBuffer = new CsiData[_options.WindowSize];
        int fillIndex = 0; // Next position to write in the buffer

        try
        {
            await foreach (var csiData in _channelManager.Reader.ReadAllAsync(stoppingToken))
            {
                Interlocked.Increment(ref _framesConsumed);

                // ── Accumulate frame into the window buffer ──
                windowBuffer[fillIndex] = csiData;
                fillIndex++;

                // ── Check if the window is full ──
                if (fillIndex < _options.WindowSize)
                    continue;

                Interlocked.Increment(ref _windowsProcessed);

                try
                {
                    // ── Process the full window ──
                    var windowSpan = new ReadOnlySpan<CsiData>(windowBuffer, 0, _options.WindowSize);
                    var filteredSignal = _signalProcessor.ProcessWindow(windowSpan);

                    if (filteredSignal.Length > 0)
                    {
                        // ── Broadcast filtered data via SignalR ──
                        // We send the latest frame (most recent timestamp) along with
                        // its now-populated SubcarrierAmplitudes for visualization.
                        // ONNX için 6400 elemanlı (flattened) matrisimiz var. 
                        // Sadece SignalR (Grafik) için son anı (t = 99) cımbızlıyoruz:
                        int subcarrierCount = filteredSignal.Length / _options.WindowSize;
                        var latestAmplitudes = new float[subcarrierCount];

                        for (int sc = 0; sc < subcarrierCount; sc++)
                        {
                            // Matristeki her alt taşıyıcının son zaman indeksini (WindowSize - 1) çekiyoruz
                            int offset = sc * _options.WindowSize;
                            latestAmplitudes[sc] = filteredSignal[offset + (_options.WindowSize - 1)];
                        }

                        var latestFrame = windowBuffer[_options.WindowSize - 1];
                        // 6400 elemanlı diziyi değil, sadece güncel 64 elemanlı diziyi ön yüze veriyoruz
                        latestFrame.SubcarrierAmplitudes = latestAmplitudes;
                        latestFrame.SubcarrierCount = subcarrierCount;

                        await _broadcastService.BroadcastCsiDataAsync(latestFrame, stoppingToken);

                        // ── ONNX Inference (Step 4 — skipped for now) ──
                        // TODO: Uncomment when the ONNX model is available.
                        // var inferenceResult = _modelEvaluator.Predict(filteredSignal);
                        // await _broadcastService.BroadcastInferenceResultAsync(inferenceResult, stoppingToken);
                        //
                        // // Check for automation trigger (e.g., LyingOnCouch for 3+ consecutive windows)
                        // if (inferenceResult.Confidence >= confidenceThreshold)
                        //     await _broadcastService.TriggerAutomationAsync(inferenceResult.PredictedLabel, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error processing window #{WindowNumber}.",
                        Interlocked.Read(ref _windowsProcessed));
                    // Continue processing — don't let one bad window kill the pipeline
                }

                // ── Slide the window forward ──
                // Keep the last (WindowSize - SlideStep) frames and shift them to the front.
                int keepCount = _options.WindowSize - _options.SlideStep;
                if (keepCount > 0)
                {
                    Array.Copy(windowBuffer, _options.SlideStep, windowBuffer, 0, keepCount);
                    // Clear the freed slots to allow GC of old CsiData references
                    Array.Clear(windowBuffer, keepCount, _options.SlideStep);
                }
                else
                {
                    // SlideStep >= WindowSize: non-overlapping windows, clear everything
                    Array.Clear(windowBuffer, 0, _options.WindowSize);
                }

                fillIndex = keepCount > 0 ? keepCount : 0;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown
        }

        _logger.LogInformation(
            "CSI Processing pipeline stopped. " +
            "Frames consumed: {Frames}, Windows processed: {Windows}.",
            Interlocked.Read(ref _framesConsumed),
            Interlocked.Read(ref _windowsProcessed));
    }
}
