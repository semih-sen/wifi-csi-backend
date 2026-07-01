using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Application.Processing.Dsp;
using CsiRadar.Backend.Core.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Phase 2 per-RX DSP stage. Consumes aligned pairs (fanned out from the alignment
/// service), and for <b>each RX independently</b> derives the three modalities from
/// raw int8 I/Q:
///   • amplitude  — <c>|CSI|</c> per subcarrier (per frame);
///   • sanitized phase — unwrap + linear detrend (per frame);
///   • Doppler — a streaming STFT column emitted every <see cref="DspContract.StftHopSize"/>
///     samples over each subcarrier's amplitude history.
///
/// RX0 and RX1 are processed side by side but never fused (fusion is Phase 3). Output
/// <see cref="AlignedDspFrame"/> goes onto the loss-tolerant DSP output channel — the
/// Phase 3 seam. Follows the established BackgroundService + Channel stage pattern.
///
/// Single-threaded consumer, so the per-RX STFT history rings need no locking.
/// </summary>
public sealed class CsiDspBackgroundService : BackgroundService
{
    private readonly DspInputChannelManager _input;
    private readonly DspOutputChannelManager _output;
    private readonly DspDiagnostics _diagnostics;
    private readonly ILogger<CsiDspBackgroundService> _logger;

    private readonly RxStftHistory _rx0History = new();
    private readonly RxStftHistory _rx1History = new();

    public CsiDspBackgroundService(
        DspInputChannelManager input,
        DspOutputChannelManager output,
        DspDiagnostics diagnostics,
        ILogger<CsiDspBackgroundService> logger)
    {
        _input = input;
        _output = output;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Per-RX DSP stage started. Modalities: amplitude + sanitized phase (per frame), " +
            "Doppler STFT (window={Window}, hop={Hop}, bins={Bins}).",
            DspContract.StftWindowSize, DspContract.StftHopSize, DspContract.StftBins);

        try
        {
            await foreach (var aligned in _input.Reader.ReadAllAsync(stoppingToken))
            {
                RxDsp rx0 = Derive(aligned.Rx0, _rx0History, isRx0: true);
                RxDsp rx1 = Derive(aligned.Rx1, _rx1History, isRx0: false);

                _diagnostics.IncFramesProcessed();

                _output.Writer.TryWrite(new AlignedDspFrame
                {
                    SeqNo = aligned.SeqNo,
                    Rx0 = rx0,
                    Rx1 = rx1,
                    Source = aligned,
                });
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }

        _logger.LogInformation("Per-RX DSP stage stopped.");
    }

    /// <summary>
    /// Derives one RX's modalities. Amplitude and phase are pure per-frame transforms;
    /// Doppler is fed the fresh amplitude and returns a column only on a hop boundary.
    /// </summary>
    private RxDsp Derive(CsiData frame, RxStftHistory history, bool isRx0)
    {
        int rawLen = Math.Min(frame.RawDataLength, frame.RawCsiData.Length);
        int n = rawLen / 2;
        ReadOnlySpan<sbyte> raw = frame.RawCsiData.AsSpan(0, rawLen);

        var amplitude = new float[n];
        var phase = new float[n];
        CsiDsp.Amplitude(raw, amplitude);
        CsiDsp.SanitizedPhase(raw, phase);

        _diagnostics.SetLastSubcarriers(n);
        if (n != DspContract.Subcarriers)
            _diagnostics.IncSubcarrierMismatch();

        float[,]? doppler = history.Push(amplitude);
        if (doppler is not null)
        {
            if (isRx0) _diagnostics.IncRx0DopplerColumns();
            else _diagnostics.IncRx1DopplerColumns();
        }

        return new RxDsp
        {
            DeviceMac = frame.DeviceMac,
            Amplitude = amplitude,
            SanitizedPhase = phase,
            DopplerColumn = doppler,
        };
    }

    /// <summary>
    /// Per-RX rolling amplitude history that emits a streaming STFT column
    /// <c>[subcarrier, StftBins]</c> every <see cref="DspContract.StftHopSize"/>
    /// samples, once at least one full <see cref="DspContract.StftWindowSize"/> window
    /// has accumulated. Column <c>f</c> becomes available after
    /// <c>StftWindowSize + f·StftHopSize</c> samples — identical to
    /// <see cref="StftProcessor.Spectrogram"/>'s frame starts, so runtime and the
    /// golden harness agree.
    /// </summary>
    private sealed class RxStftHistory
    {
        private int _subcarriers;
        private double[] _ring = [];   // [subcarrier * W + timeSlot], ring over time
        private int _writePos;         // next slot to write (== oldest once full)
        private long _count;           // total samples seen
        private int _hopCountdown;

        public float[,]? Push(ReadOnlySpan<float> amplitude)
        {
            int w = DspContract.StftWindowSize;
            int sc = amplitude.Length;

            if (sc == 0)
                return null;
            if (_subcarriers != sc)
                Reset(sc);

            // Write this frame's amplitudes into the current time slot.
            for (int k = 0; k < sc; k++)
                _ring[k * w + _writePos] = amplitude[k];
            _writePos = (_writePos + 1) % w;
            _count++;

            if (_count < w)
                return null;

            bool emit;
            if (_count == w)
            {
                emit = true;                 // first full window
                _hopCountdown = DspContract.StftHopSize;
            }
            else if (--_hopCountdown <= 0)
            {
                emit = true;
                _hopCountdown = DspContract.StftHopSize;
            }
            else
            {
                emit = false;
            }

            return emit ? ComputeColumn(sc, w) : null;
        }

        private float[,] ComputeColumn(int sc, int w)
        {
            int bins = DspContract.StftBins;
            var column = new float[sc, bins];

            Span<double> windowSamples = stackalloc double[DspContract.StftWindowSize];
            Span<float> binScratch = stackalloc float[DspContract.StftBins];

            for (int k = 0; k < sc; k++)
            {
                // Gather this subcarrier's window in time order (oldest → newest).
                int baseIdx = k * w;
                for (int t = 0; t < w; t++)
                    windowSamples[t] = _ring[baseIdx + (_writePos + t) % w];

                StftProcessor.MagnitudeColumn(windowSamples, binScratch);
                for (int m = 0; m < bins; m++)
                    column[k, m] = binScratch[m];
            }
            return column;
        }

        private void Reset(int subcarriers)
        {
            _subcarriers = subcarriers;
            _ring = new double[subcarriers * DspContract.StftWindowSize];
            _writePos = 0;
            _count = 0;
            _hopCountdown = 0;
        }
    }
}
