using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.MachineLearning;

/// <summary>
/// Loads the trained <c>model.onnx</c> and performs ONNX inference (Seam A), running
/// directly on onnxruntime's <see cref="InferenceSession"/>.
///
/// The exported graph already bakes in normalization and softmax (see wifi-csi-ml
/// ExportWrapper), so this evaluator does NO normalization — that is a standing
/// invariant; any normalization on the C# side is a regression. It reshapes the
/// consumer's flat window to the exact ONNX input tensor, runs the session, and maps
/// the probability vector to a label via <see cref="LabelMap"/>.
///
/// Inference is optional: when no model file exists at <c>ModelPath</c>, the
/// evaluator stays inactive (<see cref="IsReady"/> == false) and the pipeline runs
/// the graph/recording path only.
///
/// Threading: onnxruntime's <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>
/// is thread-safe, so concurrent <see cref="Predict"/> calls are fine without a lock.
/// </summary>
public sealed class OnnxModelEvaluator : IOnnxModelEvaluator, IDisposable
{
    private readonly ILogger<OnnxModelEvaluator> _logger;
    private readonly LabelMap? _labelMap;
    private readonly InferenceSession? _session;
    private readonly string _inputName = OnnxInput.TensorName;
    private readonly string _outputName = OnnxInput.OutputTensorName;

    public OnnxModelEvaluator(
        IOptions<InferenceOptions> options,
        ILogger<OnnxModelEvaluator> logger)
    {
        _logger = logger;
        var cfg = options.Value;

        if (!File.Exists(cfg.ModelPath))
        {
            _logger.LogWarning(
                "ONNX model '{ModelPath}' not found — inference disabled. The graph and " +
                "recording pipeline still run. Drop a model.onnx + labels.json into the " +
                "configured paths to enable classification.", cfg.ModelPath);
            return;
        }

        try
        {
            _labelMap = LabelMap.Load(cfg.LabelsPath);
            _session = new InferenceSession(cfg.ModelPath);

            // Resolve the actual tensor names (fall back to the contract defaults) so a
            // re-exported graph with different names still binds.
            _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? OnnxInput.TensorName;
            _outputName = _session.OutputMetadata.Keys.FirstOrDefault() ?? OnnxInput.OutputTensorName;

            // Probe once with a zero window so a label/model mismatch fails fast at
            // startup rather than mislabeling at runtime.
            float[] probe = RunSession(new float[OnnxInput.Length]);
            _labelMap.AssertMatchesModelOutput(probe.Length);

            _logger.LogInformation(
                "ONNX evaluator ready: model '{ModelPath}', {ClassCount} class(es) [{Classes}], " +
                "input '{Input}' length {Length}, output '{Output}'.",
                cfg.ModelPath, _labelMap.Count, string.Join(", ", _labelMap.Labels),
                _inputName, OnnxInput.Length, _outputName);
        }
        catch (Exception ex)
        {
            // A misconfigured model is a hard error worth surfacing loudly, but the
            // host should still come up so the graph/recording path is usable.
            _session?.Dispose();
            _session = null;
            _labelMap = null;
            _logger.LogError(ex,
                "Failed to initialize ONNX inference from '{ModelPath}' / '{LabelsPath}'. " +
                "Inference is disabled. Fix the model/label map and restart.",
                cfg.ModelPath, cfg.LabelsPath);
        }
    }

    /// <inheritdoc />
    public bool IsReady => _session is not null && _labelMap is not null;

    /// <inheritdoc />
    public IReadOnlyList<string> Labels => _labelMap?.Labels ?? [];

    /// <inheritdoc />
    public InferenceResult Predict(ReadOnlySpan<float> window)
    {
        if (!IsReady)
            throw new InvalidOperationException(
                "Predict called while the evaluator is not ready. Guard with IsReady.");

        if (window.Length < OnnxInput.Length)
            throw new ArgumentException(
                $"Window length ({window.Length}) is smaller than the ONNX input " +
                $"length ({OnnxInput.Length}). The consumer must feed a full window.",
                nameof(window));

        // Copy EXACTLY the ONNX input length out of the (possibly oversized, pooled)
        // span. This exact-length reshape is what keeps the tensor binding correct.
        var buffer = new float[OnnxInput.Length];
        window[..OnnxInput.Length].CopyTo(buffer);

        float[] probabilities = RunSession(buffer);
        return _labelMap!.Map(probabilities, DateTime.UtcNow.Ticks);
    }

    /// <summary>Runs the session on a 6400-float buffer and returns the output vector.</summary>
    private float[] RunSession(float[] buffer)
    {
        var tensor = new DenseTensor<float>(
            buffer, [1, OnnxInput.Subcarriers, OnnxInput.WindowSize]);

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session!.Run(inputs);

        foreach (var r in results)
        {
            if (r.Name == _outputName)
                return r.AsTensor<float>().ToArray();
        }

        // No name match (unexpected) — take the first output.
        return results.First().AsTensor<float>().ToArray();
    }

    public void Dispose() => _session?.Dispose();
}
