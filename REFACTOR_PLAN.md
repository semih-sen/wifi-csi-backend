# CsiRadar.Backend — Refactor Plan

**Target framework:** .NET 10 · **Scope:** Real-time CSI signal-processing pipeline
**Status:** Draft for review · **Audience:** Backend / DSP / ML engineers

---

## 0. Purpose & Scope

This document defines a phased refactor of the `CsiRadar.Backend` Producer–Consumer
pipeline. The current architecture (MQTT ingest → bounded channel → windowed
filtering → ONNX inference → SignalR/MQTT broadcast) is structurally sound and
respects the manifest's layering and DI rules. However, an architectural review
surfaced four defect clusters that will degrade real-time behaviour or corrupt
signal output under production load.

This plan is **not** a rewrite. It is a sequenced set of changes that:

1. Removes garbage-collection pressure from the hot path (zero-allocation mandate).
2. Corrects a stateful-filter correctness bug that silently distorts the signal.
3. Decouples broadcast transport from the inference pipeline (backpressure isolation).
4. Locks down the ONNX I/O contract before inference is wired.

The ordering matters: two architectural decisions gate everything else. Writing
code before those decisions are locked will produce work that is later discarded.

---

## 1. Defect Inventory (baseline)

The following defects were identified in the current codebase and motivate this plan.

### BUG-1 — Wrong element type for CSI payload (`int[]` instead of `sbyte[]`)

**Location:** `CsiPayloadDto.Data`, `CsiData.RawCsiData`
**Severity:** High (memory) / Medium (correctness)

ESP-IDF's `wifi_csi_info_t` callback emits CSI as `int8_t` (signed, −128..127),
interleaved imag/real per subcarrier. Both the DTO and the domain entity model
this as `int[]`, inflating each 1-byte sample to 4 bytes.

- **4× memory waste.** A 128-element payload occupies 512 B instead of 128 B.
  At 100 Hz with a 1024-frame `DropOldest` backlog this is ~0.5 MB of avoidable
  retained heap plus continuous large-object churn.
- The amplitude math promotes these values to `double` unnecessarily. The maximum
  of `imag² + real²` is `2 × 127² = 32 258`, which fits in a 32-bit `int`. The
  `double` promotion is wasted FPU work on the hottest math path.

### BUG-2 — `ProcessWindow` allocates heavily per cycle

**Location:** `SignalFilteringService.ProcessWindow`
**Severity:** High (violates the zero-allocation mandate directly)

Each window invocation allocates, for a 64-subcarrier × 100-sample window:

- 1 jagged outer array (`double[subcarrierCount][]`)
- 64 inner row arrays (`new double[windowSize]`)
- 64 filter-output arrays (`OnlineFilter.ProcessSamples` returns a fresh array)
- 1 flattened `float[]` result

That is roughly **130 discrete heap allocations per window**, each feeding Gen0.
Under Server GC this still triggers periodic Gen0 collections; every collection is
jitter in the visualization stream and latency in the automation path. This is the
single largest contributor to the GC pressure the manifest explicitly forbids.

### BUG-3 — SignalR broadcast blocks the consumer loop

**Location:** `CsiProcessingBackgroundService.ExecuteAsync` →
`await _broadcastService.BroadcastCsiDataAsync(...)`
**Severity:** High (data-loss under realistic conditions)

The consumer loop `await`s `Clients.All.SendAsync`. SignalR back-pressures on the
slowest client. A single slow mobile client (weak Wi-Fi) stalls the await, which
stalls the consumer, which lets the bounded channel fill, which triggers
`DropOldest` — i.e. **one bad connection causes frame loss across the entire
pipeline**, including the inference-critical path.

### BUG-4 — Stateful IIR filter misused under overlapping windows

**Location:** `SignalFilteringService` (singleton `OnlineFilter[]`) vs.
`ProcessingOptions.SlideStep`
**Severity:** High (silent signal corruption)

`OnlineFilter` is an IIR filter: it carries z⁻¹ state across calls. The filters
are singletons fed window-by-window. With `SlideStep < WindowSize` (the
`ProcessingOptions` default is `SlideStep = 50`), overlapping samples are pushed
through the filter **twice**, so on the second pass the filter's state already
"contains" those samples. The frequency/phase response is corrupted and the
"latest sample" plucked for the graph
(`filteredSignal[offset + WindowSize-1]`) is a multiply-filtered value.

> Note: `appsettings.json` currently sets `SlideStep = 100` (non-overlapping),
> which masks the bug. The defect is latent and activates the moment overlapping
> windows are enabled.

### Secondary findings

- **Reference sharing:** `CsiData.RawCsiData = dto.Data` shares the DTO's array.
  Since a fresh DTO is allocated per message, there is no pooling benefit — only a
  reference handoff. True zero-alloc parsing would bypass the DTO entirely.
- **`SubcarrierPhases` never populated.** The entity exposes phase but the pipeline
  never computes `atan2`. Phase is high-value for gait recognition and should be
  planned for, not silently dropped.
- **`UpdateBaseline` is dead code.** No caller exists. The manifest specifies a
  nightly-updated dynamic baseline; the trigger mechanism is missing.
- **Debounce state machine unimplemented.** `ConfidenceThreshold = 0.7` and
  `ConsecutiveCountForAutomation = 3` exist in config and are referenced in a TODO,
  but the consecutive-window confirmation logic does not exist. The referenced
  `confidenceThreshold` local is not even declared.
- **`EnsureFiltersInitialized` is not thread-safe.** Safe today only because the
  consumer is single-threaded; it races the moment a calibration trigger calls
  `UpdateBaseline` concurrently.
- **Diagnostic counters are computed but never surfaced** (`MessagesReceived/
  Dropped/Errors`, `_windowsProcessed`). `Dropped` in particular is the early
  warning signal for backpressure and must be observable.

---

## 2. Phase 0 — Architectural Decisions (no code)

These three decisions must be ratified before any implementation begins. Phases 1–5
are derived from them.

### D1 — Adopt stream-based (per-frame) filtering

**Decision:** Move the IIR filter upstream. Demodulate + baseline-subtract + filter
**each frame exactly once** as it enters the consumer, and write the filtered frame
into a flat ring buffer. The "window" becomes a *view* over the ring buffer — a
snapshot handed to ML/broadcast — not a re-filtering unit.

**Rationale:**

- Overlapping windows become free (the view slides; no re-filtering).
- IIR state is never corrupted; no per-window transient/settling artifacts.
- The `latestAmplitudes[offset + WindowSize-1]` extraction trick disappears — the
  most-recent filtered frame is already in hand.

**Interface impact:** `ISignalProcessor.ProcessWindow(ReadOnlySpan<CsiData>)` is
retired. It is replaced by a per-frame entry point (filter one frame, return the
filtered frame) plus a separate windowing component that snapshots the ring buffer.

> This is the highest-leverage decision in the plan because it reshapes the data
> flow. Resolves BUG-4 and removes the structural cause of BUG-2.

### D2 — Memory ownership model (the `ArrayPool` × `DropOldest` footgun)

**Decision:** Pool **only consumer-side buffers** with deterministic, bounded
lifecycles. Do **not** pool per-frame arrays that travel through the channel.

**Rationale — the footgun:** `BoundedChannelFullMode.DropOldest` drops items
**without a callback**. If a dropped frame's array was rented from `ArrayPool<T>`,
it can never be `Return`ed → **pool leak**. Under sustained 100 Hz load the pool
inflates, eventually falls back to fresh allocation, and silently reintroduces the
exact GC pressure pooling was meant to remove — with a hard-to-diagnose signature.

**Ownership rules:**

| Buffer | Travels through channel? | Can be dropped? | Strategy |
|---|---|---|---|
| Per-frame raw CSI (`sbyte[]`) | Yes | Yes (DropOldest) | **Do not pool.** Right-size and let GC reclaim (1-byte arrays, cheap). |
| Window matrix / filter scratch (`double[]`, `float[]`) | No | No | **Pool** via `ArrayPool<T>.Shared`, guaranteed `Return` in `try/finally`. |

The 80/20: leave the small per-frame arrays to GC; pool the large consumer buffers,
which are the actual GC killers from BUG-2.

### D3 — Lock the ONNX I/O tensor contract with the Python team

**Decision:** Pin the input tensor layout, dtype, and axis order with the model
authors before any inference wiring. Do not touch Phase 4 until this is signed off.

**Open questions to resolve:**

- Axis order: `[subcarrier, time]` vs `[time, subcarrier]`. Current code emits
  subcarrier-major: `[sc0_t0 … sc0_tN, sc1_t0 …]`.
- Channels-first vs channels-last for the 1D-CNN.
- For LSTM, the model likely expects `(timesteps, features)`, which requires a
  transpose of the current layout.
- Input dtype and any normalization the model assumes was applied at training time.

**Rationale:** A layout mismatch does not throw — it silently feeds a transposed
tensor, the model emits garbage predictions, and the failure point is nearly
impossible to localize after the fact.

---

## 3. Phase 1 — Data Model & Flow Reshape

**Gated on:** D1, D2.
**Closes:** BUG-1, BUG-4. Removes the structural cause of BUG-2.

1. **Type correction.** `CsiPayloadDto.Data` and `CsiData.RawCsiData` → `sbyte[]`.
   Keep the I/Q amplitude math in `int` arithmetic (`2 × 127²` fits `int`); drop the
   `double` promotion in the inner loop.

2. **Lift filtering upstream.** Demodulation + baseline subtraction + IIR filtering
   become per-frame, executed the instant a frame is dequeued. The filtered output
   is written into a **flat ring buffer** — `float[subcarrierCount * capacity]`,
   not jagged — for cache locality.

3. **Separate windowing.** When the ring buffer holds a full window, a snapshot
   view is produced for ML/broadcast. The slide becomes a view-offset update; the
   `Array.Copy` frame-shifting in the consumer is deleted.

**Exit criteria:** Overlapping windows produce correct filter output (verified
against a reference signal); per-frame raw memory is `sbyte`-sized.

---

## 4. Phase 2 — Allocation Elimination

**Gated on:** D2, Phase 1.
**Closes:** remaining half of BUG-2.

1. Bind consumer scratch buffers to `ArrayPool<float>.Shared` /
   `ArrayPool<double>.Shared`; every rent is matched by a `Return` in `try/finally`.

2. Use the flat buffer from Phase 1 and apply the filter **in place**.
   `MathNet.Filtering.OnlineFilter.ProcessSamples` returns a new array, so we either
   wrap it with an in-place adapter or implement the filter ourselves as a
   **4th-order Butterworth biquad cascade** applied in place. The cascade is the
   only clean way to escape the per-call allocation.

**Exit criteria:** Steady-state per-window heap allocation ≈ 0; Server GC Gen0
collection frequency measurably drops under a 100 Hz load test.

> ⚠️ **Side effect:** If we hand-roll the biquad, we own the coefficients. They must
> be validated to match `OnlineFilter.CreateLowpass`'s frequency response with a
> dedicated test, otherwise the response silently drifts.

---

## 5. Phase 3 — Broadcast Isolation

**Gated on:** independent of Phase 1/2 (can run in parallel).
**Closes:** BUG-3.

1. Introduce a dedicated `Channel<CsiSignalDto>` with depth 1–2 and `DropOldest`
   (graph data is loss-tolerant).
2. A separate background pump drains this channel to SignalR. The inference path is
   fully decoupled from broadcast transport.

**Design intent:** Missing a graph frame is irrelevant; missing an inference frame
is not. The two must not share a fate.

> ⚠️ **Side effect:** `Clients.All.SendAsync` still blocks on a slow client inside
> the pump. Bound it with a send timeout or per-client buffer limit, or the pump
> task stalls on a single dead connection.

---

## 6. Phase 4 — Inference & Automation State Machine

**Gated on:** D3 **and** delivery of the `.onnx` file.

1. Implement `OnnxModelEvaluator.Predict` via `PredictionEnginePool`
   (`Microsoft.Extensions.ML`). Enable the commented `AddPredictionEnginePool`
   block in `Program.cs`.

2. **Build the debounce state machine (new component).** Implement the
   "N consecutive windows above confidence threshold" confirmation
   (`ConsecutiveCountForAutomation`, `ConfidenceThreshold`) that is referenced in
   config but does not exist in code. This is a distinct layer from the
   `_lastAutomationStatus` deduplication in `BroadcastService`:

   - Dedup answers "did the status change?"
   - Debounce answers "has the *same* prediction held for N windows above threshold?"

   Only after debounce confirmation does `TriggerAutomationAsync` fire.

**Exit criteria:** End-to-end inference produces correct labels against the pinned
contract; automation fires only after sustained, confident detection.

---

## 7. Phase 5 — Hardening & Observability

**Gated on:** Phases 1–4.

- **Thread-safe filter init.** Make `EnsureFiltersInitialized` safe (lock or
  immutable swap) before any calibration trigger can call `UpdateBaseline`
  concurrently with the consumer.
- **Wire the baseline updater.** Implement the nightly dynamic-baseline trigger
  (timer or empty-room detection) the manifest specifies; retire or activate the
  currently dead `UpdateBaseline`.
- **Surface diagnostics.** Expose `MessagesReceived/Enqueued/Dropped/Errors` and
  `_windowsProcessed` via a health endpoint or periodic structured log. Treat
  `Dropped` as the primary backpressure alarm.
- **Graceful-shutdown audit.** Guarantee the writer is completed and the reader
  drains the channel remainder on shutdown, per the manifest's `CancellationToken`
  requirement.
- **Phase (optional, deferred).** Decide whether to populate `SubcarrierPhases`
  (`atan2`) for downstream gait work; if yes, fold it into the per-frame path
  established in Phase 1.

---

## 8. Sequencing & Dependencies

```
D1, D2, D3  (decisions)
    │
    ├── D1, D2 ──► PHASE 1 (flow reshape) ──► PHASE 2 (allocation)
    │                                              │
    │                                              ├──► PHASE 4 (inference)*  ──► PHASE 5 (hardening)
    │                                              │
    └────────────────► PHASE 3 (broadcast)  [parallel with Phase 1/2]
                                                   ▲
            D3 + .onnx file ──────────────────────┘  (*also gates Phase 4)
```

- **Critical path:** decisions → Phase 1 → Phase 2.
- **Phase 3** is largely independent of the flow reshape and can proceed in parallel.
- **Phase 4** is gated on both D3 and the model file; it is intentionally last
  (the manifest already marks inference as "Step 4").

---

## 9. Risk Register

| Risk | Phase | Mitigation |
|---|---|---|
| `ArrayPool` leak via `DropOldest` | D2 / 2 | Never pool channel-borne arrays; pool only deterministic consumer buffers. |
| Hand-rolled biquad drifts from reference response | 2 | Coefficient validation test against `CreateLowpass`. |
| Broadcast pump stalls on dead client | 3 | Send timeout / per-client buffer bound. |
| Silent ONNX tensor-layout mismatch | D3 / 4 | Pin contract with Python team before wiring; assert shape at load. |
| Filter-init race on calibration | 5 | Make init thread-safe before wiring the nightly trigger. |
| MQTTnet v5 / ML.NET 5.0.0 vs .NET 10 API drift | all | Compile-verify factory APIs and package compatibility early. |

---

## 10. Definition of Done

- Overlapping windows produce correct, single-pass-filtered output.
- Per-frame raw memory is `sbyte`-sized; steady-state per-window heap alloc ≈ 0.
- A slow SignalR client cannot induce frame drops in the inference path.
- ONNX inference matches the pinned contract; automation fires only on confirmed,
  sustained detection.
- Diagnostics (especially `Dropped`) are observable; shutdown drains the channel
  cleanly; no dead code remains in the filtering service.
