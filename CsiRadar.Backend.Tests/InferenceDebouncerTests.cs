using CsiRadar.Backend.Application.MachineLearning;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Unit tests for <see cref="InferenceDebouncer"/>: a label is confirmed only after
/// it holds for the required consecutive count above the confidence threshold, and
/// only once per transition.
/// </summary>
public class InferenceDebouncerTests
{
    private const string IdleLabel = "EmptyRoom";

    private static InferenceDebouncer Build(int consecutive = 3, float threshold = 0.7f) =>
        new(Options.Create(new InferenceOptions
        {
            ConsecutiveCountForAutomation = consecutive,
            ConfidenceThreshold = threshold,
            DefaultIdleLabel = IdleLabel,
        }));

    private static InferenceResult Result(string label, float confidence) =>
        new() { PredictedLabel = label, Confidence = confidence };

    [Fact]
    public void ConfirmsOnlyAfterConsecutiveCount()
    {
        var d = Build(consecutive: 3);
        Assert.Null(d.Push(Result("Walking", 0.9f)));
        Assert.Null(d.Push(Result("Walking", 0.9f)));
        Assert.Equal("Walking", d.Push(Result("Walking", 0.9f)));
    }

    [Fact]
    public void DoesNotReconfirmTheSameLabel()
    {
        var d = Build(consecutive: 2);
        Assert.Null(d.Push(Result("Walking", 0.9f)));
        Assert.Equal("Walking", d.Push(Result("Walking", 0.9f)));
        // Further identical windows must NOT re-confirm.
        Assert.Null(d.Push(Result("Walking", 0.9f)));
    }

    [Fact]
    public void LowConfidenceCollapsesToIdleLabel()
    {
        var d = Build(consecutive: 2, threshold: 0.7f);
        Assert.Null(d.Push(Result("Walking", 0.5f)));
        Assert.Equal(IdleLabel, d.Push(Result("CatPassed", 0.4f)));
    }

    [Fact]
    public void StreakResetsWhenLabelChanges()
    {
        var d = Build(consecutive: 3);
        Assert.Null(d.Push(Result("Walking", 0.9f)));
        Assert.Null(d.Push(Result("Walking", 0.9f)));
        Assert.Null(d.Push(Result("EmptyRoom", 0.9f))); // resets streak
        Assert.Null(d.Push(Result("EmptyRoom", 0.9f)));
        Assert.Equal("EmptyRoom", d.Push(Result("EmptyRoom", 0.9f)));
    }
}
