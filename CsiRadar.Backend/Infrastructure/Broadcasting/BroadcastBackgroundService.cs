using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Infrastructure.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Infrastructure.Broadcasting;

/// <summary>
/// Drains the loss-tolerant broadcast channel and pushes CSI graph frames to
/// SignalR clients, off the inference-critical consumer thread.
///
/// This is the fix for the "slow client stalls the pipeline" defect: the
/// processing consumer never awaits <c>SendAsync</c>; it only enqueues onto the
/// <see cref="SignalBroadcastChannelManager"/> (DropOldest, depth 2). A slow or
/// dead client can at worst stall <em>this</em> pump, which simply drops stale
/// graph frames — the inbound CSI channel and inference path are untouched.
///
/// Each send is additionally bounded by <see cref="SendTimeout"/> so a single
/// wedged connection cannot block the pump indefinitely.
/// </summary>
public sealed class BroadcastBackgroundService : BackgroundService
{
    /// <summary>Per-send upper bound; a slower client gets its frame dropped.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    private readonly SignalBroadcastChannelManager _broadcastChannel;
    private readonly IHubContext<RadarHub> _hubContext;
    private readonly ILogger<BroadcastBackgroundService> _logger;

    private long _sent;
    private long _sendFailures;

    public BroadcastBackgroundService(
        SignalBroadcastChannelManager broadcastChannel,
        IHubContext<RadarHub> hubContext,
        ILogger<BroadcastBackgroundService> logger)
    {
        _broadcastChannel = broadcastChannel;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Broadcast pump started (SignalR transport decoupled from processing).");

        try
        {
            await foreach (var dto in _broadcastChannel.Reader.ReadAllAsync(stoppingToken))
            {
                // Bound each send so a wedged client can't stall the pump.
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                sendCts.CancelAfter(SendTimeout);

                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveCsiData", dto, sendCts.Token);
                    Interlocked.Increment(ref _sent);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // graceful shutdown
                }
                catch (Exception ex)
                {
                    // Timed-out or failed send — drop the frame and keep pumping.
                    Interlocked.Increment(ref _sendFailures);
                    _logger.LogDebug(ex, "Broadcast send dropped a frame (slow/dead client?).");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }

        _logger.LogInformation(
            "Broadcast pump stopped. Frames sent: {Sent}, send failures: {Failures}.",
            Interlocked.Read(ref _sent), Interlocked.Read(ref _sendFailures));
    }
}
