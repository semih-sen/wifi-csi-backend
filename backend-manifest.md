Wi-Fi CSI Radar - C# Backend (.NET 10) Teknik Mimari ve Geliştirme Rehberi

Sayın Code Agent (Kod Asistanı), bu doküman Wi-Fi CSI verilerini işleyecek yüksek performanslı arka uç (backend) servisinin teknik manifestosudur. Lütfen kod üretirken aşağıdaki mimari kurallara, tasarım desenlerine ve kütüphane seçimlerine harfiyen uyunuz.

1. Proje Türü ve Genel Mimari

Proje Tipi: .NET 10 Worker Service (Arka plan servisi). Sistem 7/24 çalışan bir daemon/konteyner olacağı için standart web API yerine Worker Service mimarisi tercih edilmelidir.

Tasarım Deseni: Clean Architecture (veya modüler yapı) ve Producer-Consumer (Üretici-Tüketici) deseni.

Bağımlılık Enjeksiyonu (DI): Tüm servisler IServiceCollection üzerinden arayüzler (interface) ile sisteme enjekte edilmelidir (Loose coupling).

2. Temel Katmanlar ve İş Parçacıkları (Threads)

Saniyede 100 Hz (100 paket/saniye) hızında gelen verinin sistemi boğmaması (GC - Garbage Collection spike'ları yaratmaması) için veri toplama ve veri işleme süreçleri birbirinden tamamen izole edilmelidir.

2.1. Ingestion Layer (Veri Toplama - Producer)

Kütüphane: MQTTnet

Görev: Yerel ağdaki Mosquitto broker'a bağlanıp ESP32'den gelen (örn: sensor/csi/raw) topic'ini dinler.

Kural: Gelen byte dizileri (payload) veya JSON verisi hiçbir işleme tabi tutulmadan anında arabellek (buffer) yapısına aktarılmalıdır.

Performans: MQTT event handler içinde Task.Run veya uzun süren işlemler (I/O, matematik) KESİNLİKLE yapılmayacaktır.

2.2. Buffer Layer (Arabellek Yönetimi)

Kütüphane/Yapı: System.Threading.Channels

Görev: MQTT'den gelen veriyi (Producer) alır ve analiz katmanına (Consumer) aktarır. ConcurrentQueue yerine çok daha yüksek performanslı ve asenkron olan Channel<T> yapısı kullanılmalıdır.

Bellek Yönetimi: Aşırı nesne üretiminden kaçınmak için ArrayPool<T> veya Span<T> / Memory<T> kullanılarak Zero-Allocation (sıfır tahsisat) hedeflenmelidir.

2.3. Processing Layer (Sinyal İşleme - Consumer)

Kütüphane: Math.NET Numerics

Görev: Channel<T> üzerinden gelen verileri paketler halinde okur (Örn: 100 verilik pencereler - Sliding Window).

İşlemler:

Baseline Subtraction: Odanın statik referans verisi (kalibrasyon) anlık veriden çıkartılır.

Filtreleme: MathNet.Filtering namespace'i altındaki Low-pass (Alçak Geçiren) veya Butterworth filtreleri uygulanarak çevresel gürültüler (noise) temizlenir.

2.4. Inference Layer (Makine Öğrenmesi)

Kütüphane: Microsoft.ML ve Microsoft.ML.OnnxTransformer

Görev: Python tarafında eğitilmiş .onnx (1D-CNN / LSTM) modelini yükler.

İşlem: Filtrelenmiş sinyal penceresi (sliding window array), PredictionEngine (veya thread-safe olan PredictionEnginePool) kullanılarak modele beslenir.

Çıktı: Modelin tahmini (örn: EmptyRoom, Walking, LyingOnCouch) alınır.

2.5. Broadcasting & Automation Layer (Yayın ve Tetikleme)

Kütüphane (UI için): SignalR (Microsoft.AspNetCore.SignalR)

Kütüphane (Otomasyon için): MQTTnet (Publisher olarak)

Görevler:

İşlenmiş CSI grafikleri ve ONNX çıkarım sonuçları SignalR Hub üzerinden Web/Mobil ön yüzlere anlık (WebSocket) fırlatılır.

Eğer LyingOnCouch (Koltuğa yattı) durumu ardışık olarak (örn: 3 saniye boyunca) tespit edilirse, Home Assistant'ı tetiklemek için MQTT üzerinden home/radar/automation topic'ine payload gönderilir.

3. Örnek Dizin ve Sınıf Yapısı (Beklenen Mimarinin İskeleti)

CsiRadar.Backend/
│
├── Program.cs                      # Host Builder ve DI konfigürasyonları
│
├── Core/                           # İş mantığı ve arayüzler
│   ├── Entities/                   # CsiData, InferenceResult vs. modelleri
│   └── Interfaces/                 # IMqttClientService, ISignalProcessor, vb.
│
├── Infrastructure/                 # Dış dünya bağlantıları
│   ├── Mqtt/                       # MQTTnet implementasyonları (MqttListenerBackgroundService)
│   └── SignalR/                    # RadarHub.cs
│
├── Application/                    # Veri işleme merkezi
│   ├── Channels/                   # CsiDataChannelManager.cs (Producer/Consumer köprüsü)
│   ├── Processing/                 # SignalFilteringService.cs (Math.NET)
│   └── MachineLearning/            # OnnxModelEvaluator.cs (ML.NET PredictionEnginePool)
│
└── appsettings.json                # MQTT Broker IP, ONNX model dosya yolu vb.


4. Kritik Performans ve Stabilite Uyarıları (Kod Yazılırken Dikkat Edilecekler)

Thread-Safety: ML.NET PredictionEngine thread-safe değildir. Mutlaka PredictionEnginePool servisi kullanılmalıdır.

Backpressure (Geri Basınç): Eğer sinyal işleme hızı, MQTT'den gelen veriden yavaş kalırsa sistem şişer (Out of Memory). Channel<T> tanımlanırken BoundedChannelOptions kullanılmalı ve gerekiyorsa en eski veriler düşürülmelidir (DropOldest kuralı).

Graceful Shutdown: Worker Service durdurulurken MQTT bağlantısının düzgünce kesilmesi ve Channel kuyruklarının işlenip bitirilmesi için CancellationToken mimarisi tüm asenkron metotlarda eksiksiz kullanılmalıdır.