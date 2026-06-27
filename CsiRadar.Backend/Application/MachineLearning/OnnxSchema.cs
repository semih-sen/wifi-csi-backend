namespace CsiRadar.Backend.Application.MachineLearning;

// ─────────────────────────────────────────────────────────────────────────────
// ONNX tensor contract (Seam A).
//
// The exported graph (wifi-csi-ml/src/export_onnx.py) declares:
//   input  "input"  : float32 [batch, 64, 100]   RAW filtered window, subcarrier-major
//   output "output" : float32 [batch, num_classes] softmax probabilities
//
// The backend feeds one window at a time (batch = 1). The input vector MUST be
// exactly 64 × 100 = 6400 floats in subcarrier-major order — the same layout
// CsiRingBuffer.SnapshotSubcarrierMajor produces and read_csibin.window_stream
// reproduces. A length mismatch corrupts inference, so OnnxModelEvaluator validates
// the length before predicting.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Fixed shape of the ONNX model input. Not a data carrier — just the contract.</summary>
public static class OnnxInput
{
    /// <summary>Subcarrier count baked into the model input (the C dimension).</summary>
    public const int Subcarriers = 64;

    /// <summary>Window length baked into the model input (the T dimension).</summary>
    public const int WindowSize = 100;

    /// <summary>Exact flat input length the ONNX graph expects (64 × 100).</summary>
    public const int Length = Subcarriers * WindowSize;

    /// <summary>The ONNX graph's input tensor name.</summary>
    public const string TensorName = "input";

    /// <summary>The ONNX graph's output tensor name.</summary>
    public const string OutputTensorName = "output";
}
