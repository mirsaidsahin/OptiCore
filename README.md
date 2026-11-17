# OptiCore

**Windows için ölçerek optimize eden, her değişikliği geri alınabilir tutan sistem platformu.**

OptiCore; sistem analizi, temizlik, başlangıç yönetimi, işlem yönetimi, oyun performansı, Windows ayarları,
program kaldırma, sürücüler, ağ, güvenlik, depolama, benchmark ve otomatik bakımı tek bir uygulamada toplar.
Üç ilkeye göre tasarlandı:

1. **Ölç, tahmin etme.** Her bulgu, dayandığı sayılarla birlikte gösterilir. Sağlık puanının nasıl hesaplandığı
   uygulamanın içinde açıklanır. Önce/sonra ölçümleri ve benchmark'lar bir değişikliğin gerçek etkisini gösterir.
2. **Her şey geri alınabilir.** Sisteme yapılan her değişiklik uygulanmadan önce *değişiklik günlüğüne*
   (önceki ve sonraki değerleriyle) yazılır. Geri Alma Merkezi tek bir değişikliği ya da her şeyi eski hâline döndürür
   ve kaydedilen değişikliklerin hâlâ geçerli olup olmadığını canlı sistemle karşılaştırır.
3. **Dürüst ol.** Her Windows ayarının yanında kanıt düzeyi (kanıtlı / duruma bağlı / yalnızca his / etkisi
   tartışmalı), riski, ödünleşimi ve kayıt defterinde tam olarak neyi değiştirdiği yazar. Kayıt defteri "temizliği",
   Prefetch silme, Defender'ı kapatma gibi ölçülebilir faydası olmayan ya da zararlı işlemler bilerek yoktur.

---

## Modüller

| Sayfa | Neler yapar |
|---|---|
| **Pano** | Sağlık puanı (5 grup, ağırlıklı ve açıklamalı), önerilen işlemler, canlı CPU/RAM/GPU/disk/ağ grafikleri, çekirdek haritası (P/E çekirdekleri ayrı), en çok kaynak tüketenler, depolama, pil ve sıcaklık. Özelleştirilebilir. |
| **Performans Zekâsı** | Tüm modüllerden gelen bulgular (kanıt + beklenen etki + tek tıkla çözüm), **"Şu an neden yavaş?"** canlı teşhisi (8 sn dinler; ısıl kısılma, bellek baskısı, tek çekirdek sınırı, disk gecikmesi… ve sorumlu uygulamalar), puan ve kaynak kullanımı geçmişi, **önce/sonra karşılaştırma**. |
| **Sistem** | İşlemci (hibrit çekirdekler, önbellek), bellek modülleri ve yükseltme yolu, ekran kartı (D3DKMT ile sürücüsüz sıcaklık/kullanım), diskler (SMART, yönetici ile), ağ bağdaştırıcıları, pil sağlığı ve kapasite geçmişi, termal (kısılma tespiti), anakart/BIOS/TPM/Güvenli Önyükleme. Rapor dışa aktarma (seri numaraları çıkarılarak). |
| **Akıllı Temizlik** | 25+ kategori (Windows, 6 tarayıcı, Electron uygulamaları, oyun başlatıcıları, npm/pip/NuGet/Gradle/Cargo/Go önbellekleri) — önce ölçer, gösterir, sonra siler. Yinelenen dosyalar (SHA-256, sabit bağlantılar ayıklanır), büyük dosyalar, boş klasörler, **proje artıkları** (eski projelerdeki node_modules/bin/obj/.venv — proje dosyasıyla doğrulanır). |
| **Başlangıç** | Kayıt defteri, başlangıç klasörü, Mağaza uygulamaları, zamanlanmış görevler, hizmetler. Açılış etkisi **ölçülür** (Windows'un açılış izi ya da OptiCore'un açılış sonrası ölçümü). Açılış süresi geçmişi. **Başlangıç bekçisi**: yeni bir program kendini başlangıca eklediğinde bildirir. Şüpheli girdiler (kodlanmış PowerShell, Temp'ten çalışan, çift uzantı…) işaretlenir. |
| **Windows Ayarları** | 51 ayar (gizlilik, telemetri, performans, görünüm, oyun, Gezgin, güncellemeler), 4 ön ayar, güç planları. Windows güncellemesi bir ayarı geri alırsa fark eder ve yeniden uygulamayı önerir. |
| **Oyun** | Steam/Epic/GOG/Xbox/Riot kütüphanesi ve Roblox, oyun başına profil (güç planı, öncelik, tarayıcıları verimlilik moduna alma — oyun kapanınca geri döner), oturum otomatik algılama, **ETW ile FPS, kare süresi, %1/%0,1 düşük, takılma sayısı**, darboğaz analizi (GPU / CPU / tek çekirdek / bellek / ısı / FPS sınırı), arka plan analizi, oturum geçmişi, oyun içi katman. **32 oyun** için elle yazılmış notlar ve ipuçları (ETS2, ATS, FS25, BeamNG, CS2, VALORANT, Fortnite, Roblox, GTA V, Cyberpunk 2077, Elden Ring, Forza Horizon, Flight Simulator…; liste `GameKnowledge.cs` içinde). Oyun, mağaza kimliğinden ya da kurulum klasörünün adından tanınır; profil, FPS ve darboğaz analizi listede olmayan oyunlarda da çalışır. |
| **İşlemler** | Gruplu / liste / ağaç görünümü, CPU/bellek/IO/GPU, ayrıntılar (komut satırı, imza, üst işlemler, bağlantılar), öncelik, **verimlilik modu (EcoQoS)**, duraklatma, ağaç sonlandırma, işlem kuralları, uygulama başına kullanım geçmişi, **bellek sızıntısı** ve uzun süreli CPU kullanımı uyarıları. |
| **Uygulamalar** | Masaüstü + Mağaza uygulamaları, gerçek boyut ölçümü, toplu ve sessiz kaldırma (MSI/Inno/Squirrel), kaldırma sonrası artık taraması (klasör, kayıt defteri, kısayol — kayıt anahtarları yedeklenerek silinir), gereksiz yazılım tespiti, taşınabilir programlar. |
| **Depolama** | Kutu haritası (içine girilebilir), dosya türleri, en büyük dosyalar, disk sağlığı/SMART, boş alan eğilimi ve **dolma tarihi tahmini**, izlenen klasörler. |
| **Ağ** | Canlı trafik, Wi-Fi sinyali/standart/kanal, gecikme-titreşim-paket kaybı (modem ve internet ayrı ayrı), **DNS karşılaştırması** (ham UDP sorgusu, önbelleksiz) ve DNS değiştirme, uygulama başına bağlantılar, adım adım bağlantı teşhisi, onarım araçları. |
| **Sürücüler** | Envanter, sorunlu aygıtlar (hata kodu açıklamalı), eski sürücüler, pnputil ile yedekleme/geri yükleme, resmî üretici sayfaları. **Sürücü indirmez** (yanlış sürücü riski). |
| **Güvenlik ve Gizlilik** | Defender, güvenlik duvarı, UAC, SmartScreen, güncellemeler, Windows sürüm desteği, Güvenli Önyükleme, TPM, BitLocker, bellek bütünlüğü, RDP, SMB1, hesaplar, proxy, hosts dosyası. Ağa açık portlar (risk açıklamalı), şüpheli işlemler (imza, konum) ve SHA-256 → VirusTotal, **uygulama izinleri** (kamera/mikrofon/konum… son kullanım ve kullanılmayan izinler), **tarayıcı eklentisi denetimi**. Güvenlik ayarlarını kendisi değiştirmez; ilgili Windows sayfasını açar. |
| **Geri Alma Merkezi** | Değişiklik günlüğü, canlı doğrulama, tek kayıt / tüm işlem / her şeyi geri alma, geri yükleme noktaları, yapılandırmayı JSON olarak dışa aktarma. |
| **Tanılama** | Uygulama çökmeleri ve donmaları (sorumlu modül ve hata kodu açıklaması), mavi ekran (bug check kodu → olası neden), beklenmedik kapanmalar, olay günlüğü analizi (bilinen olaylar açıklanır), kararlılık endeksi, Windows Update geçmişi, **winget ile uygulama güncellemeleri**. |
| **Benchmark** | CPU (tek/çok çekirdek), bellek (bant genişliği, gecikme), disk (önbelleksiz sıralı/4K), ekran kartı (WebGL), internet hızı, **kararlılık ve ısı testi** (98 °C'de kendini durdurur). Geçmiş ve önceki sonuca göre fark. 1000 puan = Core i5-1235U / DDR4-3200 / NVMe / Iris Xe. |
| **Otomatik Bakım** | Takvimli görevler (analiz, temizlik, başlangıç denetimi, geri alınan ayarları onarma, geri yükleme noktası), yalnızca boştayken/prizdeyken çalışma, 3 hazır profil, uyarı kuralları, bakım geçmişi, oturum açılışında başlatma. |
| **Araçlar** | Sağ tık menüsü yöneticisi, kilitli dosya bulucu, güvenli silme (SSD uyarısıyla), dosya taşıyıcı (geri alınabilir), **sistem anlık görüntüsü ve fark karşılaştırma**, yinelenen uygulama/çalışma zamanı tespiti. |

Ek olarak: Türkçe/İngilizce arayüz, koyu/açık/sistem teması ve 5 vurgu rengi, **Ctrl+K komut paleti** (sayfa,
ayar ve eylem arama), bildirim merkezi, sistem tepsisi, tek örnek çalışma, simülasyon modu.

---

## Mimari

```
src/
  Engine/        Tüm sistem mantığı (.NET 10 kitaplığı, kullanıcı arayüzünden bağımsız)
    Core/        RPC yönlendirici, işler (job), veri deposu, ayarlar, bildirimler, iki dilli metin (LText)
    Journal/     Değişiklik günlüğü: kaydet → uygula → doğrula → geri al
    Platform/    Win32/WMI/PDH/kayıt defteri/Görev Zamanlayıcı/Authenticode yardımcıları
    Native/      P/Invoke bildirimleri
    Modules/     Her özellik bir modül; bulgularını Performans Zekâsı'na kaynak olarak ekler
  App/           Masaüstü kabuğu: WinForms + WebView2, tepsi, yerel diyaloglar, oyun katmanı
    ui/          Arayüz (Preact + htm, derleme adımı yok; tüm grafikler elle çizilmiş SVG)
  Cli/           opticore-cli: motor yöntemlerini çağırma ve tarayıcı için geliştirme sunucusu
tests/           xUnit testleri (geri alma garantisi gerçek kayıt defterinde, korumalı bir anahtarda sınanır)
```

Arayüz motorla tek bir mesaj protokolüyle konuşur: `{id, method, params, lang}` → `{id, ok, result|error}`.
Masaüstünde bu WebView2 mesajlarıyla, geliştirme sunucusunda yerel HTTP ile taşınır (yalnızca 127.0.0.1,
her çalıştırmaya özel belirteç, yabancı Origin/Host reddedilir, sunucu yönetici olarak çalıştırılamaz).
Uzun işlemler (tarama, benchmark…) iş olarak başlar ve arayüz ilerlemelerini izler.

Tüm durum `%LOCALAPPDATA%\OptiCore` altında okunabilir JSON dosyalarındadır. Veritabanı yok, telemetri yok;
internete yalnızca kullanıcının başlattığı hız testi, DNS testi ve güncelleme denetimi çıkar.

## Derleme ve çalıştırma

Gereken: Windows 10/11 x64, [.NET 10 SDK](https://dot.net/download), WebView2 çalışma zamanı (Windows 11'de hazır gelir).

```powershell
./build.ps1                    # her şeyi derle
./build.ps1 test               # testleri çalıştır
./build.ps1 run -DryRun        # masaüstü uygulamasını simülasyon modunda başlat
./build.ps1 run                # masaüstü uygulamasını başlat
./build.ps1 serve -DryRun      # arayüzü tarayıcıda geliştirmek için: http://127.0.0.1:5317
./build.ps1 publish            # tek dosyalık, kendi içinde çalışan sürüm → ./release/OptiCore.exe
```

`release\OptiCore.exe` tek başına bir dosyadır: arayüz exe'nin içine gömülüdür ve ilk açılışta kullanıcının
veri klasörüne çıkarılır. .NET kurulu olmayan bir Windows 10/11 (x64) bilgisayara kopyalanıp doğrudan çalıştırılabilir.
Exe dijital olarak imzalı olmadığı için başka bir bilgisayarda ilk açılışta Windows SmartScreen uyarı gösterebilir.

Geliştirme derlemeleri ise .NET'i bulmak için `build.ps1 run` üzerinden başlatılmalıdır.

Komut satırından herhangi bir motor yöntemini çağırmak:

```powershell
opticore-cli call system.hardware
opticore-cli call processes.top n=5 --warm 2000
opticore-cli call cleanup.scan --wait --dry-run
opticore-cli methods
```

**Simülasyon modu** (`--dry-run`): bütün taramalar ve ölçümler gerçektir, ama sistemde hiçbir şey değişmez;
değişiklik günlüğüne "simülasyon" olarak yazılır. İlk denemeler için önerilir.

OptiCore standart kullanıcı olarak başlar ve yalnızca gerektiğinde yönetici olarak yeniden başlatmayı önerir.

## Bilinen sınırlar

Bunlar tasarım gereği ya da Windows'un sunduğu verilerle ilgili, açıkça bilinen sınırlardır:

- **Sıcaklıklar:** OptiCore çekirdek sürücüsü yüklemez. Bu yüzden yalnızca Windows'un sunduğu sensörleri
  (ACPI termal bölgeleri, ekran kartı, yönetici modunda diskler) okur; çekirdek başına işlemci sıcaklığı ve
  çoğu dizüstünde fan hızı bu yolla alınamaz. Isıl kısılma tespiti sıcaklığa değil, donanım yazılımının bildirdiği
  hız sınırına dayanır ve her sistemde çalışır.
- **FPS ölçümü** yönetici yetkisi ister (ETW) ve DirectX 9–12 oyunlarını kapsar. Vulkan/OpenGL oyunlarında FPS
  ölçülmez; oturum yine işlemci/ekran kartı/sıcaklık verileriyle kaydedilir. Oyun içi katman pencereli ve
  kenarlıksız modda görünür, özel tam ekranda görünmez.
- **SMART verileri, açılış süresi geçmişi, geri yükleme noktaları ve Windows'un açılış izi** yönetici yetkisi ister.
- **Ekran kartı benchmark'ı** WebGL üzerinden ölçülür; aynı bilgisayarda önce/sonra karşılaştırması için
  güvenilirdir, oyunlardaki DirectX/Vulkan performansının birebir karşılığı değildir.
- **Uygulama başına ağ trafiği** baytı olarak ölçülemez (Windows bunu yönetici olmadan sunmaz); bağlantı ve
  uzak sunucu sayısı gösterilir.
- Bu sürüm standart kullanıcı ve simülasyon modunda gerçek bir bilgisayarda uçtan uca denendi; değişiklik
  günlüğünün uygula/geri al zinciri testlerle gerçek kayıt defterinde doğrulandı. Yönetici yetkisiyle çalışan
  yollar (FPS yakalama, SMART, geri yükleme noktası, sürücü yedeği) derleniyor ama bu oturumda yetkili olarak
  çalıştırılmadı.

## Üçüncü taraf bileşenler

- [Preact](https://preactjs.com) 10.29.8 ve [htm](https://github.com/developit/htm) 3.1.1 — `src/App/ui/vendor` (lisanslar yanında)
- [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2)
- [Microsoft.Diagnostics.Tracing.TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent) — FPS yakalama
- [System.Management](https://www.nuget.org/packages/System.Management) — WMI
