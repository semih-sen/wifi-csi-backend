using CsiRadar.Backend.Application.MachineLearning;
using CsiRadar.Backend.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Shared, single ONNX session for the Seam A tests. Mirrors production (the
/// evaluator is a singleton) and avoids creating/disposing several native
/// onnxruntime sessions in one process, which can crash the runner.
/// </summary>
public sealed class OnnxEvaluatorFixture : IDisposable
{
    public OnnxModelEvaluator Evaluator { get; }

    public OnnxEvaluatorFixture()
    {
        var models = ModelsDir();
        var options = Options.Create(new InferenceOptions
        {
            ModelPath = Path.Combine(models, "model.onnx"),
            LabelsPath = Path.Combine(models, "labels.json"),
        });
        Evaluator = new OnnxModelEvaluator(options, NullLogger<OnnxModelEvaluator>.Instance);
    }

    /// <summary>Locate the backend's Models/ dir by walking up from the test bin.</summary>
    private static string ModelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CsiRadar.Backend", "Models");
            if (File.Exists(Path.Combine(candidate, "model.onnx")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate CsiRadar.Backend/Models/model.onnx.");
    }

    public void Dispose() => Evaluator.Dispose();
}

/// <summary>
/// Seam A binding test: drives the REAL exported model.onnx + labels.json through
/// <see cref="OnnxModelEvaluator"/>. This is the test that catches the nastiest Seam A
/// failure modes — a mis-bound VectorType (wrong input length) or a label map that
/// disagrees with the model's output dimension.
/// </summary>
public class OnnxModelEvaluatorTests : IClassFixture<OnnxEvaluatorFixture>
{
    private readonly OnnxModelEvaluator _evaluator;

    public OnnxModelEvaluatorTests(OnnxEvaluatorFixture fixture) => _evaluator = fixture.Evaluator;

    [Fact]
    public void LoadsModelAndLabelMap()
    {
        Assert.True(_evaluator.IsReady);
        Assert.NotEmpty(_evaluator.Labels);
    }

    [Fact]
    public void Predict_BindsExactLengthAndReturnsProbabilityDistribution()
    {
        // A full window of arbitrary values; the graph normalizes + softmaxes itself.
        var window = new float[OnnxInput.Length];
        for (int i = 0; i < window.Length; i++)
            window[i] = MathF.Sin(i * 0.01f);

        var result = _evaluator.Predict(window);

        Assert.Contains(result.PredictedLabel, _evaluator.Labels);
        Assert.Equal(_evaluator.Labels.Count, result.Scores.Count);

        float sum = 0f;
        foreach (var p in result.Scores.Values)
            sum += p;
        Assert.InRange(sum, 0.999f, 1.001f);                 // softmax distribution
        Assert.Equal(result.Confidence, result.Scores[result.PredictedLabel], 3);
    }

    [Fact]
    public void Predict_AcceptsOversizedPooledBuffer()
    {
        // Simulate the consumer's pooled (oversized) buffer: only the first 6400 bind.
        var pooled = new float[OnnxInput.Length + 512];
        var result = _evaluator.Predict(pooled);

        Assert.Contains(result.PredictedLabel, _evaluator.Labels);
    }

    [Fact]
    public void Predict_RejectsShortWindow()
    {
        var tooShort = new float[OnnxInput.Length - 1];
        Assert.Throws<ArgumentException>(() => _evaluator.Predict(tooShort));
    }
}
