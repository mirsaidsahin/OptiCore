using System.Diagnostics;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public enum TweakRisk { Safe, Moderate, Advanced }

/// <summary>How well-founded the claimed benefit is. OptiCore says so openly instead of promising speed.</summary>
public enum TweakEvidence { Proven, Situational, Cosmetic, Minimal }

/// <summary>One registry value. On = value while the tweak is applied; Off = Windows default (null means "value absent").</summary>
public sealed record RegOp(string Path, string Name, RegistryValueKind Kind, object On, object? Off, bool MissingIsOn = false, RegistryView View = RegistryView.Registry64);

public sealed class TweakDef
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public LText Title { get; init; }
    public LText Description { get; init; }
    public LText? Tradeoff { get; init; }
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;
    public TweakEvidence Evidence { get; init; } = TweakEvidence.Proven;
    public bool Admin { get; init; }
    /// <summary>none | explorer | logoff | reboot</summary>
    public string Restart { get; init; } = "none";
    public RegOp[] Ops { get; init; } = [];
    public Func<RegOp[]>? DynamicOps { get; init; }
    public string? Service { get; init; }
    public string ServiceOff { get; init; } = "auto";
    public string[] Tasks { get; init; } = [];
    public Func<bool?>? Detect { get; init; }
    public Action<ChangeScope>? Apply { get; init; }
    public Action<ChangeScope>? Revert { get; init; }
    public int MinBuild { get; init; }
    public string[] Presets { get; init; } = [];

    public RegOp[] AllOps() => DynamicOps is null ? Ops : [.. Ops, .. DynamicOps()];
}

public sealed class TweakService
{
    private const string Adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ClassicMenu = @"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private readonly ChangeJournal _journal;
    private readonly DataStore _store;
    private readonly List<TweakDef> _catalog;
    private readonly int _build;

    public TweakService(ChangeJournal journal, DataStore store)
    {
        _journal = journal; _store = store;
        _build = int.TryParse(Reg.GetStr(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild"), out var b) ? b : Environment.OSVersion.Version.Build;
        _catalog = Catalog();
    }

    private static RegOp D(string path, string name, int on, int? off, bool missingIsOn = false) => new(path, name, RegistryValueKind.DWord, on, off, missingIsOn);
    private static RegOp S(string path, string name, string on, string? off) => new(path, name, RegistryValueKind.String, on, off);

    // ------------------------------------------------------------------ catalog
    private static List<TweakDef> Catalog() =>
    [
        // ============================================================ privacy
        new()
        {
            Id = "privacy.adid", Category = "privacy", Presets = ["essentials", "privacy"],
            Title = Loc.T("Reklam kimliğini kapat", "Turn off the advertising ID"),
            Description = Loc.T("Uygulamaların size özel reklam göstermek için kullandığı kimliği devre dışı bırakır.", "Disables the identifier apps use to show you personalized ads."),
            Tradeoff = Loc.T("Reklamlar görünmeye devam eder, yalnızca size göre seçilmez.", "Ads still appear; they are just no longer tailored to you."),
            Ops = [D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)],
        },
        new()
        {
            Id = "privacy.tailored", Category = "privacy", Presets = ["privacy"],
            Title = Loc.T("Kişiselleştirilmiş deneyimleri kapat", "Turn off tailored experiences"),
            Description = Loc.T("Microsoft'un tanılama verilerinizi ipucu, reklam ve öneri sunmak için kullanmasını engeller.", "Stops Microsoft from using your diagnostic data for tips, ads and recommendations."),
            Ops = [D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)],
        },
        new()
        {
            Id = "privacy.startads", Category = "privacy", Presets = ["essentials", "privacy", "lean"],
            Title = Loc.T("Başlat menüsü önerilerini ve tanıtım uygulamalarını kapat", "Turn off Start menu suggestions and promoted apps"),
            Description = Loc.T("Başlat menüsündeki uygulama önerilerini ve Windows'un tanıtım amaçlı uygulamaları kendiliğinden yüklemesini durdurur.", "Stops app suggestions in Start and stops Windows from silently installing promoted apps."),
            Ops =
            [
                D(Cdm, "SystemPaneSuggestionsEnabled", 0, 1), D(Cdm, "SilentInstalledAppsEnabled", 0, 1), D(Cdm, "SubscribedContent-338388Enabled", 0, 1),
                D(Cdm, "PreInstalledAppsEnabled", 0, 1), D(Cdm, "OemPreInstalledAppsEnabled", 0, 1), D(Adv, "Start_IrisRecommendations", 0, 1),
            ],
        },
        new()
        {
            Id = "privacy.tips", Category = "privacy", Presets = ["essentials", "privacy", "gaming", "lean"],
            Title = Loc.T("Windows ipuçları ve \"kurulumu tamamla\" hatırlatmalarını kapat", "Turn off Windows tips and \"finish setting up\" reminders"),
            Description = Loc.T("Windows kullanırken çıkan ipucu bildirimlerini ve güncellemelerden sonra görünen tam ekran kurulum hatırlatmalarını kapatır.", "Turns off tip notifications and the full-screen setup reminders that appear after updates."),
            Ops =
            [
                D(Cdm, "SubscribedContent-338389Enabled", 0, 1), D(Cdm, "SoftLandingEnabled", 0, 1), D(Cdm, "SubscribedContent-310093Enabled", 0, 1),
                D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1),
            ],
        },
        new()
        {
            Id = "privacy.settingsads", Category = "privacy", Presets = ["privacy"],
            Title = Loc.T("Ayarlar uygulamasındaki önerilen içeriği kapat", "Turn off suggested content in Settings"),
            Description = Loc.T("Ayarlar sayfalarında gösterilen Microsoft hizmet tanıtımlarını gizler.", "Hides the Microsoft service promotions shown on Settings pages."),
            Ops = [D(Cdm, "SubscribedContent-338393Enabled", 0, 1), D(Cdm, "SubscribedContent-353694Enabled", 0, 1), D(Cdm, "SubscribedContent-353696Enabled", 0, 1)],
        },
        new()
        {
            Id = "privacy.lockscreen", Category = "privacy", Presets = ["essentials", "privacy"],
            Title = Loc.T("Kilit ekranındaki ipucu ve reklamları kapat", "Turn off tips and ads on the lock screen"),
            Description = Loc.T("Kilit ekranında gösterilen \"eğlenceli bilgiler\" ve tanıtımları kaldırır.", "Removes the \"fun facts\" and promotions shown on the lock screen."),
            Ops = [D(Cdm, "RotatingLockScreenOverlayEnabled", 0, 1), D(Cdm, "SubscribedContent-338387Enabled", 0, 1)],
        },
        new()
        {
            Id = "privacy.bing", Category = "privacy", Presets = ["privacy", "lean"], Restart = "explorer",
            Title = Loc.T("Başlat aramasında web sonuçlarını kapat", "Turn off web results in Start search"),
            Description = Loc.T("Başlat menüsünde yazdıklarınızın Bing'e gönderilmesini durdurur; arama yalnızca bu bilgisayarda yapılır ve daha hızlı yanıt verir.", "Stops what you type in Start from being sent to Bing; search stays on this PC and responds faster."),
            Tradeoff = Loc.T("Başlat menüsünden doğrudan web araması yapılamaz.", "You can no longer search the web straight from Start."),
            Ops = [D(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null), D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, null)],
        },
        new()
        {
            Id = "privacy.searchhighlights", Category = "privacy", Presets = ["privacy", "lean"],
            Title = Loc.T("Arama vurgularını kapat", "Turn off search highlights"),
            Description = Loc.T("Arama kutusundaki günün konusu görsellerini ve önerilerini gizler.", "Hides the daily illustrations and suggestions in the search box."),
            Ops = [D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, 1)],
        },
        new()
        {
            Id = "privacy.launchtrack", Category = "privacy", Presets = ["privacy"],
            Title = Loc.T("Uygulama başlatma takibini kapat", "Turn off app launch tracking"),
            Description = Loc.T("Windows'un hangi uygulamaları açtığınızı kaydetmesini durdurur.", "Stops Windows from recording which apps you open."),
            Tradeoff = Loc.T("Başlat menüsündeki \"en çok kullanılanlar\" listesi boş kalır.", "The \"most used\" list in Start stays empty."),
            Ops = [D(Adv, "Start_TrackProgs", 0, 1)],
        },
        new()
        {
            Id = "privacy.langlist", Category = "privacy", Presets = ["privacy"],
            Title = Loc.T("Web sitelerinin dil listeme erişmesini engelle", "Stop websites from reading my language list"),
            Description = Loc.T("Tarayıcının kurulu dillerinizi sitelere bildirmesini engeller; parmak izi çıkarmayı zorlaştırır.", "Stops the browser from telling sites which languages are installed; makes fingerprinting harder."),
            Ops = [D(@"HKCU\Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1, null)],
        },
        new()
        {
            Id = "privacy.inking", Category = "privacy", Presets = ["privacy"],
            Title = Loc.T("Yazma ve mürekkep kişiselleştirmesini kapat", "Turn off inking and typing personalization"),
            Description = Loc.T("Yazdıklarınızdan kişisel sözlük oluşturulmasını ve bunun Microsoft'a gönderilmesini durdurur.", "Stops building a personal dictionary from what you type and sending it to Microsoft."),
            Ops =
            [
                D(@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0), D(@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0),
                D(@"HKCU\Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, 1), D(@"HKCU\Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, 1),
            ],
        },
        new()
        {
            Id = "privacy.feedback", Category = "privacy", Presets = ["privacy"],
            Title = Loc.T("Geri bildirim isteklerini kapat", "Turn off feedback requests"),
            Description = Loc.T("Windows'un \"deneyiminizi puanlayın\" sorularını göstermesini engeller.", "Stops Windows from asking you to rate your experience."),
            Ops = [D(@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null)],
        },
        new()
        {
            Id = "privacy.cloudclipboard", Category = "privacy", Presets = ["privacy"],
            Title = Loc.T("Panoyu cihazlar arasında eşitlemeyi kapat", "Turn off clipboard sync across devices"),
            Description = Loc.T("Kopyaladığınız metinlerin Microsoft hesabınız üzerinden diğer cihazlara gönderilmesini engeller.", "Stops text you copy from being sent to your other devices through your Microsoft account."),
            Ops = [D(@"HKCU\Software\Microsoft\Clipboard", "EnableCloudClipboard", 0, null)],
        },
        new()
        {
            Id = "privacy.recall", Category = "privacy", Presets = ["privacy"], MinBuild = 26100,
            Title = Loc.T("Recall ekran anlık görüntülerini kapat", "Turn off Recall snapshots"),
            Description = Loc.T("Copilot+ bilgisayarlarda Windows'un ekranınızın düzenli anlık görüntülerini kaydetmesini engeller.", "On Copilot+ PCs, stops Windows from saving regular snapshots of your screen."),
            Ops = [D(@"HKCU\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null)],
        },
        new()
        {
            Id = "privacy.activity", Category = "privacy", Presets = ["privacy"], Admin = true,
            Title = Loc.T("Etkinlik geçmişini kapat", "Turn off activity history"),
            Description = Loc.T("Açtığınız uygulama ve dosyaların geçmişinin tutulmasını ve Microsoft'a yüklenmesini engeller.", "Stops the history of apps and files you open from being kept and uploaded to Microsoft."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null), D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null)],
        },

        // ============================================================ telemetry
        new()
        {
            Id = "telemetry.level", Category = "telemetry", Presets = ["privacy"], Admin = true,
            Title = Loc.T("Tanılama verisini en düşük düzeye indir", "Reduce diagnostic data to the minimum"),
            Description = Loc.T("Microsoft'a gönderilen tanılama verisini \"Gerekli\" düzeyiyle sınırlar. Home ve Pro sürümlerinde daha azı mümkün değildir; \"tamamen kapatma\" vaat eden araçlar gerçeği söylemez.",
                                "Limits the diagnostic data sent to Microsoft to the \"Required\" level. On Home and Pro nothing lower is possible; tools promising to switch it off entirely are not telling the truth."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1, null)],
        },
        new()
        {
            Id = "telemetry.appraiser", Category = "telemetry", Admin = true, Risk = TweakRisk.Moderate, Evidence = TweakEvidence.Situational,
            Title = Loc.T("Uyumluluk değerlendiricisini durdur", "Stop the Compatibility Appraiser"),
            Description = Loc.T("Bilgisayarı düzenli olarak tarayan CompatTelRunner görevini kapatır. Bu görev özellikle sabit diskli sistemlerde dakikalarca disk ve işlemci kullanabilir.",
                                "Disables the CompatTelRunner task that scans the PC periodically. It can use the disk and processor for minutes, especially on hard-disk systems."),
            Tradeoff = Loc.T("Windows sürüm yükseltmeleri için uyumluluk verisi toplanmaz.", "No compatibility data is gathered for Windows feature upgrades."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AITEnable", 0, null)],
            Tasks = [@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"],
        },
        new()
        {
            Id = "telemetry.ceip", Category = "telemetry", Presets = ["privacy"], Admin = true,
            Title = Loc.T("Müşteri Deneyimi Geliştirme Programı'ndan çık", "Leave the Customer Experience Improvement Program"),
            Description = Loc.T("Kullanım istatistiklerinin toplanıp gönderilmesini durdurur.", "Stops usage statistics from being collected and sent."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0, null)],
            Tasks = [@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"],
        },
        new()
        {
            Id = "telemetry.diagtrack", Category = "telemetry", Admin = true, Risk = TweakRisk.Moderate, Evidence = TweakEvidence.Situational,
            Title = Loc.T("Telemetri hizmetini devre dışı bırak (DiagTrack)", "Disable the telemetry service (DiagTrack)"),
            Description = Loc.T("\"Bağlı Kullanıcı Deneyimleri ve Telemetri\" hizmetini kapatır. Kazanç esas olarak gizliliktir; kaynak tasarrufu küçüktür.", "Turns off the \"Connected User Experiences and Telemetry\" service. The gain is mainly privacy; the resource saving is small."),
            Tradeoff = Loc.T("Geri Bildirim Merkezi ve bazı sorun giderme özellikleri çalışmaz.", "Feedback Hub and some troubleshooting features stop working."),
            Service = "DiagTrack", ServiceOff = "auto",
        },

        // ============================================================ performance
        new()
        {
            Id = "perf.startupdelay", Category = "performance", Presets = ["lean", "gaming"], Evidence = TweakEvidence.Situational,
            Title = Loc.T("Başlangıç uygulamalarındaki bekleme süresini kaldır", "Remove the startup apps delay"),
            Description = Loc.T("Windows, oturum açıldıktan sonra başlangıç uygulamalarını birkaç saniye bekletir. Bu ayar beklemeyi kaldırır; uygulamalarınız daha erken hazır olur.",
                                "After sign-in Windows holds startup apps back for a few seconds. This removes the wait, so your apps are ready sooner."),
            Tradeoff = Loc.T("Masaüstü ilk saniyelerde biraz daha meşgul olur.", "The desktop is slightly busier in the first seconds."),
            Ops = [D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, null)],
        },
        new()
        {
            Id = "perf.widgets", Category = "performance", Presets = ["lean"], Admin = true, Restart = "logoff",
            Title = Loc.T("Pencere öğelerini (Widgets) kapat", "Turn off Widgets"),
            Description = Loc.T("Görev çubuğundaki pencere öğeleri panosunu ve arka planda çalışan işlemlerini kapatır; genellikle 100-250 MB bellek serbest kalır.", "Turns off the Widgets board and its background processes; this usually frees 100-250 MB of memory."),
            Tradeoff = Loc.T("Hava durumu ve haber panosu kullanılamaz.", "The weather and news board is no longer available."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null)],
        },
        new()
        {
            Id = "perf.storagesense", Category = "performance", Presets = ["essentials"],
            Title = Loc.T("Depolama Algılayıcısı'nı aç", "Turn on Storage Sense"),
            Description = Loc.T("Disk dolmaya yaklaştığında Windows'un geçici dosyaları ve Geri Dönüşüm Kutusu'nu kendiliğinden temizlemesini sağlar.", "Lets Windows clean temporary files and the Recycle Bin automatically when the disk runs low."),
            Ops = [D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1, 0)],
        },
        new()
        {
            Id = "perf.faststartup", Category = "performance", Admin = true, Evidence = TweakEvidence.Situational,
            Title = Loc.T("Hızlı Başlangıç'ı kapat", "Turn off Fast Startup"),
            Description = Loc.T("\"Kapat\" seçeneğinin çekirdeği gerçekten sıfırlamasını sağlar. Günlerce biriken bellek sızıntılarını, sürücü tuhaflıklarını ve çift işletim sistemi sorunlarını önler.",
                                "Makes \"Shut down\" really reset the kernel. Prevents days of accumulated memory leaks, driver oddities and dual-boot problems."),
            Tradeoff = Loc.T("SSD'de açılış 1-3 saniye, sabit diskte daha fazla uzar.", "Boot takes 1-3 seconds longer on an SSD, more on a hard disk."),
            Ops = [D(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1)],
        },
        new()
        {
            Id = "perf.hibernate", Category = "performance", Admin = true, Risk = TweakRisk.Moderate,
            Title = Loc.T("Hazırda bekletmeyi kapat ve hiberfil.sys'yi sil", "Turn off hibernation and delete hiberfil.sys"),
            Description = Loc.T("Hazırda bekletme dosyasını kaldırır. Bu dosya genellikle takılı belleğin %40'ı kadar yer kaplar.", "Removes the hibernation file, which usually takes about 40% of the installed memory in disk space."),
            Tradeoff = Loc.T("Hazırda bekletme ve Hızlı Başlangıç kullanılamaz. Dizüstülerde pil bittiğinde açık işler kaydedilemez.", "Hibernate and Fast Startup become unavailable. On laptops, open work cannot be saved when the battery runs out."),
            // Some systems never write HibernateEnabled; the presence of hiberfil.sys then tells the truth.
            Detect = () => Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") is int v ? v == 0 : !File.Exists(Path.Combine(Paths.SystemDrive, "hiberfil.sys")),
            Apply = s => s.Command("powercfg /hibernate off", Proc.Sys("powercfg.exe"), "/hibernate off", Proc.Sys("powercfg.exe"), "/hibernate on"),
            Revert = s => s.Command("powercfg /hibernate on", Proc.Sys("powercfg.exe"), "/hibernate on", Proc.Sys("powercfg.exe"), "/hibernate off"),
        },
        new()
        {
            Id = "perf.sysmain", Category = "performance", Admin = true, Risk = TweakRisk.Moderate, Evidence = TweakEvidence.Minimal,
            Title = Loc.T("SysMain (Superfetch) hizmetini kapat", "Disable SysMain (Superfetch)"),
            Description = Loc.T("Sık kullanılan uygulamaları belleğe önceden yükleyen hizmeti kapatır. SSD'li sistemlerde ölçülebilir bir fayda sağlamaz; yalnızca sabit diskli ve belleği az sistemlerde açılış sonrası disk yoğunluğunu azaltabilir.",
                                "Turns off the service that preloads frequently used apps. On SSD systems it brings no measurable benefit; only on hard-disk, low-memory systems can it reduce disk activity after boot."),
            Tradeoff = Loc.T("Uygulamalar ilk açılışta daha yavaş başlar.", "Apps start more slowly on first launch."),
            Service = "SysMain", ServiceOff = "auto",
        },
        new()
        {
            Id = "perf.wsearch", Category = "performance", Admin = true, Risk = TweakRisk.Moderate, Evidence = TweakEvidence.Situational,
            Title = Loc.T("Arama dizin oluşturmayı kapat", "Disable search indexing"),
            Description = Loc.T("Dosya içeriklerini arka planda dizinleyen Windows Search hizmetini kapatır. Sabit diskli sistemlerde arka plan disk kullanımını azaltır.", "Turns off the Windows Search service that indexes file contents in the background. Reduces background disk use on hard-disk systems."),
            Tradeoff = Loc.T("Başlat menüsü ve Dosya Gezgini araması belirgin biçimde yavaşlar; Outlook araması çalışmaz.", "Start menu and File Explorer search become noticeably slower; Outlook search stops working."),
            Service = "WSearch", ServiceOff = "delayed-auto",
        },
        new()
        {
            Id = "perf.backgroundapps", Category = "performance", Admin = true, Risk = TweakRisk.Moderate, Evidence = TweakEvidence.Situational,
            Title = Loc.T("Mağaza uygulamalarının arka planda çalışmasını engelle", "Stop Store apps from running in the background"),
            Description = Loc.T("Microsoft Store uygulamalarının kapalıyken arka planda çalışmasını ilke düzeyinde kapatır; pil ve bellek tasarrufu sağlar.", "Blocks Microsoft Store apps from running in the background when closed; saves battery and memory."),
            Tradeoff = Loc.T("Posta, Takvim gibi uygulamalar kapalıyken bildirim göndermez ve canlı kutucukları güncellenmez.", "Apps such as Mail and Calendar send no notifications while closed."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2, null)],
        },

        // ============================================================ visual
        new()
        {
            Id = "visual.transparency", Category = "visual", Presets = ["lean"], Evidence = TweakEvidence.Minimal,
            Title = Loc.T("Saydamlık efektlerini kapat", "Turn off transparency effects"),
            Description = Loc.T("Görev çubuğu ve menülerdeki bulanık saydamlığı kapatır. Güncel ekran kartlarında fark ölçülemez; zayıf tümleşik grafiklerde arayüz biraz daha akıcı olabilir.",
                                "Turns off the blurred transparency of the taskbar and menus. On current graphics cards the difference cannot be measured; on weak integrated graphics the interface may feel a little smoother."),
            Ops = [D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)],
        },
        new()
        {
            Id = "visual.animations", Category = "visual", Presets = ["lean"], Evidence = TweakEvidence.Cosmetic, Restart = "logoff",
            Title = Loc.T("Pencere ve görev çubuğu animasyonlarını kapat", "Turn off window and taskbar animations"),
            Description = Loc.T("Pencere küçültme/büyütme ve görev çubuğu animasyonlarını kapatır. Gerçek performansı değiştirmez; arayüz daha çabuk tepki veriyormuş gibi hissettirir.",
                                "Turns off minimize/maximize and taskbar animations. It does not change real performance; the interface simply feels more immediate."),
            Ops = [S(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1"), D(Adv, "TaskbarAnimations", 0, 1)],
        },
        new()
        {
            Id = "visual.menudelay", Category = "visual", Presets = ["lean"], Evidence = TweakEvidence.Cosmetic, Restart = "logoff",
            Title = Loc.T("Menü açılma gecikmesini kısalt", "Shorten the menu delay"),
            Description = Loc.T("Alt menülerin açılmadan önceki bekleme süresini 400 ms'den 100 ms'ye indirir.", "Reduces the wait before submenus open from 400 ms to 100 ms."),
            Ops = [S(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "100", "400")],
        },

        // ============================================================ gaming
        new()
        {
            Id = "gaming.gamemode", Category = "gaming", Presets = ["gaming"], Evidence = TweakEvidence.Situational,
            Title = Loc.T("Oyun Modu'nu aç", "Turn on Game Mode"),
            Description = Loc.T("Oyun sırasında Windows Update'in sürücü kurmasını ve yeniden başlatma bildirimi göstermesini engeller, oyuna işlemci önceliği verir.", "During a game it stops Windows Update from installing drivers and showing restart prompts, and gives the game processor priority."),
            Ops = [D(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0, missingIsOn: true)],
        },
        new()
        {
            Id = "gaming.gamedvr", Category = "gaming", Presets = ["gaming", "lean"], Evidence = TweakEvidence.Situational,
            Title = Loc.T("Arka plan oyun kaydını kapat (Game DVR)", "Turn off background game recording (Game DVR)"),
            Description = Loc.T("Xbox Game Bar'ın oyunu arka planda sürekli kaydetmesini kapatır. Kayıt açıkken ekran kartı ve disk üzerinde ölçülebilir bir yük oluşur.", "Stops Xbox Game Bar from continuously recording the game in the background. While recording is on it puts a measurable load on the graphics card and disk."),
            Tradeoff = Loc.T("Win+Alt+G ile \"son 30 saniyeyi kaydet\" çalışmaz.", "\"Record the last 30 seconds\" (Win+Alt+G) stops working."),
            Ops = [D(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0, 1), D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1)],
        },
        new()
        {
            Id = "gaming.hags", Category = "gaming", Admin = true, Risk = TweakRisk.Moderate, Evidence = TweakEvidence.Situational, Restart = "reboot",
            Title = Loc.T("Donanım hızlandırmalı GPU zamanlamasını aç", "Turn on hardware-accelerated GPU scheduling"),
            Description = Loc.T("Grafik zamanlamasını işlemciden ekran kartına devreder. DLSS/FSR kare üretimi için gereklidir; işlemcinin sınırladığı oyunlarda küçük bir kazanç sağlayabilir.",
                                "Moves graphics scheduling from the processor to the graphics card. Required for DLSS/FSR frame generation; may give a small gain in processor-limited games."),
            Tradeoff = Loc.T("Bazı oyun ve sürücü birleşimlerinde takılmaya yol açabilir; sorun olursa geri alın.", "Can cause stutter with some game and driver combinations; undo it if problems appear."),
            Ops = [D(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1)],
        },
        new()
        {
            Id = "gaming.mouseaccel", Category = "gaming", Restart = "logoff",
            Title = Loc.T("Fare ivmesini kapat (işaretçi hassasiyetini artır)", "Turn off mouse acceleration (enhance pointer precision)"),
            Description = Loc.T("İşaretçinin, fareyi ne kadar hızlı hareket ettirdiğinize göre farklı mesafe gitmesini engeller. Nişan almada tutarlılık isteyen oyuncuların standart ayarıdır.",
                                "Stops the pointer from travelling a different distance depending on how fast you move the mouse. The standard setting for players who want consistent aim."),
            Tradeoff = Loc.T("İşaretçi ilk başta farklı hissettirir; alışmak birkaç gün sürebilir.", "The pointer feels different at first; getting used to it can take a few days."),
            Ops = [S(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0", "1"), S(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0", "6"), S(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0", "10")],
        },
        new()
        {
            Id = "gaming.stickykeys", Category = "gaming", Presets = ["gaming", "essentials"],
            Title = Loc.T("Yapışkan Tuşlar kısayol uyarısını kapat", "Turn off the Sticky Keys shortcut prompt"),
            Description = Loc.T("Shift tuşuna beş kez basınca çıkan ve oyunu bölen pencereyi engeller.", "Prevents the dialog that pops up, and interrupts the game, when Shift is pressed five times."),
            Ops =
            [
                S(@"HKCU\Control Panel\Accessibility\StickyKeys", "Flags", "506", "510"), S(@"HKCU\Control Panel\Accessibility\Keyboard Response", "Flags", "122", "126"),
                S(@"HKCU\Control Panel\Accessibility\ToggleKeys", "Flags", "58", "62"),
            ],
        },
        new()
        {
            Id = "gaming.nagle", Category = "gaming", Admin = true, Risk = TweakRisk.Advanced, Evidence = TweakEvidence.Situational, Restart = "reboot",
            Title = Loc.T("Nagle algoritmasını kapat", "Disable Nagle's algorithm"),
            Description = Loc.T("Küçük TCP paketlerinin birleştirilmek üzere bekletilmesini engeller. Yalnızca TCP kullanan oyunlarda (bazı MMO'lar) gecikmeyi azaltır; çoğu güncel oyun UDP kullandığı için onlarda hiçbir etkisi yoktur.",
                                "Stops small TCP packets from being held back to be merged. It lowers latency only in games that use TCP (some MMOs); most current games use UDP, where it has no effect at all."),
            Tradeoff = Loc.T("Büyük dosya aktarımlarında verim biraz düşebilir.", "Throughput of large transfers can drop slightly."),
            DynamicOps = () =>
            {
                const string root = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
                var ops = new List<RegOp>();
                foreach (var id in Reg.SubKeys(root))
                {
                    string key = root + "\\" + id;
                    if (string.IsNullOrEmpty(Reg.GetStr(key, "DhcpIPAddress")) && string.IsNullOrEmpty(Reg.GetStr(key, "IPAddress"))) continue;
                    ops.Add(D(key, "TcpAckFrequency", 1, null));
                    ops.Add(D(key, "TCPNoDelay", 1, null));
                }
                return [.. ops];
            },
        },
        new()
        {
            Id = "gaming.mmcss", Category = "gaming", Admin = true, Risk = TweakRisk.Advanced, Evidence = TweakEvidence.Minimal, Restart = "reboot",
            Title = Loc.T("Multimedya ağ kısıtlamasını kaldır", "Remove multimedia network throttling"),
            Description = Loc.T("Windows'un multimedya oynatılırken ağ paketlerine uyguladığı sınırı kaldırır. İnternette çok önerilir, ancak güncel ağ kartlarında ve oyunlarda ölçülebilir bir kazanç gösterilmemiştir.",
                                "Removes the limit Windows applies to network packets while multimedia plays. Widely recommended online, yet no measurable gain has been shown on current network cards and games."),
            Ops = [D(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", -1, 10)],
        },

        // ============================================================ explorer
        new()
        {
            Id = "explorer.extensions", Category = "explorer", Presets = ["essentials"],
            Title = Loc.T("Dosya uzantılarını göster", "Show file extensions"),
            Description = Loc.T("Dosya adlarının sonundaki .exe, .pdf gibi uzantıları gösterir. \"fatura.pdf.exe\" gibi belge kılığındaki zararlıları fark etmenin en basit yoludur.",
                                "Shows endings such as .exe and .pdf. The simplest way to spot malware disguised as a document, like \"invoice.pdf.exe\"."),
            Ops = [D(Adv, "HideFileExt", 0, 1)],
        },
        new()
        {
            Id = "explorer.hidden", Category = "explorer",
            Title = Loc.T("Gizli dosyaları göster", "Show hidden files"),
            Description = Loc.T("Dosya Gezgini'nde gizli dosya ve klasörleri görünür yapar.", "Makes hidden files and folders visible in File Explorer."),
            Ops = [D(Adv, "Hidden", 1, 2)],
        },
        new()
        {
            Id = "explorer.thispc", Category = "explorer",
            Title = Loc.T("Dosya Gezgini'ni \"Bu bilgisayar\" ile aç", "Open File Explorer to \"This PC\""),
            Description = Loc.T("Gezgin, son kullanılanlar yerine sürücüleriniz ve klasörlerinizle açılır.", "Explorer opens to your drives and folders instead of recent items."),
            Ops = [D(Adv, "LaunchTo", 1, 2)],
        },
        new()
        {
            Id = "explorer.classicmenu", Category = "explorer", Restart = "explorer", MinBuild = 22000,
            Title = Loc.T("Klasik sağ tık menüsünü kullan", "Use the classic context menu"),
            Description = Loc.T("Windows 11'in kısaltılmış menüsü yerine tüm seçenekleri içeren tam menüyü doğrudan açar.", "Opens the full menu with every option directly, instead of Windows 11's shortened one."),
            Detect = () => Reg.Exists(ClassicMenu + @"\InprocServer32"),
            Apply = s => s.RegSet(ClassicMenu + @"\InprocServer32", "", RegistryValueKind.String, ""),
            Revert = s => s.RegDeleteKey(ClassicMenu),
        },
        new()
        {
            Id = "explorer.endtask", Category = "explorer", MinBuild = 22631,
            Title = Loc.T("Görev çubuğu sağ tık menüsüne \"Görevi sonlandır\" ekle", "Add \"End task\" to the taskbar context menu"),
            Description = Loc.T("Donan bir uygulamayı Görev Yöneticisi'ni açmadan, görev çubuğundan kapatmanızı sağlar.", "Lets you close a frozen app from the taskbar without opening Task Manager."),
            Ops = [D(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, 0)],
        },
        new()
        {
            Id = "explorer.taskbarleft", Category = "explorer", MinBuild = 22000,
            Title = Loc.T("Görev çubuğunu sola hizala", "Align the taskbar to the left"),
            Description = Loc.T("Başlat düğmesini ve simgeleri Windows 10'daki gibi sola alır.", "Moves the Start button and icons to the left, as in Windows 10."),
            Ops = [D(Adv, "TaskbarAl", 0, 1)],
        },
        new()
        {
            Id = "explorer.taskview", Category = "explorer", Evidence = TweakEvidence.Cosmetic,
            Title = Loc.T("Görev Görünümü düğmesini gizle", "Hide the Task View button"),
            Description = Loc.T("Görev çubuğundaki Görev Görünümü düğmesini kaldırır (Win+Tab çalışmaya devam eder).", "Removes the Task View button from the taskbar (Win+Tab keeps working)."),
            Ops = [D(Adv, "ShowTaskViewButton", 0, 1)],
        },
        new()
        {
            Id = "explorer.cliphistory", Category = "explorer",
            Title = Loc.T("Pano geçmişini aç (Win+V)", "Turn on clipboard history (Win+V)"),
            Description = Loc.T("Kopyaladığınız son öğeleri saklar; Win+V ile aralarından seçebilirsiniz. Geçmiş yalnızca bu bilgisayarda tutulur.", "Keeps the last items you copied; pick among them with Win+V. The history stays on this PC."),
            Ops = [D(@"HKCU\Software\Microsoft\Clipboard", "EnableClipboardHistory", 1, 0)],
        },
        new()
        {
            Id = "explorer.seconds", Category = "explorer", MinBuild = 22621, Evidence = TweakEvidence.Cosmetic,
            Title = Loc.T("Görev çubuğu saatinde saniyeyi göster", "Show seconds in the taskbar clock"),
            Description = Loc.T("Saatin yanına saniyeleri ekler.", "Adds seconds to the clock."),
            Tradeoff = Loc.T("Saat her saniye yenilendiği için dizüstülerde çok küçük bir ek güç tüketimi olur.", "Because the clock redraws every second, laptops use a tiny bit more power."),
            Ops = [D(Adv, "ShowSecondsInSystemClock", 1, 0)],
        },
        new()
        {
            Id = "explorer.longpaths", Category = "explorer", Admin = true,
            Title = Loc.T("260 karakterden uzun dosya yollarına izin ver", "Allow file paths longer than 260 characters"),
            Description = Loc.T("Derin klasör yapılarında \"yol çok uzun\" hatalarını önler. Geliştirme araçları ve yedekleme yazılımları için yararlıdır.", "Prevents \"path too long\" errors in deep folder structures. Useful for development tools and backup software."),
            Ops = [D(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0)],
        },
        new()
        {
            Id = "explorer.verbose", Category = "explorer", Admin = true,
            Title = Loc.T("Açılış ve kapanışta ayrıntılı durum iletileri göster", "Show detailed status messages at startup and shutdown"),
            Description = Loc.T("\"Lütfen bekleyin\" yerine Windows'un o an ne yaptığını gösterir. Yavaş açılışın nerede takıldığını görmenizi sağlar.", "Shows what Windows is doing instead of \"Please wait\". Lets you see where a slow startup gets stuck."),
            Ops = [D(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", 1, null)],
        },

        // ============================================================ updates
        new()
        {
            Id = "update.norestart", Category = "updates", Presets = ["essentials", "gaming"], Admin = true,
            Title = Loc.T("Oturum açıkken kendiliğinden yeniden başlatmayı engelle", "Prevent automatic restarts while signed in"),
            Description = Loc.T("Windows Update, siz oturum açmışken bilgisayarı kendiliğinden yeniden başlatmaz; güncelleme kurulur ama yeniden başlatma zamanını siz seçersiniz.", "Windows Update will not restart the PC on its own while you are signed in; updates still install, but you choose when to restart."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, null)],
        },
        new()
        {
            Id = "update.p2p", Category = "updates", Presets = ["essentials", "privacy"], Admin = true,
            Title = Loc.T("Güncellemeleri başka bilgisayarlara yüklemeyi kapat", "Stop uploading updates to other PCs"),
            Description = Loc.T("Teslim İyileştirme'nin indirdiğiniz güncellemeleri internetteki diğer bilgisayarlarla paylaşmasını durdurur; yükleme bant genişliğiniz size kalır.", "Stops Delivery Optimization from sharing downloaded updates with other PCs on the internet; your upload bandwidth stays yours."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null)],
        },
        new()
        {
            Id = "update.nodrivers", Category = "updates", Admin = true, Risk = TweakRisk.Moderate, Evidence = TweakEvidence.Situational,
            Title = Loc.T("Windows Update'in sürücü kurmasını engelle", "Stop Windows Update from installing drivers"),
            Description = Loc.T("Windows Update'in, üreticiden kendi kurduğunuz ekran kartı ya da yonga seti sürücüsünü daha eski bir sürümle değiştirmesini önler.", "Prevents Windows Update from replacing a graphics or chipset driver you installed from the manufacturer with an older version."),
            Tradeoff = Loc.T("Sürücü güncellemelerini üreticinin sitesinden kendiniz takip etmeniz gerekir.", "You have to follow driver updates on the manufacturer's site yourself."),
            Ops = [D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, null)],
        },
    ];

    public static readonly (string Id, LText Title, LText Description)[] Presets =
    [
        ("essentials", Loc.T("Herkes için önerilenler", "Recommended for everyone"), Loc.T("Kanıtlı ve yan etkisiz ayarlar: reklamlar, ipuçları, dosya uzantıları, zorla yeniden başlatma.", "Proven settings with no side effects: ads, tips, file extensions, forced restarts.")),
        ("privacy", Loc.T("Gizlilik", "Privacy"), Loc.T("Windows'un topladığı ve gönderdiği verileri en aza indirir.", "Minimizes the data Windows collects and sends.")),
        ("gaming", Loc.T("Oyun", "Gaming"), Loc.T("Oyun sırasında bölünmeyi ve arka plan yükünü azaltan ayarlar.", "Settings that reduce interruptions and background load while gaming.")),
        ("lean", Loc.T("Hafif sistem", "Lean system"), Loc.T("Zayıf donanımda arayüzü sadeleştirir ve arka plan yükünü azaltır.", "Simplifies the interface and reduces background load on modest hardware.")),
    ];

    // ------------------------------------------------------------------ state
    private bool Supported(TweakDef t) => _build >= t.MinBuild && (t.Service is null || ServiceOps.StartMode(t.Service) is not null);

    /// <returns>on | off | mixed | unsupported</returns>
    public string State(TweakDef t)
    {
        if (!Supported(t)) return "unsupported";
        if (t.Detect is not null) return t.Detect() switch { true => "on", false => "off", null => "unsupported" };
        if (t.Service is not null) return ServiceOps.StartMode(t.Service) == "disabled" ? "on" : "off";
        var ops = t.AllOps();
        if (ops.Length == 0) return "unsupported";
        int on = 0, off = 0;
        foreach (var op in ops)
        {
            var live = Reg.Get(op.Path, op.Name, op.View);
            if (live is null) { if (op.MissingIsOn) on++; else off++; continue; }
            string encoded = RegState.Encode(live);
            if (string.Equals(encoded, RegState.Encode(op.On), StringComparison.OrdinalIgnoreCase)) on++;
            else if (op.Off is not null && string.Equals(encoded, RegState.Encode(op.Off), StringComparison.OrdinalIgnoreCase)) off++;
            else if (op.Off is null) off++; // any other explicit value behaves like the default
        }
        return on == ops.Length ? "on" : off == ops.Length ? "off" : "mixed";
    }

    private void Run(ChangeScope scope, TweakDef t, bool on)
    {
        if (on && t.Apply is not null) t.Apply(scope);
        else if (!on && t.Revert is not null) t.Revert(scope);
        foreach (var op in t.AllOps())
        {
            if (on) scope.RegSet(op.Path, op.Name, op.Kind, op.On, op.View);
            else if (op.Off is null) scope.RegDeleteValue(op.Path, op.Name, op.View);
            else scope.RegSet(op.Path, op.Name, op.Kind, op.Off, op.View);
        }
        if (t.Service is not null) scope.Service(t.Service, on ? "disabled" : t.ServiceOff);
        foreach (var task in t.Tasks) scope.Task(task, !on);
    }

    private object Apply(string[] ids, bool on, LText? title = null)
    {
        var tweaks = ids.Select(id => _catalog.FirstOrDefault(t => t.Id == id) ?? throw RpcException.NotFound("Unknown tweak " + id)).Where(Supported).ToList();
        if (tweaks.Count == 0) throw RpcException.Invalid("Nothing to apply");
        if (tweaks.Any(t => t.Admin) && !Elevation.IsElevated && !_journal.DryRun && tweaks.All(t => t.Admin)) throw RpcException.Elevation();

        using var scope = _journal.Begin("tweaks", title ?? (tweaks.Count == 1
            ? (on ? Loc.T("Uygulandı: {0}", "Applied: {0}") : Loc.T("Varsayılana döndürüldü: {0}", "Restored to default: {0}")).F(tweaks[0].Title)
            : (on ? Loc.T("{0} ayar uygulandı", "{0} tweaks applied") : Loc.T("{0} ayar varsayılana döndürüldü", "{0} tweaks restored to default")).F(tweaks.Count)), [.. tweaks.Select(t => t.Id)]);
        var skipped = new List<string>();
        foreach (var t in tweaks)
        {
            if (t.Admin && !Elevation.IsElevated && !scope.DryRun) { skipped.Add(t.Id); continue; }
            Run(scope, t, on);
        }
        string restart = tweaks.Select(t => t.Restart).OrderByDescending(r => r switch { "reboot" => 3, "logoff" => 2, "explorer" => 1, _ => 0 }).First();
        scope.Set.Restart = restart;
        var set = scope.Commit();

        var applied = _store.Load(Path.Combine("tweaks", "applied.json"), () => new Dictionary<string, DateTime>());
        if (!scope.DryRun)
        {
            foreach (var t in tweaks.Where(t => !skipped.Contains(t.Id))) { if (on) applied[t.Id] = DateTime.UtcNow; else applied.Remove(t.Id); }
            _store.Save(Path.Combine("tweaks", "applied.json"), applied);
        }
        return new { applied = set.Applied, failed = set.Failed, simulated = set.Simulated, restart, skippedForElevation = skipped, changeSet = set.Id };
    }

    public List<string> AppliedIds() => [.. _catalog.Where(t => State(t) == "on").Select(t => t.Id)];

    /// <summary>Applies a set of tweaks on behalf of another module (game profiles, automation).</summary>
    public object ApplyMany(string[] ids, bool on, LText title) => Apply(ids, on, title);

    /// <summary>Tweaks OptiCore applied that are no longer in effect (Windows feature updates like to reset them).</summary>
    public List<TweakDef> Drifted()
    {
        var applied = _store.Load(Path.Combine("tweaks", "applied.json"), () => new Dictionary<string, DateTime>());
        return [.. _catalog.Where(t => applied.ContainsKey(t.Id) && State(t) is "off" or "mixed")];
    }

    private object View(TweakDef t) => new
    {
        t.Id, t.Category, t.Title, t.Description, t.Tradeoff, t.Risk, t.Evidence, t.Admin, t.Restart, t.Presets,
        state = State(t),
        // Transparency: exactly what will be changed.
        changes = t.AllOps().Select(o => $"{o.Path}\\{o.Name} = {RegState.Encode(o.On)}")
            .Concat(t.Service is null ? [] : [$"service {t.Service} → disabled"])
            .Concat(t.Tasks.Select(x => "task " + x + " → disabled"))
            .Concat(t.Apply is null ? [] : [t.Id == "perf.hibernate" ? "powercfg /hibernate off" : t.Id == "explorer.classicmenu" ? ClassicMenu + "\\InprocServer32 (default) = \"\"" : "custom"]).ToList(),
    };

    public IEnumerable<Finding> Findings()
    {
        var drift = Drifted();
        if (drift.Count == 0) yield break;
        yield return new Finding
        {
            Id = "tweaks.drift", Group = "maintenance", Severity = Severity.Info,
            Title = Loc.T("Uyguladığınız {0} ayar geri alınmış", "{0} of the tweaks you applied were reset").F(drift.Count),
            Detail = Loc.T("Windows güncellemeleri bazı ayarları varsayılana döndürür. OptiCore bunu fark eder ve tek tıkla yeniden uygulayabilir.", "Windows updates reset some settings to their defaults. OptiCore notices and can re-apply them in one click."),
        }.Ev(Loc.T("Ayarlar", "Tweaks"), string.Join(", ", drift.Take(4).Select(t => t.Title.ToString())))
         .Fix(Loc.T("Yeniden uygula", "Re-apply"), "tweaks.apply", new { ids = drift.Select(t => t.Id).ToArray(), on = true }, safe: true)
         .Go(Loc.T("Ayarları aç", "Open tweaks"), "tweaks");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("tweaks.list", _ => new
        {
            items = _catalog.Where(Supported).Select(View).ToList(),
            presets = Presets.Select(p => new { p.Id, p.Title, p.Description, count = _catalog.Count(t => Supported(t) && t.Presets.Contains(p.Id)) }).ToList(),
            drift = Drifted().Select(t => t.Id).ToList(),
            build = _build,
        });
        rpc.On("tweaks.apply", c => Apply(c.StrArr("ids"), c.Bool("on", true)));
        rpc.On("tweaks.preset", c =>
        {
            string id = c.Str("id");
            var preset = Presets.FirstOrDefault(p => p.Id == id);
            if (preset.Id is null) throw RpcException.NotFound("Unknown preset");
            var ids = _catalog.Where(t => Supported(t) && t.Presets.Contains(id) && State(t) != "on").Select(t => t.Id).ToArray();
            if (ids.Length == 0) return new { applied = 0, failed = 0, simulated = _journal.DryRun, restart = "none", skippedForElevation = Array.Empty<string>(), changeSet = (string?)null };
            return Apply(ids, true, Loc.T("Ön ayar uygulandı: {0}", "Preset applied: {0}").F(preset.Title));
        });
        rpc.Do("tweaks.restartExplorer", _ =>
        {
            if (_journal.DryRun) return;
            foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { /* another session */ } finally { p.Dispose(); } }
            Thread.Sleep(600);
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo(Path.Combine(Paths.Windows, "explorer.exe")) { UseShellExecute = true });
        });

        // ---- power plans
        rpc.On("power.plans", _ => new { plans = PowerPlans.List(), ultimate = PowerPlans.List().Any(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Nihai", StringComparison.OrdinalIgnoreCase)) });
        rpc.On("power.setActive", c =>
        {
            var id = Guid.Parse(c.Str("id"));
            var plan = PowerPlans.List().FirstOrDefault(p => p.Id == id) ?? throw RpcException.NotFound("Unknown power plan");
            using var scope = _journal.Begin("tweaks", Loc.T("Güç planı: {0}", "Power plan: {0}").F(plan.Name));
            var entry = scope.PowerScheme(id);
            if (entry.Error is not null) throw RpcException.Failed(entry.Error);
            return new { simulated = scope.DryRun };
        });
        rpc.On("power.unlockUltimate", _ =>
        {
            var before = PowerPlans.List().Select(p => p.Id).ToHashSet();
            if (_journal.DryRun) return new { simulated = true, created = (string?)null };
            var r = Proc.Run(Proc.Sys("powercfg.exe"), "-duplicatescheme " + PowerPlans.UltimateTemplate);
            if (!r.Ok) throw RpcException.Failed(r.Text);
            var created = PowerPlans.List().FirstOrDefault(p => !before.Contains(p.Id));
            using var scope = _journal.Begin("tweaks", Loc.T("Nihai Performans güç planı eklendi", "Ultimate Performance power plan added"));
            scope.Note("power.plan.created", created?.Name ?? "Ultimate Performance", new() { ["guid"] = created?.Id.ToString() });
            return new { simulated = false, created = created?.Id.ToString() };
        });
    }
}
