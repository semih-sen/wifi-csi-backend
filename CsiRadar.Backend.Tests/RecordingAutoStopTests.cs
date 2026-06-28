using System.Diagnostics;
using CsiRadar.Backend.Application.Processing;
using CsiRadar.Backend.Application.Recording;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Server-side auto-stop: a recording started with a duration must stop on its own
/// (independent of any client), expose its deadline, and not auto-fire when started
/// open-ended or after a manual stop.
/// </summary>
public class RecordingAutoStopTests
{
    private static RecordingService NewService()
    {
        var processing = Options.Create(new ProcessingOptions());
        var processor = new CsiStreamProcessor(processing, NullLogger<CsiStreamProcessor>.Instance);
        return new RecordingService(
            Options.Create(new RecordingOptions()),
            processing,
            processor,
            NullLogger<RecordingService>.Instance);
    }

    private static async Task<bool> WaitUntilIdle(RecordingService svc, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (svc.IsRecording && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(20);
        return !svc.IsRecording;
    }

    [Fact]
    public async Task Start_WithDuration_AutoStopsAndRaisesEvent()
    {
        var svc = NewService();
        RecordingStatus? autoStopped = null;
        svc.AutoStopped += s => autoStopped = s;

        RecordingStatus status = svc.Start("Walking", "Alice", durationMs: 150);
        Assert.True(status.IsRecording);
        Assert.True(status.StopAtUnixMs > status.StartedAtUnixMs); // deadline exposed

        Assert.True(await WaitUntilIdle(svc, timeoutMs: 2000), "recording did not auto-stop");
        Assert.NotNull(autoStopped);
        Assert.False(autoStopped!.IsRecording);
        Assert.Equal(status.SessionId, autoStopped.SessionId);
    }

    [Fact]
    public void Start_OpenEnded_HasNoDeadlineAndDoesNotAutoStop()
    {
        var svc = NewService();
        bool fired = false;
        svc.AutoStopped += _ => fired = true;

        RecordingStatus status = svc.Start("EmptyRoom", "", durationMs: 0);
        Assert.Equal(0, status.StopAtUnixMs);
        Assert.True(svc.IsRecording);
        Assert.False(fired);
    }

    [Fact]
    public async Task ManualStop_CancelsTheAutoStop()
    {
        var svc = NewService();
        int fired = 0;
        svc.AutoStopped += _ => Interlocked.Increment(ref fired);

        svc.Start("Walking", "Bob", durationMs: 150);
        svc.Stop(); // manual stop before the deadline

        Assert.False(svc.IsRecording);
        await Task.Delay(400); // past the would-be auto-stop time
        Assert.Equal(0, fired); // the pending auto-stop must not fire
    }
}
