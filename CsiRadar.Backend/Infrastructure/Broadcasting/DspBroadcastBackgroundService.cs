using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Infrastructure.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Infrastructure.Broadcasting;

/// <summary>
/// Drains the loss-tolerant DSP viz channel and pushes <see cref="DspFrameDto"/> frames
/// to SignalR clients as <c>ReceiveDspFrame</c>, off the DSP consumer thread.
///
/// Same decoupling discipline as the (retired) V1 graph pump: the DSP stage only
/// enqueues via a non-blocking <c>TryWrite</c> onto a DropOldest depth-2 channel; a
/// slow or dead client can at worst stall <em>this</em> pump, which simply drops stale
/// viz frames. Each send is bounded by <see cref="SendTimeout"/> so a wedged connection
/// cannot block the pump indefinitely.
/// </summary>
public sealed class DspBroadcastBackgroundService : BackgroundService
{
    /// <summary>Per-send upper bound; a slower client gets its frame dropped.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    private readonly DspBroadcastChannelManager _broadcastChannel;
    private readonly IHubContext<RadarHub> _hubContext;
    private readonly ILogger<DspBroadcastBackgroundService> _logger;

    private long _sent;
    private long _sendFailures;

    public DspBroadcastBackgroundService(
        DspBroadcastChannelManager broadcastChannel,
        IHubContext<RadarHub> hubContext,
        ILogger<DspBroadcastBackgroundService> logger)
    {
        _broadcastChannel = broadcastChannel;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DSP viz pump started (10 Hz tap decoupled from the DSP stage).");

        try
        {
            await foreach (var dto in _broadcastChannel.Reader.ReadAllAsync(stoppingToken))
            {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                sendCts.CancelAfter(SendTimeout);

                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveDspFrame", dto, sendCts.Token);
                    Interlocked.Increment(ref _sent);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // graceful shutdown
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _sendFailures);
                    _logger.LogDebug(ex, "DSP viz send dropped a frame (slow/dead client?).");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }

        _logger.LogInformation(
            "DSP viz pump stopped. Frames sent: {Sent}, send failures: {Failures}.",
            Interlocked.Read(ref _sent), Interlocked.Read(ref _sendFailures));
    }
}
