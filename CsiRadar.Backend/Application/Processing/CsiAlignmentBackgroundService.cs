using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Infrastructure.Mqtt;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Stage between raw ingestion and processing: drains the raw per-RX channel,
/// pairs frames by seqNo via <see cref="CsiAlignmentBuffer"/>, and writes aligned
/// pairs onto <see cref="AlignedCsiChannelManager"/>.
///
/// Single reader of the raw channel, single writer of the aligned channel — the
/// whole multi-RX merge happens on this one thread, so the buffer's dictionary needs
/// no synchronization.
/// </summary>
public sealed class CsiAlignmentBackgroundService : BackgroundService
{
    private readonly CsiDataChannelManager _rawChannel;
    private readonly AlignedCsiChannelManager _alignedChannel;
    private readonly IngestionDiagnostics _diagnostics;
    private readonly IngestionOptions _options;
    private readonly ILogger<CsiAlignmentBackgroundService> _logger;

    public CsiAlignmentBackgroundService(
        CsiDataChannelManager rawChannel,
        AlignedCsiChannelManager alignedChannel,
        IngestionDiagnostics diagnostics,
        IOptions<IngestionOptions> options,
        ILogger<CsiAlignmentBackgroundService> logger)
    {
        _rawChannel = rawChannel;
        _alignedChannel = alignedChannel;
        _diagnostics = diagnostics;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sentinel -1 matches no real 48-bit MAC, so a misconfigured slot simply never
        // pairs (frames fall to UnknownDevice) instead of crashing the host.
        long rx0Mac = ResolveMac(_options.Rx0Mac, "Rx0Mac");
        long rx1Mac = ResolveMac(_options.Rx1Mac, "Rx1Mac");

        if (rx0Mac == rx1Mac)
            _logger.LogError(
                "Ingestion Rx0Mac and Rx1Mac resolve to the same device — no frame can ever pair. " +
                "Set two distinct RX MACs in the Ingestion config.");

        long timeoutTicks = _options.PairingTimeoutMs > 0
            ? _options.PairingTimeoutMs * TimeSpan.TicksPerMillisecond
            : 0;

        var buffer = new CsiAlignmentBuffer(
            rx0Mac, rx1Mac, timeoutTicks, _options.MaxPending, _diagnostics);

        _logger.LogInformation(
            "CSI alignment started. RX0={Rx0}, RX1={Rx1}, pairingWindow={WindowMs}ms, maxPending={MaxPending}.",
            CsiBinaryProtocol.FormatMac(rx0Mac), CsiBinaryProtocol.FormatMac(rx1Mac),
            _options.PairingTimeoutMs, _options.MaxPending);

        try
        {
            await foreach (var frame in _rawChannel.Reader.ReadAllAsync(stoppingToken))
            {
                var paired = buffer.Accept(frame, DateTime.UtcNow.Ticks);
                if (paired is not null)
                    _alignedChannel.Writer.TryWrite(paired); // loss-tolerant (DropOldest)
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }

        _logger.LogInformation("CSI alignment stopped.");
    }

    private long ResolveMac(string configured, string field)
    {
        if (CsiBinaryProtocol.TryParseMac(configured, out long mac))
            return mac;

        _logger.LogError(
            "Ingestion {Field} ('{Value}') is not a valid MAC — that RX slot is disabled and will not pair.",
            field, configured);
        return -1;
    }
}
