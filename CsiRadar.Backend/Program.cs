using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Application.MachineLearning;
using CsiRadar.Backend.Application.Processing;
using CsiRadar.Backend.Application.Recording;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Interfaces;
using CsiRadar.Backend.Infrastructure.Broadcasting;
using CsiRadar.Backend.Infrastructure.Mqtt;
using CsiRadar.Backend.Infrastructure.SignalR;

// ──────────────────────────────────────────────────────────────────────
// CsiRadar.Backend — Composition Root (Program.cs)
//
// This is the DI wiring for the entire Producer-Consumer pipeline:
//   MQTT (Producer) → Channel<CsiData> (Buffer) → Processing (Consumer)
//     → ONNX Inference → SignalR Broadcast + MQTT Automation
// ──────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// ──────────────────────────────────────────────────────
// 1. CONFIGURATION — Bind strongly-typed options
// ──────────────────────────────────────────────────────
builder.Services.Configure<MqttOptions>(
    builder.Configuration.GetSection(MqttOptions.SectionName));

builder.Services.Configure<IngestionOptions>(
    builder.Configuration.GetSection(IngestionOptions.SectionName));

builder.Services.Configure<ProcessingOptions>(
    builder.Configuration.GetSection(ProcessingOptions.SectionName));

builder.Services.Configure<InferenceOptions>(
    builder.Configuration.GetSection(InferenceOptions.SectionName));

// ──────────────────────────────────────────────────────
// 2. BUFFER LAYER — Bounded Channel (Producer-Consumer bridge)
// ──────────────────────────────────────────────────────
// Singleton: raw per-RX channel shared between the MQTT producer and the alignment stage.
// BoundedChannel with DropOldest backpressure (1024 frames ≈ 10 sec at 100 Hz).
builder.Services.AddSingleton<CsiDataChannelManager>();

// Aligned channel: paired [RX0, RX1] frames emitted by the alignment stage and read
// by the processing consumer (V2 Phase 1 multi-source ingestion).
builder.Services.AddSingleton<AlignedCsiChannelManager>();

// Ingestion diagnostics: lock-free per-RX / pairing counters surfaced at /health.
builder.Services.AddSingleton<IngestionDiagnostics>();

// Loss-tolerant broadcast channel (depth 2, DropOldest) that decouples SignalR
// transport from the inference-critical consumer — a slow client cannot stall ingestion.
builder.Services.AddSingleton<SignalBroadcastChannelManager>();

// Inference broadcast channel (depth 64, DropOldest): carries per-window inference
// results + confirmed statuses to the inference pump, off the consumer thread.
builder.Services.AddSingleton<InferenceBroadcastChannelManager>();

// ──────────────────────────────────────────────────────
// 3. CORE SERVICES — Interface-based DI (loose coupling)
// ──────────────────────────────────────────────────────

// MQTT Client: Handles connection to Mosquitto broker and message publishing.
// Registered as singleton — shared between the listener (producer) and broadcast (publisher).
builder.Services.AddSingleton<IMqttClientService, MqttClientService>();

// Signal Processor: per-frame demod + baseline subtraction + Butterworth IIR filtering
// (own zero-allocation biquad cascade; filters each frame exactly once).
builder.Services.AddSingleton<ICsiStreamProcessor, CsiStreamProcessor>();

// Calibration coordinator: thread-safe hand-off for empty-room baseline requests
// (the consumer captures the frames and calls UpdateBaseline on its own thread).
builder.Services.AddSingleton<CalibrationCoordinator>();

// ONNX Model Evaluator: loads model.onnx + labels.json and runs inference.
builder.Services.AddSingleton<IOnnxModelEvaluator, OnnxModelEvaluator>();

// Inference debounce state machine (consumer-thread-only): confirms a label after
// N consecutive windows above the confidence threshold before automation fires.
builder.Services.AddSingleton<InferenceDebouncer>();

// Broadcast Service: SignalR push + MQTT automation publishing.
builder.Services.AddSingleton<IBroadcastService, BroadcastService>();

// Recording Service (Hem arayüzü hem de concrete sınıfı kullanıldığı için böyle kaydediyoruz)
builder.Services.AddSingleton<RecordingService>();
builder.Services.AddSingleton<IRecordingService>(sp => sp.GetRequiredService<RecordingService>());

// ──────────────────────────────────────────────────────
// 4. ML.NET — ONNX inference (Seam A)
// ──────────────────────────────────────────────────────
// OnnxModelEvaluator (registered above) loads model.onnx + labels.json from the
// configured Inference paths, builds an ML.NET ONNX transformer, and validates the
// label map against the model's output dimension at startup. If the model file is
// absent it stays inactive and the pipeline runs the graph/recording path only.
// No PredictionEnginePool is needed: only the single consumer thread predicts, and
// the evaluator guards its PredictionEngine with a lock.

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.SetIsOriginAllowed(_ => true)
               .AllowAnyMethod()
               .AllowAnyHeader()
               .AllowCredentials();
    });
});


// ──────────────────────────────────────────────────────
// 5. SIGNALR — Real-time WebSocket communication
// ──────────────────────────────────────────────────────
builder.Services.AddSignalR(options =>
{
    // Enable detailed errors in development for easier debugging.
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();

    // Keep-alive interval to detect stale connections.
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);

    // Client timeout — if no message is received within this period,
    // the connection is considered dead.
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});

// ──────────────────────────────────────────────────────
// 6. HOSTED SERVICES — Background workers
// ──────────────────────────────────────────────────────

// Producer: MQTT listener that ingests binary CSI frames into the raw channel.
builder.Services.AddHostedService<MqttListenerBackgroundService>();

// Alignment: pairs raw per-RX frames by seqNo into the aligned channel (V2 Phase 1).
builder.Services.AddHostedService<CsiAlignmentBackgroundService>();

// Consumer: Processing pipeline that reads from the channel,
// filters signals, runs inference, and enqueues graph frames for broadcast.
builder.Services.AddHostedService<CsiProcessingBackgroundService>();

// Broadcast pump: drains the broadcast channel to SignalR off the consumer thread.
builder.Services.AddHostedService<BroadcastBackgroundService>();

// Inference pump: drains inference results to SignalR + triggers MQTT automation.
builder.Services.AddHostedService<InferenceBroadcastBackgroundService>();

// Calibration broadcaster: pushes baseline calibration start/finish to SignalR clients.
builder.Services.AddHostedService<CalibrationBroadcastBackgroundService>();

// Recording auto-stop forwarder: broadcasts RecordingState when a timed recording ends.
builder.Services.AddHostedService<RecordingAutoStopForwarder>();
// Disk yazıcı işçi (Kayıt kanalını boşaltan motor)
builder.Services.AddHostedService<RecordingBackgroundService>();

// ──────────────────────────────────────────────────────
// 7. LOGGING — Structured logging configuration
// ──────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.WebHost.UseUrls("http://0.0.0.0:5000");


// ──────────────────────────────────────────────────────
// BUILD & RUN
// ──────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors("AllowAll");

// Map the SignalR hub endpoint
app.MapHub<RadarHub>("/hubs/radar");

// ── Diagnostics: a single health/observability endpoint (Phase 4) ──
// Surfaces model + calibration + recorder state and the live processing config so
// drift and "why is nothing classifying?" are answerable without attaching a debugger.
app.MapGet("/health", (
    IOnnxModelEvaluator evaluator,
    ICsiStreamProcessor processor,
    IRecordingService recording,
    IngestionDiagnostics ingestion,
    Microsoft.Extensions.Options.IOptions<ProcessingOptions> processing) =>
{
    var p = processing.Value;
    return Results.Ok(new
    {
        status = "ok",
        contractVersion = CsiRadar.Backend.Infrastructure.SignalR.ContractInfo.Version,
        model = new { loaded = evaluator.IsReady, classes = evaluator.Labels },
        // V2 Phase 1 exit-criteria surface: per-RX frame rate, pairing rate, unpaired
        // drops, and the asserted binary protocol version.
        ingestion = ingestion.Snapshot(),
        processing = new
        {
            p.WindowSize,
            p.SlideStep,
            p.SamplingRateHz,
            subcarriers = processor.SubcarrierCount,
            calibrated = processor.IsCalibrated,
        },
        recording = recording.Status,
    });
});

app.Logger.LogInformation("CsiRadar Backend starting...");
app.Logger.LogInformation("SignalR Hub mapped at /hubs/radar");

await app.RunAsync();
