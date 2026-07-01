using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Infrastructure.Broadcasting;

/// <summary>
/// Drains the inference broadcast channel and pushes results to clients off the
/// inference-critical consumer thread, mirroring <see cref="DspBroadcastBackgroundService"/>
/// for viz frames.
///
/// Every message broadcasts a per-window <c>ReceiveInference</c> event (so the UI's
/// live scores animate). When a message carries a freshly confirmed status, it also
/// triggers the Home Assistant automation via <see cref="IBroadcastService.TriggerAutomationAsync"/>,
/// which dedupes + emits <c>ReceiveStatus</c>.
/// </summary>
public sealed class InferenceBroadcastBackgroundService : BackgroundService
{
    private readonly InferenceBroadcastChannelManager _channel;
    private readonly IBroadcastService _broadcast;
    private readonly ILogger<InferenceBroadcastBackgroundService> _logger;

    private long _broadcastCount;
    private long _confirmations;
    private long _failures;

    public InferenceBroadcastBackgroundService(
        InferenceBroadcastChannelManager channel,
        IBroadcastService broadcast,
        ILogger<InferenceBroadcastBackgroundService> logger)
    {
        _channel = channel;
        _broadcast = broadcast;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Inference broadcast pump started.");

        try
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _broadcast.BroadcastInferenceResultAsync(msg.Result, stoppingToken);
                    Interlocked.Increment(ref _broadcastCount);

                    if (msg.ConfirmedStatus is { } status)
                    {
                        await _broadcast.TriggerAutomationAsync(status, stoppingToken);
                        Interlocked.Increment(ref _confirmations);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // graceful shutdown
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failures);
                    _logger.LogWarning(ex, "Inference broadcast failed for one window; continuing.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }

        _logger.LogInformation(
            "Inference broadcast pump stopped. Broadcasts: {Broadcasts}, confirmations: {Confirmations}, failures: {Failures}.",
            Interlocked.Read(ref _broadcastCount),
            Interlocked.Read(ref _confirmations),
            Interlocked.Read(ref _failures));
    }
}
