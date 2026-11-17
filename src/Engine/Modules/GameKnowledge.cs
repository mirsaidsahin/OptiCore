using OptiCore.Engine.Core;

namespace OptiCore.Engine.Modules;

/// <summary>
/// Hand-written notes for specific games: what usually limits their frame rate and which settings matter.
/// A game is recognized by its store id (steam:730, epic:Fortnite…) or, for other stores and versions,
/// by the name of its install folder. Everything else in the Gaming module (profiles, FPS, bottleneck
/// analysis) works for every game; these notes only add game-specific advice on top.
/// </summary>
public static class GameKnowledge
{
    private sealed record Entry(string Key, string[] Ids, string[] Folders, LText Note, LText[] Tips);

    private static readonly LText CompareApis = Loc.T("Oyun birden fazla grafik arayüzü (DirectX 11/12, Vulkan) sunuyorsa her biriyle birer oturum oynayıp Oyun > Oturumlar'da karşılaştırın; hangisinin daha iyi olduğu ekran kartına ve sürücüye göre değişir. OptiCore FPS'i DirectX ile ölçebilir, Vulkan'da ölçemez.",
        "If the game offers several graphics APIs (DirectX 11/12, Vulkan), play one session with each and compare them under Gaming > Sessions; which is better depends on the graphics card and driver. OptiCore measures FPS with DirectX, not with Vulkan.");

    private static readonly Entry[] Entries =
    [
        // ---------------------------------------------------------------- simulators
        new("ets2", ["steam:227300"], ["Euro Truck Simulator 2"],
            Loc.T("Euro Truck Simulator 2 tek bir ana iş parçacığında çalışır: performansı işlemcinin tek çekirdek hızı belirler. Yoğun şehirlerde ve trafikte FPS düşüşü çoğu zaman ekran kartından değil, işlemciden kaynaklanır.",
                  "Euro Truck Simulator 2 runs on one main thread: single-core CPU speed decides its performance. FPS drops in dense cities and traffic usually come from the CPU, not the graphics card."),
            [
                Loc.T("Ayarlar > Grafik'te \"Ölçekleme\" (Scaling) değeri %100'ün üzerindeyse oyun daha yüksek çözünürlükte çizilir; %200, ekran kartı yükünü dört katına çıkarır.", "If Settings > Graphics > Scaling is above 100%, the game renders at a higher resolution; 200% quadruples the graphics load."),
                Loc.T("Trafik yoğunluğu ve \"Görüş mesafesi\" işlemciyi en çok yoran iki ayardır; şehirlerde takılma varsa önce bunları düşürün.", "Traffic density and view distance are the two settings that load the CPU most; lower them first if cities stutter."),
                Loc.T("Çok sayıda harita ve trafik modu yükleme süresini ve bellek kullanımını belirgin biçimde artırır. Mod Yöneticisi'nde kullanmadıklarınızı devre dışı bırakın.", "Many map and traffic mods noticeably increase loading time and memory use. Disable the ones you do not use in the Mod Manager."),
                Loc.T("Oyun varsayılan olarak DirectX 11 ile çalışır; OptiCore'un FPS ölçümü bu oyunda doğrudan çalışır.", "The game runs on DirectX 11 by default; OptiCore's FPS measurement works directly with it."),
            ]),
        new("ats", ["steam:270880"], ["American Truck Simulator"],
            Loc.T("American Truck Simulator, ETS2 ile aynı motoru kullanır: tek çekirdek işlemci hızı belirleyicidir.", "American Truck Simulator uses the same engine as ETS2: single-core CPU speed is decisive."),
            [
                Loc.T("Ölçeklemeyi %100'de tutun; trafik yoğunluğu ve görüş mesafesi işlemci yükünü en çok etkileyen ayarlardır.", "Keep Scaling at 100%; traffic density and view distance affect CPU load the most."),
                Loc.T("Büyük şehirlerde (Los Angeles, Phoenix) FPS düşüşü normaldir; bu bölgelerdeki oturumları Oturumlar sekmesinde ayrıca karşılaştırın.", "FPS drops in big cities (Los Angeles, Phoenix) are normal; compare sessions in those areas separately under Sessions."),
            ]),
        new("fs25", ["steam:2300320"], ["Farming Simulator 25", "FarmingSimulator2025"],
            Loc.T("Farming Simulator 25 hem işlemciyi (araçlar, bitki ve hayvan simülasyonu) hem ekran kartını yoğun kullanır. Büyük çiftliklerde ve çok sayıda araçla işlemci darboğazı sık görülür.",
                  "Farming Simulator 25 loads both the CPU (vehicles, crop and animal simulation) and the graphics card heavily. On large farms with many vehicles a CPU bottleneck is common."),
            [
                Loc.T("Ekran kartı darboğazında önce \"Gölge kalitesi\" ve \"Bitki örtüsü görüş mesafesi\"ni düşürün; görüntü kalitesine en az etki edip en çok kazandıranlar bunlardır.", "With a GPU bottleneck, lower shadow quality and foliage view distance first; they cost the least visually and gain the most."),
                Loc.T("Ekran kartınız destekliyorsa DLSS/FSR/XeSS'i \"Kalite\" modunda açın.", "If your graphics card supports it, enable DLSS/FSR/XeSS in Quality mode."),
                Loc.T("Çok sayıda modlu araç ve yerleştirilebilir nesne işlemci yükünü artırır; FPS düşüyorsa önce kullanılmayan araçları satın.", "Many modded vehicles and placeables increase CPU load; if FPS drops, sell unused vehicles first."),
                Loc.T("En az 16 GB bellek önerilir; 8 GB'ta büyük haritalarda takılma olur.", "At least 16 GB of memory is recommended; 8 GB stutters on large maps."),
            ]),
        new("fs22", ["steam:1248130"], ["Farming Simulator 22", "FarmingSimulator2022"],
            Loc.T("Farming Simulator 22 büyük çiftliklerde işlemciyi zorlar.", "Farming Simulator 22 strains the CPU on large farms."),
            [Loc.T("Gölge ve bitki örtüsü mesafesini düşürmek en büyük kazancı sağlar.", "Lowering shadows and foliage distance gives the biggest gain.")]),
        new("beamng", ["steam:284160"], ["BeamNG.drive"],
            Loc.T("BeamNG.drive'da her araç ayrı bir fizik iş parçacığı kullanır: araç sayısı arttıkça işlemci yükü hızla artar.", "In BeamNG.drive every vehicle uses its own physics thread: CPU load grows quickly with the number of vehicles."),
            [
                Loc.T("Yapay zekâ trafik sayısını işlemcinizin fiziksel çekirdek sayısının yarısı civarında tutun.", "Keep AI traffic at around half the number of physical CPU cores."),
                Loc.T("Vulkan yerine DirectX 11 seçiliyse OptiCore FPS ölçebilir.", "With DirectX 11 selected instead of Vulkan, OptiCore can measure FPS."),
            ]),
        new("snowrunner", ["steam:1465360"], ["SnowRunner"],
            Loc.T("SnowRunner'da çamur, su ve araç fiziği işlemciyi; geniş açık haritalar ve bitki örtüsü ekran kartını yorar.", "In SnowRunner mud, water and vehicle physics load the CPU; large open maps and vegetation load the graphics card."),
            [Loc.T("Bitki örtüsü ve gölge kalitesi ekran kartı yükünün büyük kısmıdır; ekran kartı sınırındaysanız önce bunları düşürün.", "Vegetation and shadow quality are most of the graphics load; lower them first if you are GPU bound.")]),
        new("assettocorsa", ["steam:244210"], ["assettocorsa"],
            Loc.T("Assetto Corsa eski bir motor kullanır ve büyük ölçüde tek çekirdeğe dayanır; ekran kartı çoğu sistemde rahattır.", "Assetto Corsa uses an older engine and relies mostly on one core; the graphics card is comfortable on most systems."),
            [
                Loc.T("Yapay zekâ rakip sayısı ve yansıma (reflection) güncelleme sıklığı işlemci yükünü en çok etkileyen ayarlardır.", "The number of AI opponents and the reflection update rate affect CPU load the most."),
                Loc.T("Content Manager ve Custom Shaders Patch kullanıyorsanız, CSP'nin ışık ve hava efektleri ekran kartı yükünü belirgin biçimde artırır.", "If you use Content Manager with Custom Shaders Patch, CSP's lighting and weather effects noticeably increase the graphics load."),
            ]),
        new("acc", ["steam:805550"], ["Assetto Corsa Competizione"],
            Loc.T("Assetto Corsa Competizione Unreal Engine 4 kullanır ve ekran kartını yoğun kullanır; yağmurda ve gece yarışlarında yük artar.", "Assetto Corsa Competizione uses Unreal Engine 4 and is heavy on the graphics card; the load rises in rain and night races."),
            [Loc.T("Kalabalık rakip sayısında işlemci de sınıra gelebilir; FPS düşüşünün kaynağını OptiCore'un darboğaz analizinden ayırt edin.", "With large grids the CPU can hit its limit too; tell the source of an FPS drop apart with OptiCore's bottleneck analysis.")]),
        new("msfs", ["steam:1250410", "steam:2537590"], ["Microsoft Flight Simulator", "Microsoft.FlightSimulator", "Microsoft.Limitless"],
            Loc.T("Microsoft Flight Simulator hem ana işlemci iş parçacığına hem ekran kartına yük bindirir; büyük havalimanları ve şehirler işlemciyi, bulutlar ve çözünürlük ekran kartını zorlar.",
                  "Microsoft Flight Simulator loads both the main CPU thread and the graphics card; big airports and cities strain the CPU, clouds and resolution strain the GPU."),
            [
                Loc.T("Geliştirici modunda FPS göstergesi darboğazı (\"main thread\" ya da \"GPU\") kendisi de söyler; OptiCore'un analiziyle karşılaştırabilirsiniz.", "In developer mode the FPS display names the bottleneck itself (\"main thread\" or \"GPU\"); you can compare it with OptiCore's analysis."),
                Loc.T("Arazi ve nesne ayrıntı düzeyi (LOD) işlemci yükünü, hacimsel bulutlar ekran kartı yükünü en çok belirleyen ayarlardır.", "Terrain and object level of detail (LOD) drive CPU load the most, volumetric clouds drive the GPU load."),
            ]),
        new("citiesskylines", ["steam:255710"], ["Cities_Skylines"],
            Loc.T("Cities: Skylines büyük şehirlerde işlemciyi ve özellikle belleği zorlar; çok sayıda mod ve varlık bellek kullanımını katlar.", "Cities: Skylines strains the CPU and especially memory in large cities; many mods and assets multiply memory use."),
            [Loc.T("Yoğun mod kullanımında 16 GB'ın üzeri bellek gerekir; bellek dolarsa oyun diske sayfalar ve takılır.", "Heavy mod use needs more than 16 GB of memory; when memory fills up the game pages to disk and stutters.")]),
        new("citiesskylines2", ["steam:949230"], ["Cities Skylines II"],
            Loc.T("Cities: Skylines II ekran kartını çok yoğun kullanır; nüfus arttıkça simülasyon işlemciyi de zorlar.", "Cities: Skylines II is very heavy on the graphics card; as the population grows the simulation strains the CPU too."),
            [Loc.T("Alan derinliği (depth of field) ve hacimsel bulutları kapatmak görüntüyü az etkileyip belirgin FPS kazandırır.", "Turning off depth of field and volumetric clouds changes the image little and gains noticeable FPS.")]),
        new("arma3", ["steam:107410"], ["Arma 3"],
            Loc.T("Arma 3 büyük ölçüde tek çekirdeğe dayanır; çok oyunculu sunucularda FPS'i sunucu ve yapay zekâ yükü de belirler.", "Arma 3 relies largely on one core; on multiplayer servers the server and AI load also decide FPS."),
            [Loc.T("\"Nesne görüş mesafesi\" işlemci yükünü, genel görüş mesafesinden daha çok belirler.", "Object view distance drives CPU load more than overall view distance.")]),
        new("dayz", ["steam:221100"], ["DayZ"],
            Loc.T("DayZ şehirlerde ve kalabalık sunucularda işlemci sınırına takılır.", "DayZ hits the CPU limit in towns and on busy servers."),
            [CompareApis]),

        // ---------------------------------------------------------------- competitive
        new("cs2", ["steam:730"], ["Counter-Strike Global Offensive", "Counter-Strike 2"],
            Loc.T("Counter-Strike 2'de çoğu sistemde FPS'i işlemci belirler (özellikle tek çekirdek hızı ve önbellek); düşük grafik ayarlarında ekran kartı boşta bekler. Rekabetçi oyunda ortalama FPS'ten çok %1 düşük değerleri ve kare süresi tutarlılığı önemlidir.",
                  "In Counter-Strike 2 the CPU (especially single-core speed and cache) decides FPS on most systems; at low graphics settings the graphics card waits. In competitive play the 1% lows and frame time consistency matter more than average FPS."),
            [
                Loc.T("Ekran kartınız destekliyorsa NVIDIA Reflex ya da AMD Anti-Lag'i açın; giriş gecikmesini azaltır.", "If your graphics card supports it, enable NVIDIA Reflex or AMD Anti-Lag; it reduces input latency."),
                Loc.T("Çoklu örneklemeli kenar yumuşatma (MSAA) ekran kartı yükünün büyük kısmıdır; ekran kartı sınırındaysanız önce onu düşürün.", "Multisample anti-aliasing (MSAA) is most of the graphics load; lower it first if you are GPU bound."),
                Loc.T("G-Sync/FreeSync kullanıyorsanız FPS'i monitörün yenileme hızının birkaç kare altına sınırlamak (ör. 144 Hz için 141) kare sürelerini daha düzgün tutar.", "With G-Sync/FreeSync, capping FPS a few frames below the refresh rate (e.g. 141 for 144 Hz) keeps frame times smoother."),
                Loc.T("Arka planda açık tarayıcı sekmeleri ve yayın yazılımları %1 düşük değerlerini bozar; oyun profilinde tarayıcıları kısma seçeneğini açık tutun.", "Browser tabs and streaming software in the background hurt the 1% lows; keep browser throttling on in the game profile."),
            ]),
        new("valorant", ["riot:valorant"], ["VALORANT"],
            Loc.T("VALORANT hafif bir oyundur ve neredeyse her sistemde işlemci sınırlıdır; ekran kartı çoğu zaman yarı yükte çalışır.", "VALORANT is a light game and CPU bound on almost every system; the graphics card usually runs at half load."),
            [
                Loc.T("Grafik ayarlarındaki \"Çok iş parçacıklı işleme\" (Multithreaded Rendering) seçeneğini açık tutun; işlemci yükünü çekirdeklere dağıtır.", "Keep \"Multithreaded Rendering\" on in the graphics settings; it spreads the CPU load across cores."),
                Loc.T("NVIDIA Reflex seçeneği giriş gecikmesini azaltır.", "The NVIDIA Reflex option reduces input latency."),
                Loc.T("Riot Vanguard, Windows 11'de TPM 2.0 ve Güvenli Önyükleme ister; Güvenlik sayfası ikisinin de durumunu gösterir.", "On Windows 11, Riot Vanguard requires TPM 2.0 and Secure Boot; the Security page shows the state of both."),
            ]),
        new("fortnite", ["epic:Fortnite"], ["Fortnite"],
            Loc.T("Fortnite hem işlemciyi hem ekran kartını kullanır; kalabalık son çemberlerde işlemci yükü artar.", "Fortnite uses both the CPU and the graphics card; in crowded final circles the CPU load rises."),
            [
                Loc.T("Ayarlar'daki \"Performans\" işleme modu, düşük donanımda en yüksek ve en tutarlı FPS'i verir.", "The \"Performance\" rendering mode in Settings gives the highest and most consistent FPS on modest hardware."),
                Loc.T("DirectX 12 modunda bir güncellemeden sonraki ilk maçlarda takılma olması gölgelendirici derlemesindendir ve kendiliğinden geçer.", "Stutter in the first matches after an update in DirectX 12 mode comes from shader compilation and goes away by itself."),
            ]),
        new("apex", ["steam:1172470"], ["Apex Legends"],
            Loc.T("Apex Legends'ta FPS'i çoğunlukla işlemci belirler; ekran kartı yükü yüksek ayarlarda ve yüksek çözünürlükte öne çıkar.", "In Apex Legends the CPU usually sets the frame rate; the graphics load comes forward at high settings and resolutions."),
            [CompareApis]),
        new("pubg", ["steam:578080"], ["PUBG"],
            Loc.T("PUBG Unreal Engine 4 kullanır; işlemci ve ekran kartını birlikte zorlar, iniş bölgelerinde ve kalabalık çatışmalarda FPS düşer.", "PUBG uses Unreal Engine 4; it loads the CPU and GPU together, and FPS drops at landing spots and in crowded fights."),
            [Loc.T("Doku kalitesi ekran kartı belleğine bağlıdır; 4 GB ya da daha az belleği olan kartlarda \"Orta\"nın üzerine çıkarmak takılmaya yol açar.", "Texture quality depends on video memory; on cards with 4 GB or less, going above \"Medium\" causes stutter.")]),
        new("dota2", ["steam:570"], ["dota 2 beta"],
            Loc.T("Dota 2 büyük takım savaşlarında işlemci sınırına takılır; ekran kartı çoğu sistemde rahattır.", "Dota 2 hits the CPU limit in big team fights; the graphics card is comfortable on most systems."),
            [CompareApis]),
        new("r6", ["steam:359550"], ["Tom Clancy's Rainbow Six Siege"],
            Loc.T("Rainbow Six Siege yüksek FPS için hem işlemciye hem ekran kartına yaslanır.", "Rainbow Six Siege leans on both the CPU and the graphics card for high frame rates."),
            [CompareApis]),
        new("roblox", ["roblox:player"], [],
            Loc.T("Roblox'ta performans büyük ölçüde oynadığınız deneyime bağlıdır: kalabalık ve ayrıntılı haritalar işlemciyi zorlar. Roblox varsayılan olarak 60 FPS ile sınırlıdır; OptiCore bu yüzden çoğu oturumda \"kare hızı sınırlı\" sonucunu gösterir.",
                  "In Roblox performance depends largely on the experience you play: crowded, detailed maps strain the CPU. Roblox is capped at 60 FPS by default, so OptiCore shows \"frame rate capped\" for most sessions."),
            [
                Loc.T("Güncel sürümlerde Ayarlar menüsünde kare hızı sınırı seçeneği bulunur; 60 Hz'den yüksek bir monitörünüz varsa sınırı yükseltebilirsiniz.", "Current versions have a frame rate limit option in Settings; if your monitor is faster than 60 Hz you can raise the limit."),
                Loc.T("Grafik modu \"Otomatik\" iken Roblox FPS düştüğünde kaliteyi kendiliğinden düşürür; oturumları karşılaştırırken grafik kalitesini elle sabitleyin.", "With graphics mode set to \"Automatic\", Roblox lowers quality by itself when FPS drops; set graphics quality manually when comparing sessions."),
            ]),

        // ---------------------------------------------------------------- open world
        new("gta5", ["steam:271590", "steam:3240220"], ["Grand Theft Auto V", "Grand Theft Auto V Enhanced", "GTAV"],
            Loc.T("GTA V işlemciyi yoğun kullanır; şehir merkezinde ve hızlı sürüşte yük artar. Enhanced sürüm ışın izleme ve DLSS/FSR ekler.", "GTA V is heavy on the CPU; the load rises downtown and when driving fast. The Enhanced edition adds ray tracing and DLSS/FSR."),
            [
                Loc.T("Grafik ayarlarındaki \"Video Belleği\" çubuğu tahmini ekran kartı belleği kullanımını gösterir; sınırı aşmak takılmaya yol açar.", "The \"Video Memory\" bar in the graphics settings shows the estimated video memory use; going past the limit causes stutter."),
                Loc.T("MSAA en pahalı ayarlardandır; düşürmek ya da kapatmak büyük kazanç sağlar.", "MSAA is among the most expensive settings; lowering or disabling it gains a lot."),
                Loc.T("Nüfus yoğunluğu ve çeşitliliği işlemciyi yorar; şehirde FPS düşüyorsa bunları azaltın.", "Population density and variety load the CPU; reduce them if FPS drops in the city."),
            ]),
        new("rdr2", ["steam:1174180"], ["Red Dead Redemption 2"],
            Loc.T("Red Dead Redemption 2 ekran kartını çok yoğun kullanır; Saint Denis gibi şehirlerde işlemci de sınıra gelir.", "Red Dead Redemption 2 is very heavy on the graphics card; in towns like Saint Denis the CPU reaches its limit too."),
            [
                Loc.T("Su fiziği kalitesi, hacimsel aydınlatma ve MSAA en pahalı ayarlardır; görüntüye en az etki ederek en çok kazandıranlar bunlardır.", "Water physics quality, volumetric lighting and MSAA are the most expensive settings; they gain the most for the least visual cost."),
                CompareApis,
            ]),
        new("cyberpunk", ["steam:1091500", "gog:1423049311"], ["Cyberpunk 2077"],
            Loc.T("Cyberpunk 2077 ekran kartını çok yoğun kullanır; ışın izleme açıkken en ağır oyunlardan biridir. Kalabalık sokaklar işlemci yükünü artırır.", "Cyberpunk 2077 is very heavy on the graphics card and among the heaviest games with ray tracing on. Crowded streets raise the CPU load."),
            [
                Loc.T("Işın izleme açıksa DLSS/FSR/XeSS ve kare üretimi en büyük kazancı sağlar.", "With ray tracing on, DLSS/FSR/XeSS and frame generation give the biggest gain."),
                Loc.T("Oynanış ayarlarındaki \"Kalabalık yoğunluğu\" işlemci yükünü belirler; işlemci sınırındaysanız düşürün.", "\"Crowd density\" in the gameplay settings drives CPU load; lower it if you are CPU bound."),
                Loc.T("Oyun SSD'ye kurulmalıdır; sabit diskte doku yükleme gecikmeleri ve takılmalar olur.", "The game should be installed on an SSD; on a hard disk textures load late and it stutters."),
            ]),
        new("eldenring", ["steam:1245620"], ["ELDEN RING"],
            Loc.T("Elden Ring 60 FPS ile sınırlıdır; OptiCore'un \"kare hızı sınırlı\" tespiti bu oyunda normaldir. Asıl ölçüt %1 düşük değerleri ve takılma sayısıdır.", "Elden Ring is capped at 60 FPS, so OptiCore's \"frame rate capped\" result is normal here. What really matters are the 1% lows and the stutter count."),
            [
                Loc.T("Işın izleme seçeneği büyük bir FPS maliyetiyle gelir ve görsel farkı küçüktür.", "The ray tracing option costs a lot of FPS for a small visual difference."),
                Loc.T("Yeni bölgelere ilk girişteki kısa takılmalar gölgelendirici derlemesindendir; aynı yerde ikinci kez azalır.", "Brief stutters when first entering new areas come from shader compilation; they lessen the second time."),
            ]),
        new("witcher3", ["steam:292030", "gog:1207664663"], ["The Witcher 3"],
            Loc.T("The Witcher 3'ün yeni nesil sürümü DirectX 12 ve ışın izleme getirdi; eski ekran kartlarında DirectX 11 modu genellikle daha hızlıdır.", "The next-gen version of The Witcher 3 added DirectX 12 and ray tracing; on older graphics cards DirectX 11 mode is usually faster."),
            [CompareApis, Loc.T("Novigrad şehrinde işlemci yükü artar; kalabalık sayısını (NPC) düşürmek burada en çok yardımcı olan ayardır.", "CPU load rises in Novigrad; lowering the number of NPCs helps the most there.")]),
        new("bg3", ["steam:1086940", "gog:1456460669"], ["Baldurs Gate 3"],
            Loc.T("Baldur's Gate 3'ün üçüncü bölümündeki şehir işlemciyi belirgin biçimde zorlar; FPS düşüşü orada çoğunlukla işlemci kaynaklıdır.", "The city in Baldur's Gate 3's third act strains the CPU noticeably; FPS drops there are mostly CPU related."),
            [Loc.T("Başlatıcıda DirectX 11 ile Vulkan arasında seçim yapılır; OptiCore FPS'i DirectX 11 ile ölçebilir.", "The launcher lets you choose DirectX 11 or Vulkan; OptiCore can measure FPS with DirectX 11."), CompareApis]),
        new("rust", ["steam:252490"], ["Rust"],
            Loc.T("Rust işlemciyi ve belleği yoğun kullanır; kalabalık sunucularda ve büyük üslerin yakınında FPS'i grafik ayarlarından çok işlemci belirler.", "Rust is heavy on the CPU and memory; on busy servers and near big bases the CPU decides FPS more than the graphics settings."),
            [Loc.T("Rust en az 10 GB, önerilen 16 GB bellek ister; daha azında oyun diske sayfalar ve takılır.", "Rust needs at least 10 GB of memory, 16 GB recommended; with less it pages to disk and stutters.")]),
        new("forza5", ["steam:1551360"], ["ForzaHorizon5", "Forza Horizon 5"],
            Loc.T("Forza Horizon 5 iyi optimize edilmiştir; çoğu sistemde ekran kartı sınırlıdır.", "Forza Horizon 5 is well optimized; it is GPU bound on most systems."),
            [Loc.T("Oyunun kendi benchmark modu vardır; bir ayarın etkisini görmek için önce onu, sonra OptiCore oturumlarını karşılaştırın.", "The game has its own benchmark mode; use it, then compare OptiCore sessions, to see the effect of a setting."),
             Loc.T("Doku kalitesini ekran kartı belleğine göre seçin; sınırı aşınca takılma başlar.", "Pick texture quality according to video memory; stutter starts once you exceed it.")]),
        new("forza4", ["steam:1293830"], ["ForzaHorizon4", "Forza Horizon 4"],
            Loc.T("Forza Horizon 4 iyi optimize edilmiştir ve çoğu sistemde ekran kartı sınırlıdır.", "Forza Horizon 4 is well optimized and GPU bound on most systems."),
            [Loc.T("Oyunun kendi benchmark modunu ayar karşılaştırması için kullanın.", "Use the game's own benchmark mode to compare settings.")]),
        new("helldivers2", ["steam:553850"], ["Helldivers 2"],
            Loc.T("Helldivers 2'de yüksek zorlukta düşman sayısı arttıkça işlemci sınırına takılınır.", "In Helldivers 2 the CPU becomes the limit as enemy counts grow at high difficulty."),
            [Loc.T("İşlemci sınırındaysanız grafik ayarlarını düşürmek FPS'i artırmaz; ekran kartı yükü %80'in altındaysa ayarları rahatça yükseltebilirsiniz.", "When CPU bound, lowering graphics settings will not raise FPS; if the GPU load is below 80% you can safely raise them.")]),
    ];

    /// <summary>Finds the notes for a game by store id first, then by install folder name.</summary>
    public static KnownGame? Identify(Game game)
    {
        var entry = Entries.FirstOrDefault(e => e.Ids.Contains(game.Id, StringComparer.OrdinalIgnoreCase));
        if (entry is null)
        {
            string folder = Path.GetFileName(game.Folder.TrimEnd('\\'));
            if (folder.Equals("Content", StringComparison.OrdinalIgnoreCase)) folder = Path.GetFileName(Path.GetDirectoryName(game.Folder.TrimEnd('\\')) ?? ""); // Xbox: <Game>\Content
            entry = Entries.FirstOrDefault(e => e.Folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase)));
        }
        return entry is null ? null : new KnownGame(entry.Key, entry.Note, entry.Tips);
    }

    public static int Count => Entries.Length;
}
