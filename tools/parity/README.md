# DSP train/serve parity (Seam A) — V2 Phase 2

The #1 project invariant is **train/serve identity**: every transform the model
consumes must be identical in serve (C#) and train (Python). This directory holds the
golden fixture that enforces it for the Phase 2 per-RX DSP layer.

## The three modalities (pinned contract)

Derived server-side from raw int8 I/Q (`[imag, real]` interleaved per subcarrier).
No normalization here — that is baked into the ONNX graph in a later phase.

| Modality | Definition | Serve (C#) | Train twin (Python) |
|---|---|---|---|
| **Amplitude** | `\|CSI\|ₖ = sqrt(imagₖ²+realₖ²)` (bit-exact) | `CsiDsp.Amplitude` | `dsp_twin.amplitude` |
| **Sanitized phase** | `atan2` → `unwrap` across subcarriers → least-squares linear detrend (removes STO slope + offset). Single-antenna sanitization, **not** the dual-antenna conjugate method (hardware can't do it). | `CsiDsp.SanitizedPhase` | `dsp_twin.sanitized_phase` |
| **Doppler** | STFT of each subcarrier's amplitude time series (Hann window, direct real DFT). Window/hop/bins pinned in `DspContract`. | `StftProcessor` | `dsp_twin.spectrogram` |

Pinned STFT geometry (`DspContract.cs` ⇄ `dsp_twin.py`): window **64**, hop **16**,
bins **33** (DC…Nyquist). Changing any of these is a contract break — bump both sides
and regenerate the golden.

## How the gate works

1. **Serve is source of truth.** The backend test `DspGoldenParityTests` computes each
   stage from a fixed *integer* input and dumps `dsp_golden.json` here (inputs +
   outputs). Integer inputs ⇒ no float-input divergence; only the transform is tested.
2. **Train reproduces it.** `wifi-csi-ml/tests/test_dsp_parity.py` loads the fixture,
   recomputes with the twin `wifi-csi-ml/src/dsp_twin.py`, and asserts reproduction.

"Bit-for-bit" is realized as **within float32 tolerance** (`atol/rtol = 1e-4`):
amplitude is exact; phase (`atan2`) and STFT (`cos/sin`) carry only sub-ULP cross-libm
noise. The tolerance is tight enough to catch any *structural* divergence (wrong
unwrap, wrong detrend, wrong window/hop/axis/bin count) — the failure modes that
silently feed a model garbage.

## Running it

```bash
# 1. Serve side: regenerate the golden + assert the C# maths
dotnet test --filter DspGolden          # writes tools/parity/dsp_golden.json

# 2. Copy the fresh golden into the ML repo's vendored slot
cp tools/parity/dsp_golden.json ../wifi-csi-ml/tests/golden/dsp_golden.json

# 3. Train side: assert the Python twin reproduces it
cd ../wifi-csi-ml && python tests/test_dsp_parity.py
```

The Python test also finds the golden via `$CSI_DSP_GOLDEN` or the sibling backend
path, so step 2 is optional for a local run but keeps the ML repo self-contained
(same vendoring convention as `read_csibin.py`).
