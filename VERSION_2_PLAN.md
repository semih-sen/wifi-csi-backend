# VERSION_2_PLAN.md — Backend

**Scope:** `wifi-csi-backend` (C# / .NET 10) only. Firmware, ML training, and
frontend are referenced as boundaries but are **out of scope** here.

**Guiding principle:** *Lay the pipes before turning on the water.* Infrastructure
first. Every phase produces a testable, observable layer the next phase stands on.
No identity/automation logic is wired until the aligned dual-RX signal path and its
train/serve parity are proven.

**What V2 adds over V1:** two synchronized RX streams instead of one; a real DSP
layer (amplitude + sanitized phase + Doppler) instead of amplitude-only; a cascade
(cheap always-on activity model gates an expensive identity model); open-set gait
identity via metric-learning embeddings + an enrollment gallery; and a hierarchical
workflow state machine driving automation.

---

## 0. Architecture at a glance

```
TX (WROOM-32D)            2× RX (WROOM-32U)              Backend (this document)
  ping + seqNo  ──────►   raw I/Q + deviceId + seqNo  ──MQTT(binary)──► Ingestion
                                                                          │
   ┌──────────────────────────────────────────────────────────────────────┘
   ▼
 Alignment (by seqNo) → DSP per-RX (amplitude · sanitized phase · Doppler/STFT)
   → Fusion + windowing → ┌─ Activity model (amplitude-only, always on) ─┐
                          │        gates ▼ only while "walking"          │
                          └─ Identity model (multi-modal → L2 embedding) ─┘
   → Debounce (activity + identity) → Workflow FSM → automation + broadcast
                          ▲
              Enrollment sub-machine ──► Gallery (centroids, model-stamped)
```

**Layer ordering is the dependency order.** Identity needs fused DSP; fused DSP
needs alignment; alignment needs the binary multi-source contract. So we build
bottom-up and never skip ahead.

---

## 1. The non-negotiable invariants (carried from V1, hardened for V2)

These hold in every phase; violating any is a regression.

1. **Train/serve identity.** Every transform the model consumes — windowing, phase
   sanitization, STFT — must be bit-for-bit identical in C# (serve) and Python
   (train). V2's DSP chain is far larger than V1's, so this is the #1 risk, not a
   footnote. Each DSP stage ships with a golden cross-language parity test.
2. **Backend owns all heavy DSP.** RX devices publish **raw complex I/Q only**.
   Amplitude, phase, and Doppler are *derived* server-side. Nothing DSP-shaped runs
   on the ESP32 (keeps one source of truth, enables reflash-free iteration).
3. **Record raw, derive everything.** Recordings store raw I/Q (both RX, aligned),
   never pre-derived modalities. Amplitude/phase/Doppler are recomputed offline so a
   change to the DSP never invalidates the dataset.
4. **Single-occupancy assumption (explicit).** V2 assumes one person in the room.
   Two simultaneous walkers break both gait (mixed Doppler) and the single-state
   machine. Multi-target is V3. This assumption is written into the contract, not
   left implicit.
5. **Loss-tolerant vs must-deliver are separated.** Graph/inference frames may drop;
   confirmed status and automation triggers must not (the V1 confirmed-status /
   DropOldest criticality mismatch is fixed by design here).

---

## 2. Phase plan (infrastructure-first)

### Phase 1 — Multi-source ingestion contract (the pipes)

Goal: two RX streams arrive, are distinguishable, and are time-aligned **without
clock synchronization**.

- **Binary MQTT payload** replaces JSON. Two RX × raw I/Q × 100 Hz cannot ride JSON;
  binary is a V2 *precondition*, not an optimization. Define a versioned binary
  layout (magic + version + deviceId + seqNo + per-subcarrier int8 I/Q). The listener
  asserts the version and rejects mismatches loudly.
- **Sequence-number alignment.** TX embeds a monotonic `seqNo` in each ping; both RX
  echo the `seqNo` of the frame they measured. Backend aligns the two streams **by
  seqNo**, never by wall-clock (the two ESP32 clocks are independent; `esp_timer` is
  device-local). This is the linchpin of the whole multi-RX design.
- **`CsiPayloadDto` v2:** gains `deviceId` and `seqNo`. `RawCsiData` stays `sbyte[]`
  (raw int8 I/Q).
- **Alignment buffer:** keyed by `seqNo`, emits a paired `[RX0, RX1]` frame when both
  arrive within a small seq/time window; counts and surfaces unpaired/late frames
  (an RX dropping out is a first-class observable, not a silent gap).

**Exit criteria:** both RX visible end-to-end; a `/health`-style counter shows
per-RX frame rate, pairing rate, and unpaired drops; binary payload version asserted.

### Phase 2 — DSP layer per RX (the water treatment)

Goal: from raw I/Q, produce the three modalities, each with a parity test.

- **Amplitude:** `|CSI|` per subcarrier (already proven in V1; port forward).
- **Sanitized phase:** raw CSI phase is unusable (CFO/SFO/STO + PLL noise dominate
  motion). Apply single-antenna sanitization: unwrap across subcarriers + linear
  detrend (remove the STO slope and constant offset). Document explicitly that this
  is single-antenna sanitization, not the dual-antenna conjugate method (which our
  hardware can't do) — the phase is *relative/clean-enough*, not absolute.
- **Doppler:** STFT over each subcarrier's time series → a time-frequency map. This
  is a **windowed** transform: it needs a history buffer, which couples it to the
  windowing stage. Decide the STFT window/hop and bake those constants into the
  contract (train and serve must use identical parameters).
- **Golden parity harness (mandatory gate):** for each stage, the backend dumps a
  golden output from a known raw-I/Q input; Python must reproduce it bit-for-bit.
  Phase and STFT are where silent divergence hides — no stage is "done" without a
  green parity test.

**Exit criteria:** amplitude, sanitized phase, and Doppler each emitted per RX, each
with a passing cross-language parity test. No model yet — just clean, proven signal.

### Phase 3 — Fusion + windowing

Goal: assemble the aligned, multi-modal model input tensor; reproducible offline.

- Fuse the two aligned RX streams: per time step `[2 RX × (amplitude + phase) × 64]`
  plus the windowed Doppler map. Define the exact tensor layout/axis order as a
  pinned contract (the V1 lesson: a silent transpose feeds garbage).
- Windowing for gait is **longer and continuity-sensitive** than for presence (gait
  needs several full stride cycles, ≥2–3 s). A frame gap corrupts the Doppler map, so
  the alignment-drop counters from Phase 1 become a recording-integrity signal here.
- The window snapshot is the single artifact both the activity and identity models
  consume; its offline twin (Python) must produce an identical tensor.

**Exit criteria:** a fused multi-modal window is produced and its layout is parity-
tested against the Python windowing twin.

### Phase 4 — Cascade orchestration (still no identity)

Goal: the always-on cheap path runs; the expensive path is gated but stubbed.

- **Activity model (amplitude-only, always on):** empty / standing / walking /
  sitting. This is essentially the V1 problem (amplitude suffices), ported to the
  fused input. It consumes **no** phase/Doppler — efficiency win and capacity win.
- **Gate:** only when activity == walking does the orchestrator invoke the heavy
  identity path. While the room is empty/static, no phase/Doppler/identity compute
  runs. This is the cascade's whole point: cost scales with relevance.
- Identity evaluator is **wired but stubbed** in this phase (returns "not ready"),
  exactly as V1 staged ONNX before the model existed.

**Exit criteria:** activity classification runs on the fused stream; the identity
gate fires on "walking" and is observable; zero heavy compute when idle.

### Phase 5 — Identity: embedding evaluator + gallery (turning on the water)

Goal: open-set gait identity via metric learning.

- **Embedding evaluator:** loads the multi-head CNN+LSTM ONNX. The exported graph
  outputs an **L2-normalized embedding** (normalization baked into the graph — the V1
  "softmax-in-graph" principle, restated as "normalize-in-graph"). The backend never
  sees triplet loss or mining; it only extracts a normalized vector. Contract names
  pinned (`input` / `output`), batch axis dynamic, exact-length reshape with length
  assertion.
- **Gallery:** per person, a centroid **plus intra-class variance**, computed from
  enrollment embeddings. Stored as a versioned file (DB optional, not required).
  **Stamped with the model hash** — embeddings are only comparable within the space
  that produced them, so a retrain invalidates the gallery and forces re-enrollment.
  This is a new Backend↔ML contract seam: gallery ⇄ model-version must match, or the
  backend refuses to compare and flags it.
- **Centroid math, done right:** average the **already-normalized** embeddings, then
  **re-normalize** the centroid (so it stays on the unit sphere; otherwise Euclidean
  distance loses meaning). On normalized vectors, Euclidean and cosine are monotonic
  equivalents — Euclidean is fine *given normalization*.
- **Single-centroid caveat (validate, don't assume):** slippered/barefoot,
  fast/slow gaits may form *separate clusters*; one centroid can land in a meaningless
  middle. Start with one centroid per person but verify against real enrollment data
  whether the embeddings are unimodal; if not, move to multi-centroid or k-NN over
  raw gallery embeddings. Keep this swappable.
- **Per-person (not global) threshold:** different people produce tighter/looser
  clusters. Scale the accept threshold per person by *their own* intra-class variance,
  not one global number. Directly mitigates the "doesn't recognize me when I walk
  tired" false-negative.

**Exit criteria:** a walking window yields a normalized embedding; distance to gallery
centroids computed; identify/reject decision produced (thresholds provisional until
Phase 6 calibration).

### Phase 6 — Enrollment & calibration sub-system

Goal: a clean way to add people and to set the rejection threshold honestly.

- **`ENROLLING(person)` mode:** entered by a control command; **automation goes fully
  silent** while enrolling (no accidental TV trigger). Every confirmed gait window is
  accumulated as a raw embedding labeled to that person. On exit: compute centroid +
  variance, write to gallery.
- **Enrollment-quality guard:** if only one route/speed was captured, emit "needs more
  varied gait" feedback. Weak enrollment → weak recognition; never silently accept a
  thin enrollment.
- **`ENROLLING(unknown_pool)` mode:** guest walks go to a calibration pool, assigned
  to no one — the negatives.
- **⚠️ The critical calibration leak (the sharpest methodological trap):** the people
  used to *calibrate* the threshold and the people used to *evaluate* it must be
  **disjoint**. Open-set means "a person never seen in training/calibration." Calibrate
  the threshold on one negative group; measure EER on a **held-out** stranger group.
  Same-person calibrate-and-test yields an optimistic EER that collapses on a real
  stranger. This is the V1 session-level-split rule escalated to **person-independent
  evaluation** — non-negotiable.
- **Threshold via EER:** positive intra-class distances vs negative inter-class
  distances; the crossover (Equal Error Rate) is the sweet spot between false-reject
  (too tight → won't recognize you tired) and false-accept (too loose → a relative
  triggers your personalized automation). Surface the chosen threshold and the EER so
  it's auditable, not a magic constant.

**Exit criteria:** a person can be enrolled and rejected; the threshold is set from a
held-out negative group with a reported EER; gallery is model-stamped.

### Phase 7 — Workflow state machine + automation

Goal: turn confirmed events into the scenario, with safe transitions.

- **Three separated layers** (don't collapse them):
  1. *Perception* — per-window activity + identity (stateless, fast).
  2. *Confirmation* — **two** debouncers: `ActivityDebouncer` and `IdentityDebouncer`,
     with different parameters. Identity is higher-stakes → longer streak / more
     evidence; reject is also debounced (one bad window ≠ "stranger").
  3. *Workflow* — the FSM, consuming **confirmed** events only, never raw inference.
- **Identity is a transient opportunity — latch it.** Gait exists only while walking;
  once the person sits, you can no longer recognize them. So capture identity during
  the walk phase and **latch it for the presence session**, then trigger automation on
  the later sit/settle event using the latched identity. This latch is the heart of
  the FSM.
- **States / transitions (single-occupancy):**
  - `EMPTY` → presence + walking → `PRESENT_UNIDENTIFIED`
  - `PRESENT_UNIDENTIFIED` → gait identified (one of us) → `IDENTIFIED(person)`;
    rejected → `UNKNOWN_PRESENT` (different automation: notify, no personalized TV)
  - `IDENTIFIED(person)` → sit/lie detected → `SETTLED(person, surface)` →
    **fire automation** (personalized TV)
  - any state → presence lost for T seconds → `EMPTY` (+ undo automation, e.g. TV off)
  - parallel `ENROLLING(...)` overlay (automation suppressed)
- **Staleness/liveness is a first-class transition.** The V1 "status latches forever
  when the stream dies" bug is resolved here: a silent stream drives the machine back
  to `EMPTY` (safe default). Identity latch resets on this transition.
- **Guarded, timed transitions:** walking but no identity confirmed in N seconds →
  `UNKNOWN_PRESENT`; identified but no settle in M minutes → hold or revert.
- **Declarative engine:** model states/transitions/guards/actions as a transition
  table (or small DSL), with actions bound to automation hooks (MQTT → Home Assistant).
  A new scenario ("dim lights if Ayşe lies down after 22:00") is added as data, not by
  rewriting the engine.
- **Must-deliver automation:** the confirmed-status → automation path does **not** ride
  a loss-tolerant channel; it is delivered reliably (fixing the V1 criticality
  mismatch), and is idempotent so a re-assert can't double-fire.
- FSM state is in-memory; on restart, re-derive from `EMPTY` (safe default).

**Exit criteria:** the full scenario runs end-to-end — walk in → identified → wait →
settle → automation fires; stream death reverts to `EMPTY`; enrollment suppresses
automation; every transition is broadcast for the frontend.

---

## 3. Sequencing & dependencies

```
P1 ingestion (binary, seqNo align)
   └─► P2 DSP + parity ──► P3 fusion/windowing ──► P4 cascade (activity; identity stub)
                                                        └─► P5 identity + gallery
                                                               └─► P6 enrollment + EER
                                                                      └─► P7 FSM + automation
```

Strictly bottom-up — this *is* the "pipes before water" ordering. Identity (P5+) is
deliberately last because it depends on every layer beneath it; turning it on before
the DSP parity is green would debug three problems at once. The only parallelizable
slack: the binary-payload codec (P1) and the parity-harness scaffolding (P2) can be
scaffolded together early.

---

## 4. Contract seams (V2 deltas)

| Seam | V2 change |
|---|---|
| ESP32 ↔ Backend | Binary payload (versioned); `deviceId` + `seqNo`; two RX sources; raw I/Q only |
| Backend ↔ ML | Larger Seam A: DSP parity (phase + STFT), embedding contract (normalize-in-graph), **gallery ⇄ model-hash** match, threshold/EER artifact |
| Backend ↔ Frontend | FSM state, identified person, activity, **enrollment control + quality feedback**, threshold/EER visibility |

---

## 5. Risk register

| Risk | Phase | Mitigation |
|---|---|---|
| Train/serve DSP divergence (phase/STFT) | P2 | Golden cross-language parity test per stage; no stage ships without it |
| **Calibration leak** (same negatives calibrate & test) | P6 | Person-independent eval: disjoint calibrate vs held-out stranger groups |
| Gallery/model space mismatch after retrain | P5 | Model-hash stamp on gallery; refuse cross-version comparison, force re-enroll |
| Unnormalized centroid breaks distance metric | P5 | Normalize embeddings in-graph; average then re-normalize centroid |
| Single centroid can't represent multi-modal gait | P5 | Validate unimodality on real data; keep multi-centroid/k-NN swappable |
| Global threshold mis-fits per-person cluster tightness | P5/P6 | Per-person threshold scaled by intra-class variance |
| Frame gaps corrupt Doppler / recordings | P1/P3 | Pairing + unpaired-drop counters surfaced as integrity signal |
| Two-walker scenario silently breaks gait + FSM | all | Single-occupancy written into the contract; multi-target deferred to V3 |
| Weak enrollment → poor recognition | P6 | Enrollment-quality guard; reject thin/low-variance enrollments |
| Identity opportunity missed (recognized only mid-walk) | P7 | Latch identity for the presence session; reset on staleness |
| Automation lost on a loss-tolerant path | P7 | Must-deliver, idempotent automation trigger |

---

## 6. Definition of done

- Two RX stream over binary MQTT, aligned by `seqNo`, with per-RX rate and pairing
  observability.
- Amplitude, sanitized phase, and Doppler each emitted with a passing cross-language
  parity test; recordings store raw I/Q only.
- Fused multi-modal window matches its Python twin bit-for-bit.
- Cascade runs: always-on amplitude activity model gates an identity model that does
  zero work while idle.
- Identity produces a normalized embedding; gallery holds model-stamped centroids;
  per-person thresholds derived from a **held-out** negative group with a reported EER.
- Enrollment adds/rejects people with automation suppressed and quality feedback.
- The workflow FSM drives the full scenario, latches identity during the walk, fires
  must-deliver automation on settle, and reverts to `EMPTY` on stream staleness.
- Every confirmed transition is broadcast; FSM re-derives safely from `EMPTY` on restart.
```
