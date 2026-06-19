using CsiRadar.Backend.Application.Channels;
using CsiRadar.Backend.Application.MachineLearning;
using CsiRadar.Backend.Application.Processing;
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

builder.Services.Configure<ProcessingOptions>(
    builder.Configuration.GetSection(ProcessingOptions.SectionName));

builder.Services.Configure<InferenceOptions>(
    builder.Configuration.GetSection(InferenceOptions.SectionName));

// ──────────────────────────────────────────────────────
// 2. BUFFER LAYER — Bounded Channel (Producer-Consumer bridge)
// ──────────────────────────────────────────────────────
// Singleton: Single channel instance shared between MQTT producer and processing consumer.
// BoundedChannel with DropOldest backpressure (1024 frames ≈ 10 sec at 100 Hz).
builder.Services.AddSingleton<CsiDataChannelManager>();

// ──────────────────────────────────────────────────────
// 3. CORE SERVICES — Interface-based DI (loose coupling)
// ──────────────────────────────────────────────────────

// MQTT Client: Handles connection to Mosquitto broker and message publishing.
// Registered as singleton — shared between the listener (producer) and broadcast (publisher).
builder.Services.AddSingleton<IMqttClientService, MqttClientService>();

// Signal Processor: Baseline subtraction + Butterworth filtering (Math.NET).
builder.Services.AddSingleton<ISignalProcessor, SignalFilteringService>();

// ONNX Model Evaluator: Thread-safe inference via PredictionEnginePool.
builder.Services.AddSingleton<IOnnxModelEvaluator, OnnxModelEvaluator>();

// Broadcast Service: SignalR push + MQTT automation publishing.
builder.Services.AddSingleton<IBroadcastService, BroadcastService>();

// ──────────────────────────────────────────────────────
// 4. ML.NET — PredictionEnginePool for thread-safe ONNX inference
// ──────────────────────────────────────────────────────
// TODO: Uncomment and configure when the ONNX model file is available (Step 3).
// var inferenceOptions = builder.Configuration
//     .GetSection(InferenceOptions.SectionName)
//     .Get<InferenceOptions>()!;
//
// builder.Services.AddPredictionEnginePool<OnnxInput, OnnxOutput>()
//     .FromOnnxModel(
//         modelFilePath: inferenceOptions.ModelPath,
//         inputColumnName: "input",
//         outputColumnName: "output");

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

// Producer: MQTT listener that ingests CSI data into the channel.
builder.Services.AddHostedService<MqttListenerBackgroundService>();

// Consumer: Processing pipeline that reads from the channel,
// filters signals, runs inference, and broadcasts results.
builder.Services.AddHostedService<CsiProcessingBackgroundService>();

// ──────────────────────────────────────────────────────
// 7. LOGGING — Structured logging configuration
// ──────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// ──────────────────────────────────────────────────────
// BUILD & RUN
// ──────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors("AllowAll");

// Map the SignalR hub endpoint
app.MapHub<RadarHub>("/hubs/radar");

app.Logger.LogInformation("CsiRadar Backend starting...");
app.Logger.LogInformation("SignalR Hub mapped at /hubs/radar");

await app.RunAsync();
