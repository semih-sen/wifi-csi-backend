Wi-Fi CSI (Kanal Durum Bilgisi) Radar Projesi - Mimari ve Geliştirme Rehberi

1. Proje Özeti ve Vizyon

Bu proje, iki adet mikrodenetleyici kullanılarak ortamdaki Wi-Fi sinyallerinin Kanal Durum Bilgisi (CSI - Channel State Information) verilerini analiz edip, kameraya veya geleneksel hareket sensörlerine ihtiyaç duymadan kapalı alanlarda yüksek hassasiyetli varlık tespiti, yürüyüş profili tanıma (gait recognition) ve spesifik hareket (koltuğa uzanma vb.) analizi yapmayı hedeflemektedir.

Nihai Amaç: 7/24 kesintisiz çalışan, MQTT üzerinden verileri aktaran, C# tabanlı bir backend ile veriyi filtreleyip ML.NET (ONNX) üzerinden makine öğrenmesi tahminleri yürüten ve bu veriyi SignalR/WebSocket ile mobil istemcilere canlı (sıfır gecikmeli) aktaran bir akıllı ev otomasyon radarı kurmaktır.

2. Donanım ve Fiziksel Kurulum Şeması

2.1. Donanım Gereksinimleri

Mikrodenetleyici: 2 Adet ESP32-WROOM-32U Geliştirme Kartı (Harici IPEX anten soketli).

Anten: 2 Adet 2.4 GHz 4dBi Esnek (Flex/PCB) Anten (Geniş elipsoid / Fresnel zonu hacmi için düşük kazanç tercih edilmiştir).

Güç Kaynağı: 5V / 2A stabil adaptör bağlantıları.

2.2. Fiziksel Topoloji (Oda Dizilimi)

Konumlandırma: Cihazlar odanın "Cisim Köşegeni" boyunca yerleştirilecektir.

Verici (Tx) Konumu: Kısa kenarın orta hattında, yere/süpürgeliğe yakın (veya eşya arkasına gizli) bir nokta.

Alıcı (Rx) Konumu: Odanın tam karşısındaki diğer kısa kenarın orta hattında, asma tavan veya korniş hizasında.

Amaç: Sinyalin (Fresnel Zonu) odayı tavandan tabana çaprazlama keserek, merkezdeki televizyon koltuğunu ve odanın yürüme hacmini maksimum seviyede yutmasını sağlamak. Sabitleme işlemlerinde milimetrik kaymaları önlemek için 3D baskı rijit kasalar kullanılacaktır.

3. Ağ, İletişim ve Veri Akışı Protokolleri

Sistem 3 temel iletişim katmanından oluşmaktadır:

Katman (ESP-NOW - Sensör Ağı): Verici (Tx), modeme bağlanmadan, Alıcı (Rx) cihazın MAC adresine saniyede 100 kez (100 Hz / 10 ms aralıklarla) ESP-NOW protokolü üzerinden şifresiz, düşük gecikmeli "beacon/ping" paketleri gönderir.

Katman (CSI Çıkarma ve Wi-Fi): Alıcı (Rx) cihaz, gelen bu paketlerdeki fiziksel bozulmayı donanımsal olarak okur (CSI datasını yakalar). Aynı ESP32, "Station Mode" ile evdeki 2.4 GHz Wi-Fi yönlendiricisine bağlıdır.

Katman (MQTT ve Sunucu): Rx cihazı, ayıkladığı ham CSI verilerini yerel ağdaki bir MQTT Broker'a (Eclipse Mosquitto) gönderir. Veri trafiği saniyede ~50-100 paket olacak şekilde optimize edilmiştir.

4. Yazılım Mimarisi (Backend ve Geliştirme Notları)

Sayın Code Agent (Kod Asistanı), arka uç (backend) mimarisini aşağıdaki kurallara ve teknolojilere göre inşa etmelisin:

4.1. Temel Teknolojiler

Dil ve Framework: C# / .NET 10 (veya en güncel LTS sürümü) - Asenkron yapı, yüksek I/O kapasitesi nedeniyle seçilmiştir.

MQTT İstemcisi: MQTTnet kütüphanesi kullanılacaktır.

Canlı Veri Akışı: Ön yüz (Mobil/Flutter veya Web) ile haberleşme için SignalR (WebSocket) kullanılacaktır.

Ortam: Sistem Linux tabanlı bir Thin Client üzerinde Docker konteyneri olarak 7/24 çalışacak şekilde tasarlanmalıdır.

4.2. Veri İşleme ve Filtreleme (Data Pipeline)

Gelen ham CSI verisi son derece gürültülü (noisy) olacaktır. C# tarafında şu adımlar uygulanmalıdır:

Veri Toplama (Ingestion): Mosquitto'dan gelen veriler asenkron olarak hafızaya (buffer) alınır.

Boş Oda Çıkarma (Baseline Subtraction): Ortamın boş olduğu anlardaki statik gürültü bir şablon olarak tutulmalı ve anlık veriden matematiksel olarak çıkartılmalıdır (Kalibrasyon kaymalarını tolere etmek için her gece güncellenen dinamik bir referans noktası).

Filtreleme: Math.NET Numerics gibi kütüphaneler kullanılarak sinyaldeki yüksek frekanslı parazitler (termal gürültü) Low-pass (Alçak geçiren) veya Butterworth filtreleri ile ütülenmelidir.

4.3. Makine Öğrenmesi (ML.NET ve ONNX Entegrasyonu)

Model Eğitimi: Modeller Python (TensorFlow/PyTorch) tarafında laboratuvar ortamında toplanmış etiketli verilerle eğitilecektir (1D-CNN veya LSTM).

Canlı Çıkarım (Inference): Eğitilen model .onnx formatında C# projesine dahil edilecektir.

ML.NET: Backend kodu, gelen temizlenmiş (filtrelenmiş) veriyi anlık olarak ONNX modeline beslemeli ve tahmin sonuçlarını (Örn: "Yürüyüş", "Koltuğa Yattı", "Boş Oda", "Kedi Geçti") çıkarmalıdır.

4.4. Yayın (Broadcasting) ve Otomasyon

Çıkarılan makine öğrenmesi sonuçları veya ham/filtrelenmiş grafik verileri SignalR üzerinden anlık olarak mobil uygulamaya basılmalıdır.

"Koltuğa uzandı" gibi kesin (boolean) durum tespit edildiğinde, akıllı ev sistemini (Home Assistant) tetiklemek üzere MQTT üzerinden spesifik bir topic'e (örn: home/radar/status) komut gönderilmelidir.

Bu doküman, sistemin hem mekanik hem de yazılımsal felsefesini yansıtmaktadır. Kod mimarisi kurulurken performans, düşük gecikme (low-latency) ve asenkron I/O yönetimi birinci öncelik olmalıdır.