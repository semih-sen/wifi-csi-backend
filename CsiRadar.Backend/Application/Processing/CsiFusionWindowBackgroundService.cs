using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Application.Processing.Windowing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Phase 3 fusion + windowing stage. Consumes the per-RX DSP output channel (the Phase 3
/// seam) and, via <see cref="WindowAssembler"/>, assembles the fused multi-modal model
/// input: the per-frame dense stack <c>[2 RX × (amplitude + phase) × 64]</c> plus the
/// windowed per-subcarrier Doppler map, over a gait-length window
/// (<see cref="WindowContract.WindowFrames"/> frames, slid every
/// <see cref="WindowContract.WindowSlide"/>). Output <see cref="Core.Entities.FusedWindow"/>
/// goes onto the fused-window channel — the Phase 4 seam. No model runs here (Phase 4+).
///
/// Continuity is enforced: a window straddling a Phase 1 alignment gap is rejected and
/// counted, never silently stitched (its Doppler map would be corrupt). Follows the
/// established BackgroundService + Channel stage pattern; single-threaded consumer, so the
/// assembler needs no locking.
/// </summary>
public sealed class CsiFusionWindowBackgroundService : BackgroundService
{
    private readonly DspOutputChannelManager _input;
    private readonly FusedWindowChannelManager _output;
    private readonly FusionDiagnostics _diagnostics;
    private readonly ILogger<CsiFusionWindowBackgroundService> _logger;

    private readonly WindowAssembler _assembler = new();

    public CsiFusionWindowBackgroundService(
        DspOutputChannelManager input,
        FusedWindowChannelManager output,
        FusionDiagnostics diagnostics,
        ILogger<CsiFusionWindowBackgroundService> logger)
    {
        _input = input;
        _output = output;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Fusion + windowing stage started. Window={Frames} frames (~{Seconds:0.00}s @100Hz), " +
            "slide={Slide}, dense={Dense}, doppler={Doppler}.",
            WindowContract.WindowFrames, WindowContract.WindowFrames / 100.0,
            WindowContract.WindowSlide, WindowContract.DenseShape, WindowContract.DopplerShape);

        try
        {
            await foreach (var aligned in _input.Reader.ReadAllAsync(stoppingToken))
            {
                _diagnostics.IncFramesConsumed();

                var result = _assembler.Accept(aligned);

                if (result.GapDetected)
                    _diagnostics.IncGapEvents();
                if (result.EmissionRejectedForGap)
                    _diagnostics.IncWindowsRejectedForGap();

                if (result.Emitted is { } window)
                {
                    _diagnostics.IncWindowsEmitted();
                    _diagnostics.SetLastWindow(window.StartSeqNo, window.EndSeqNo);

                    // Phase 4 seam (loss-tolerant DropOldest): never awaits a downstream model.
                    _output.Writer.TryWrite(window);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }

        _logger.LogInformation("Fusion + windowing stage stopped.");
    }
}
