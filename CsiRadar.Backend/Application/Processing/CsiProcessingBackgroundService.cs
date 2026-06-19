using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Background service that acts as the CONSUMER in the Producer-Consumer pipeline.
///
/// Workflow:
///   1. Reads CSI frames from Channel&lt;CsiData&gt; (CsiDataChannelManager.Reader)
///   2. Accumulates frames into a sliding window (e.g., 100 frames = 1 second at 100 Hz)
///   3. Sends the window through ISignalProcessor for filtering
///   4. Passes the filtered signal to IOnnxModelEvaluator for inference
///   5. Forwards results to IBroadcastService for SignalR push and automation
///
/// This service runs on its own thread, completely decoupled from the MQTT ingestion.
/// </summary>
public sealed class CsiProcessingBackgroundService : BackgroundService
{
    private readonly CsiDataChannelManager _channelManager;
    private readonly ISignalProcessor _signalProcessor;
    private readonly IOnnxModelEvaluator _modelEvaluator;
    private readonly IBroadcastService _broadcastService;
    private readonly ILogger<CsiProcessingBackgroundService> _logger;

    public CsiProcessingBackgroundService(
        CsiDataChannelManager channelManager,
        ISignalProcessor signalProcessor,
        IOnnxModelEvaluator modelEvaluator,
        IBroadcastService broadcastService,
        ILogger<CsiProcessingBackgroundService> logger)
    {
        _channelManager = channelManager;
        _signalProcessor = signalProcessor;
        _modelEvaluator = modelEvaluator;
        _broadcastService = broadcastService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CSI Processing pipeline started. Waiting for data...");

        // TODO: Implement in Step 2
        // 1. await foreach (var csiData in _channelManager.Reader.ReadAllAsync(stoppingToken))
        // 2. Accumulate into sliding window
        // 3. When window is full: process → infer → broadcast
        // 4. Slide the window forward

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown
        }

        _logger.LogInformation("CSI Processing pipeline stopped.");
    }
}
