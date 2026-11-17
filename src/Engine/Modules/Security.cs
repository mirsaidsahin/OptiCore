using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using OptiCore.Engine.Core;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class SecCheck
{
    public string Id { get; set; } = "";
    /// <summary>protection | system | accounts | network</summary>
    public string Group { get; set; } = "system";
    public LText Title { get; set; }
    /// <summary>pass | info | warn | fail | unknown</summary>
    public string Status { get; set; } = "unknown";
    public LText Detail { get; set; }
    public LText? Advice { get; set; }
    /// <summary>A Windows settings page where the user can fix it. OptiCore never changes security settings itself.</summary>
    public string? Link { get; set; }
    public int Penalty { get; set; }
}

/// <summary>Runs COM objects that need a single-threaded apartment (the Shell automation objects).</summary>
public static class Sta
{
    public static T? Run<T>(Func<T> work, int timeoutMs = 8000)
    {
        T? result = default;
        var thread = new Thread(() => { try { result = work(); } catch { /* caller gets default */ } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(timeoutMs);
        return result;
    }
}

/// <summary>
/// Security and privacy audit. It only reads and reports: every remedy is a link to the Windows page
/// where the user makes the change, because a tool should not flip security switches behind your back.
/// </summary>
public sealed class SecurityService
{
    private readonly ProcessSampler _processes;
    private readonly StartupService _startup;
    private readonly SystemInfoService _system;
    private List<SecCheck>? _cache;
    private long _cacheAt;
    private readonly object _gate = new();

    public SecurityService(ProcessSampler processes, StartupService startup, SystemInfoService system)
    {
        _processes = processes; _startup = startup; _system = system;
    }

    private static SecCheck Check(string id, string group, LText title, string status, LText detail, int penalty = 0, LText? advice = null, string? link = null) =>
        new() { Id = id, Group = group, Title = title, Status = status, Detail = detail, Penalty = status is "fail" or "warn" ? penalty : 0, Advice = advice, Link = link };

    // ------------------------------------------------------------------ checks
    public List<SecCheck> Checks(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _cache is not null && Environment.TickCount64 - _cacheAt < 60000) return _cache;
            var list = new List<SecCheck>();
            foreach (var run in new Action<List<SecCheck>>[] { Antivirus, Firewall, Uac, SmartScreen, Updates, EndOfLife, Boot, Encryption, MemoryIntegrity, RemoteAccess, Accounts, Proxy, HostsCheck })
            {
                try { run(list); } catch (Exception e) { Log.Warn("security: check failed", e); }
            }
            _cache = list;
            _cacheAt = Environment.TickCount64;
            return list;
        }
    }

    private static void Antivirus(List<SecCheck> list)
    {
        const string link = "windowsdefender://threat";
        var products = Wmi.Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2");
        var active = products.Where(p => (p.L("productState") & 0x1000) != 0).Select(p => p.S("displayName")).Distinct().ToList();
        var outdated = products.Where(p => (p.L("productState") & 0x1000) != 0 && (p.L("productState") & 0x10) != 0).Select(p => p.S("displayName")).ToList();

        if (active.Count == 0)
            list.Add(Check("antivirus", "protection", Loc.T("Virüsten koruma", "Antivirus protection"), "fail",
                Loc.T("Etkin bir virüsten koruma yazılımı bulunamadı. Bu bilgisayar zararlı yazılımlara karşı korumasız.", "No active antivirus was found. This PC is unprotected against malware."), 40,
                Loc.T("Windows Güvenliği'nde gerçek zamanlı korumayı açın.", "Turn on real-time protection in Windows Security."), link));
        else
            list.Add(Check("antivirus", "protection", Loc.T("Virüsten koruma", "Antivirus protection"), "pass",
                Loc.T("Etkin: {0}", "Active: {0}").F(string.Join(", ", active))));

        if (outdated.Count > 0)
            list.Add(Check("antivirus.signatures", "protection", Loc.T("Virüs tanımları", "Virus definitions"), "warn",
                Loc.T("{0} tanımlarının güncel olmadığını bildiriyor.", "{0} reports that its definitions are out of date.").F(string.Join(", ", outdated)), 12,
                Loc.T("Koruma güncelleştirmelerini denetleyin.", "Check for protection updates."), link));

        var defender = Wmi.Query("SELECT RealTimeProtectionEnabled, AntivirusEnabled, AntivirusSignatureAge, IsTamperProtected, QuickScanAge, AMRunningMode FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender").FirstOrDefault();
        if (defender is null || !defender.B("AntivirusEnabled")) return;

        if (!defender.B("RealTimeProtectionEnabled"))
            list.Add(Check("defender.realtime", "protection", Loc.T("Gerçek zamanlı koruma", "Real-time protection"), "fail",
                Loc.T("Microsoft Defender kurulu ama gerçek zamanlı koruma kapalı: dosyalar açılırken taranmıyor.", "Microsoft Defender is installed but real-time protection is off: files are not scanned as they open."), 30,
                Loc.T("Windows Güvenliği'nde gerçek zamanlı korumayı açın.", "Turn on real-time protection in Windows Security."), link));

        long age = defender.L("AntivirusSignatureAge");
        list.Add(age > 7
            ? Check("defender.signatures", "protection", Loc.T("Defender tanımları", "Defender definitions"), age > 30 ? "fail" : "warn",
                Loc.T("Virüs tanımları {0} gündür güncellenmedi.", "Virus definitions have not been updated for {0} days.").F(age), age > 30 ? 20 : 10,
                Loc.T("Windows Update'i çalıştırın.", "Run Windows Update."), "ms-settings:windowsupdate")
            : Check("defender.signatures", "protection", Loc.T("Defender tanımları", "Defender definitions"), "pass",
                age == 0 ? Loc.T("Bugün güncellendi.", "Updated today.") : Loc.T("{0} gün önce güncellendi.", "Updated {0} days ago.").F(age)));

        list.Add(defender.B("IsTamperProtected")
            ? Check("defender.tamper", "protection", Loc.T("Kurcalama koruması", "Tamper protection"), "pass", Loc.T("Açık: zararlı yazılımlar Defender ayarlarını değiştiremez.", "On: malware cannot change Defender's settings."))
            : Check("defender.tamper", "protection", Loc.T("Kurcalama koruması", "Tamper protection"), "warn",
                Loc.T("Kapalı: bir zararlı yazılım Defender'ı sessizce devre dışı bırakabilir.", "Off: malware could silently disable Defender."), 6, Loc.T("Windows Güvenliği'nde açın.", "Turn it on in Windows Security."), link));

        long scan = defender.L("QuickScanAge");
        if (scan > 45)
            list.Add(Check("defender.scan", "protection", Loc.T("Son tarama", "Last scan"), "info",
                scan > 100000 ? Loc.T("Henüz hiç hızlı tarama yapılmamış.", "No quick scan has ever run.") : Loc.T("Son hızlı tarama {0} gün önce yapıldı.", "The last quick scan ran {0} days ago.").F(scan),
                0, Loc.T("Bir hızlı tarama başlatın.", "Start a quick scan."), link));
    }

    private static void Firewall(List<SecCheck> list)
    {
        const string root = @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
        var off = new List<LText>();
        if (Reg.GetInt(root + "PublicProfile", "EnableFirewall") == 0) off.Add(Loc.T("Ortak ağ", "Public network"));
        if (Reg.GetInt(root + "StandardProfile", "EnableFirewall") == 0) off.Add(Loc.T("Özel ağ", "Private network"));
        if (Reg.GetInt(root + "DomainProfile", "EnableFirewall") == 0) off.Add(Loc.T("Etki alanı ağı", "Domain network"));
        var third = Wmi.Query("SELECT displayName, productState FROM FirewallProduct", @"root\SecurityCenter2")
            .Where(p => (p.L("productState") & 0x1000) != 0).Select(p => p.S("displayName")).ToList();

        if (off.Count == 0)
            list.Add(Check("firewall", "protection", Loc.T("Güvenlik duvarı", "Firewall"), "pass", Loc.T("Windows Güvenlik Duvarı tüm ağ türlerinde açık.", "Windows Firewall is on for every network type.")));
        else if (third.Count > 0)
            list.Add(Check("firewall", "protection", Loc.T("Güvenlik duvarı", "Firewall"), "pass", Loc.T("Başka bir güvenlik duvarı etkin: {0}", "Another firewall is active: {0}").F(string.Join(", ", third))));
        else
            list.Add(Check("firewall", "protection", Loc.T("Güvenlik duvarı", "Firewall"), "fail",
                Loc.T("Windows Güvenlik Duvarı şu ağ türlerinde kapalı: {0}. Ağdaki diğer cihazlar bu bilgisayarın açık bağlantı noktalarına doğrudan ulaşabilir.",
                      "Windows Firewall is off for: {0}. Other devices on the network can reach this PC's open ports directly.").F(string.Join(", ", off.Select(o => o.ToString()))), 30,
                Loc.T("Windows Güvenliği'nde güvenlik duvarını açın.", "Turn the firewall on in Windows Security."), "windowsdefender://network"));
    }

    private static void Uac(List<SecCheck> list)
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        if (Reg.GetInt(key, "EnableLUA") == 0)
            list.Add(Check("uac", "system", Loc.T("Kullanıcı Hesabı Denetimi (UAC)", "User Account Control (UAC)"), "fail",
                Loc.T("UAC tamamen kapalı: çalıştırdığınız her program sormadan tam yönetici yetkisi alır.", "UAC is completely off: every program you run gets full administrator rights without asking."), 25,
                Loc.T("Denetim Masası'nda UAC'yi varsayılan düzeye getirin.", "Set UAC back to its default level in Control Panel."), "ms-settings:signinoptions"));
        else if (Reg.GetInt(key, "ConsentPromptBehaviorAdmin") == 0)
            list.Add(Check("uac", "system", Loc.T("Kullanıcı Hesabı Denetimi (UAC)", "User Account Control (UAC)"), "warn",
                Loc.T("UAC \"hiçbir zaman bildirme\" düzeyinde: programlar yönetici yetkisini onayınız olmadan alır.", "UAC is set to \"never notify\": programs get administrator rights without your approval."), 12,
                Loc.T("UAC'yi varsayılan düzeye getirin.", "Set UAC back to its default level.")));
        else
            list.Add(Check("uac", "system", Loc.T("Kullanıcı Hesabı Denetimi (UAC)", "User Account Control (UAC)"), "pass", Loc.T("Açık: yönetici yetkisi isteyen programlar onayınızı bekler.", "On: programs asking for administrator rights wait for your approval.")));
    }

    private static void SmartScreen(List<SecCheck> list)
    {
        string? value = Reg.GetStr(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled");
        list.Add(string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase)
            ? Check("smartscreen", "protection", "SmartScreen", "warn",
                Loc.T("Kapalı: internetten indirilen tanınmayan programlar uyarı vermeden çalışır.", "Off: unrecognized programs downloaded from the internet run without a warning."), 8,
                Loc.T("Windows Güvenliği > Uygulama ve tarayıcı denetimi'nden açın.", "Turn it on under Windows Security > App & browser control."), "windowsdefender://appbrowser")
            : Check("smartscreen", "protection", "SmartScreen", "pass", Loc.T("Açık: tanınmayan indirmeler çalışmadan önce uyarı verir.", "On: unrecognized downloads trigger a warning before they run.")));
    }

    public sealed record UpdateEntry(DateTime Date, int Result, string Title);

    /// <summary>Windows Update's installation history (newest first). Result: 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted.</summary>
    public static List<UpdateEntry>? UpdateHistory(int max = 150) => Sta.Run(() =>
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        dynamic searcher = session.CreateUpdateSearcher();
        int total = (int)searcher.GetTotalHistoryCount();
        var entries = new List<UpdateEntry>();
        if (total == 0) return entries;
        dynamic history = searcher.QueryHistory(0, Math.Min(total, max));
        int count = (int)history.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic h = history.Item(i);
            if ((int)h.Operation != 1) continue; // installations only
            entries.Add(new UpdateEntry((DateTime)h.Date, (int)h.ResultCode, (string)(h.Title ?? "")));
        }
        return entries;
    }, 25000);

    private static readonly System.Text.RegularExpressions.Regex QualityKb = new(@"KB5\d{6}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex StoreApp = new(@"^9[A-Z0-9]{11}-", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void Updates(List<SecCheck> list)
    {
        // Failed retries can fill hundreds of history rows, so look far enough back to find the last success.
        var history = UpdateHistory(600);
        if (history is null || history.Count == 0)
        {
            list.Add(Check("updates", "system", Loc.T("Windows güncellemeleri", "Windows updates"), "unknown", Loc.T("Güncelleme geçmişi okunamadı.", "The update history could not be read.")));
            return;
        }
        // Windows quality and security updates all carry a KB5xxxxxx number, whatever the display language.
        var ok = history.Where(h => h.Result is 2 or 3).ToList();
        DateTime? last = ok.Where(h => QualityKb.IsMatch(h.Title)).Select(h => (DateTime?)h.Date).Max()
            ?? ok.Where(h => !h.Title.Contains("KB2267602") && !h.Title.Contains("KB4052623") && !h.Title.Contains("KB890830") && !StoreApp.IsMatch(h.Title)).Select(h => (DateTime?)h.Date).Max();
        // A fresh installation ships with the updates of its image: count the install date as the last update.
        if (_system.Hardware().Os.InstallDate is DateTime installed && (last is null || installed > last)) last = installed;

        // Updates that keep failing: failed recently and never succeeded afterwards.
        var failing = history.Where(h => h.Result is 4 or 5 && (DateTime.UtcNow - h.Date).TotalDays <= 10 && !StoreApp.IsMatch(h.Title))
            .GroupBy(h => h.Title).Where(g => !ok.Any(o => o.Title == g.Key && o.Date > g.Max(x => x.Date)))
            .Select(g => (Title: g.Key, Attempts: g.Count())).OrderByDescending(g => g.Attempts).ToList();
        if (failing.Count >= 2 || failing.Any(f => f.Attempts >= 3))
            list.Add(Check("updates.failing", "system", Loc.T("Kurulamayan güncellemeler", "Updates that fail to install"), "warn",
                Loc.T("Son 10 günde {0} güncelleme kurulamadı ({1} deneme). En çok denenen: {2}. Tekrarlayan hatalar genellikle dolu disk, bozuk güncelleme önbelleği ya da devre dışı bırakılmış bir Windows Update hizmetinden kaynaklanır.",
                      "{0} updates failed to install in the last 10 days ({1} attempts). Most retried: {2}. Repeated failures usually come from a full disk, a corrupt update cache or a disabled Windows Update service.")
                    .F(failing.Count, failing.Sum(f => f.Attempts), string.Join("; ", failing.Take(3).Select(f => (f.Title.Length > 60 ? f.Title[..60] + "…" : f.Title) + $" (×{f.Attempts})"))), 8,
                Loc.T("Ayarlar > Sistem > Sorun gider > Windows Update sorun gidericisini çalıştırın.", "Run Settings > System > Troubleshoot > Windows Update troubleshooter."), "ms-settings:troubleshoot"));

        if (last is null)
        {
            list.Add(Check("updates", "system", Loc.T("Windows güncellemeleri", "Windows updates"), "warn",
                Loc.T("Geçmişte başarıyla kurulmuş bir Windows kalite güncellemesi görünmüyor.", "The history shows no successfully installed Windows quality update."), 10,
                Loc.T("Windows Update'i açıp güncellemeleri denetleyin.", "Open Windows Update and check for updates."), "ms-settings:windowsupdate"));
            return;
        }
        double days = (DateTime.UtcNow - last.Value).TotalDays;
        list.Add(days > 45
            ? Check("updates", "system", Loc.T("Windows güncellemeleri", "Windows updates"), days > 90 ? "fail" : "warn",
                Loc.T("Son güncelleme {0:0} gün önce kuruldu. Microsoft her ay güvenlik düzeltmesi yayımlar; aradaki açıklar bu bilgisayarda kapatılmamış olabilir.",
                      "The last update was installed {0:0} days ago. Microsoft ships security fixes every month; the ones in between may not be applied on this PC.").F(days), days > 90 ? 25 : 12,
                Loc.T("Windows Update'i açıp güncellemeleri kurun.", "Open Windows Update and install the updates."), "ms-settings:windowsupdate")
            : Check("updates", "system", Loc.T("Windows güncellemeleri", "Windows updates"), "pass", Loc.T("Son güncelleme {0:0} gün önce kuruldu.", "The last update was installed {0:0} days ago.").F(days)));
    }

    private void EndOfLife(List<SecCheck> list)
    {
        var os = _system.Hardware().Os;
        if (os.Edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || os.Edition.Contains("Education", StringComparison.OrdinalIgnoreCase) || os.Edition.Contains("IoT", StringComparison.OrdinalIgnoreCase)) return;
        // End of servicing for Home and Pro editions, per Microsoft's lifecycle pages.
        DateTime? end = os.BuildNumber switch
        {
            < 19045 => new DateTime(2023, 6, 13),
            19045 => new DateTime(2025, 10, 14),
            22000 => new DateTime(2023, 10, 10),
            22621 => new DateTime(2024, 10, 8),
            22631 => new DateTime(2025, 11, 11),
            26100 => new DateTime(2026, 10, 13),
            26200 => new DateTime(2027, 10, 12),
            _ => null,
        };
        if (end is null) return;
        double left = (end.Value - DateTime.UtcNow).TotalDays;
        if (left < 0)
            list.Add(Check("eol", "system", Loc.T("Windows sürümü desteği", "Windows version support"), "fail",
                Loc.T("{0} {1} sürümünün desteği {2:dd.MM.yyyy} tarihinde bitti: artık güvenlik güncellemesi almıyor.", "Support for {0} {1} ended on {2:yyyy-MM-dd}: it no longer receives security updates.").F(os.Name, os.Version, end.Value), 25,
                Loc.T("Windows Update üzerinden daha yeni bir sürüme yükseltin.", "Upgrade to a newer version through Windows Update."), "ms-settings:windowsupdate"));
        else if (left < 120)
            list.Add(Check("eol", "system", Loc.T("Windows sürümü desteği", "Windows version support"), "warn",
                Loc.T("{0} {1} sürümünün desteği {2:0} gün sonra bitiyor.", "Support for {0} {1} ends in {2:0} days.").F(os.Name, os.Version, left), 5,
                Loc.T("Yeni sürüm sunulduğunda yükseltin.", "Upgrade when the new version is offered."), "ms-settings:windowsupdate"));
        else
            list.Add(Check("eol", "system", Loc.T("Windows sürümü desteği", "Windows version support"), "pass",
                Loc.T("{0} {1}, {2:dd.MM.yyyy} tarihine kadar güvenlik güncellemesi alacak.", "{0} {1} receives security updates until {2:yyyy-MM-dd}.").F(os.Name, os.Version, end.Value)));
    }

    private void Boot(List<SecCheck> list)
    {
        var os = _system.Hardware().Os;
        list.Add(os.SecureBoot switch
        {
            true => Check("secureboot", "system", Loc.T("Güvenli Önyükleme", "Secure Boot"), "pass", Loc.T("Açık: Windows başlamadan önce yalnızca imzalı yazılım çalışabilir.", "On: only signed software can run before Windows starts.")),
            false => Check("secureboot", "system", Loc.T("Güvenli Önyükleme", "Secure Boot"), "warn",
                Loc.T("Kapalı: önyükleme aşamasına yerleşen zararlılar (bootkit) engellenmiyor. Bazı oyunların hile koruması da bunu şart koşar.", "Off: malware that hides in the boot stage (bootkits) is not blocked. Some games' anti-cheat also requires it."), 6,
                Loc.T("UEFI/BIOS ayarlarından açın.", "Enable it in the UEFI/BIOS settings.")),
            _ => Check("secureboot", "system", Loc.T("Güvenli Önyükleme", "Secure Boot"), "info", Loc.T("Bu bilgisayar eski BIOS modunda başlıyor; Güvenli Önyükleme kullanılamıyor.", "This PC boots in legacy BIOS mode; Secure Boot is not available.")),
        });
        list.Add(os.Tpm is not null
            ? Check("tpm", "system", "TPM", "pass", Loc.T("Güvenlik yongası mevcut (TPM {0}).", "A security chip is present (TPM {0}).").F(os.Tpm))
            : Check("tpm", "system", "TPM", "info", Loc.T("Etkin bir TPM bulunamadı. Disk şifreleme ve Windows Hello bundan yararlanır.", "No active TPM was found. Disk encryption and Windows Hello rely on it.")));
    }

    private void Encryption(List<SecCheck> list)
    {
        int? state = Sta.Run<int?>(() =>
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            object? v = shell.NameSpace(Paths.SystemDrive).Self.ExtendedProperty("System.Volume.BitLockerProtection");
            return v is null ? null : Convert.ToInt32(v);
        });
        bool laptop = _system.Hardware().Computer.Type == "laptop";
        if (state is 1 or 3)
            list.Add(Check("encryption", "system", Loc.T("Disk şifreleme", "Disk encryption"), "pass", Loc.T("Sistem sürücüsü şifreli.", "The system drive is encrypted.")));
        else if (state is 5)
            list.Add(Check("encryption", "system", Loc.T("Disk şifreleme", "Disk encryption"), "warn", Loc.T("Şifreleme askıya alınmış: disk şu an korumasız.", "Encryption is suspended: the disk is unprotected right now."), 6, null, "ms-settings:deviceencryption"));
        else if (state is 2 or 0)
            list.Add(Check("encryption", "system", Loc.T("Disk şifreleme", "Disk encryption"), laptop ? "warn" : "info",
                laptop
                    ? Loc.T("Sistem sürücüsü şifreli değil. Dizüstü kaybolur ya da çalınırsa içindeki tüm dosyalar başka bir bilgisayarda okunabilir.", "The system drive is not encrypted. If the laptop is lost or stolen, every file on it can be read on another computer.")
                    : Loc.T("Sistem sürücüsü şifreli değil.", "The system drive is not encrypted."), 6,
                Loc.T("Ayarlar > Gizlilik ve güvenlik > Cihaz şifreleme.", "Settings > Privacy & security > Device encryption."), "ms-settings:deviceencryption"));
    }

    private static void MemoryIntegrity(List<SecCheck> list)
    {
        bool on = Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") == 1;
        list.Add(on
            ? Check("hvci", "system", Loc.T("Bellek bütünlüğü", "Memory integrity"), "pass", Loc.T("Açık: çekirdeğe imzasız kod yüklenmesi engelleniyor.", "On: unsigned code is blocked from loading into the kernel."))
            : Check("hvci", "system", Loc.T("Bellek bütünlüğü", "Memory integrity"), "info",
                Loc.T("Kapalı. Açmak çekirdek düzeyindeki saldırılara karşı korur; eski sürücülerle uyumsuz olabilir ve bazı oyunlarda birkaç puanlık kare hızı kaybına yol açar.",
                      "Off. Turning it on protects against kernel-level attacks; it can be incompatible with old drivers and costs a few percent of frame rate in some games."),
                0, null, "windowsdefender://coreisolation"));
    }

    private static void RemoteAccess(List<SecCheck> list)
    {
        bool rdp = Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") == 0;
        if (rdp)
        {
            bool nla = Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication") != 0;
            list.Add(Check("rdp", "network", Loc.T("Uzak Masaüstü", "Remote Desktop"), "warn",
                nla ? Loc.T("Açık. Kullanmıyorsanız kapatın: internete açık RDP, fidye yazılımlarının en sık kullandığı giriş kapısıdır.", "On. Turn it off if unused: RDP exposed to the internet is the most common door for ransomware.")
                    : Loc.T("Açık ve Ağ Düzeyinde Kimlik Doğrulama kapalı: bu, parola deneme saldırılarını kolaylaştırır.", "On, with Network Level Authentication off: that makes password-guessing attacks easier."),
                nla ? 5 : 12, Loc.T("Ayarlar > Sistem > Uzak Masaüstü.", "Settings > System > Remote Desktop."), "ms-settings:remotedesktop"));
        }
        else list.Add(Check("rdp", "network", Loc.T("Uzak Masaüstü", "Remote Desktop"), "pass", Loc.T("Kapalı.", "Off.")));

        if (Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1") == 1
            || Wmi.Query("SELECT InstallState FROM Win32_OptionalFeature WHERE Name = 'SMB1Protocol'").Any(r => r.I("InstallState") == 1))
            list.Add(Check("smb1", "network", "SMB 1.0", "fail",
                Loc.T("Eski SMB 1.0 protokolü açık. WannaCry gibi solucanların yayılmak için kullandığı, Microsoft'un yıllar önce terk ettiği bir protokoldür.", "The legacy SMB 1.0 protocol is enabled. It is the protocol worms such as WannaCry spread through, and Microsoft abandoned it years ago."), 15,
                Loc.T("Windows özelliklerinden \"SMB 1.0/CIFS Dosya Paylaşım Desteği\"ni kaldırın.", "Remove \"SMB 1.0/CIFS File Sharing Support\" in Windows features.")));
    }

    private static void Accounts(List<SecCheck> list)
    {
        if (Wmi.Query("SELECT Disabled FROM Win32_UserAccount WHERE LocalAccount = TRUE AND SID LIKE 'S-1-5-21-%-501'").Any(r => !r.B("Disabled")))
            list.Add(Check("guest", "accounts", Loc.T("Konuk hesabı", "Guest account"), "warn", Loc.T("Parolasız Konuk hesabı etkin.", "The password-less Guest account is enabled."), 8,
                Loc.T("Kullanmıyorsanız devre dışı bırakın.", "Disable it if unused.")));

        const string winlogon = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        if (Reg.GetStr(winlogon, "AutoAdminLogon") == "1")
            list.Add(Reg.GetStr(winlogon, "DefaultPassword") is { Length: > 0 }
                ? Check("autologon", "accounts", Loc.T("Otomatik oturum açma", "Automatic sign-in"), "fail",
                    Loc.T("Otomatik oturum açma etkin ve hesap parolanız kayıt defterinde düz metin olarak duruyor: bu bilgisayardaki her program onu okuyabilir.", "Automatic sign-in is on and your account password sits in the registry as plain text: any program on this PC can read it."), 18,
                    Loc.T("\"netplwiz\" ile otomatik oturum açmayı kapatın.", "Turn automatic sign-in off with \"netplwiz\"."))
                : Check("autologon", "accounts", Loc.T("Otomatik oturum açma", "Automatic sign-in"), "warn",
                    Loc.T("Bilgisayar açıldığında parola sormadan oturum açılıyor.", "The PC signs in without asking for a password when it starts."), 6));

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool admin = identity.Groups?.Any(g => g.Value == "S-1-5-32-544") ?? false;
            list.Add(Check("account.type", "accounts", Loc.T("Hesap türü", "Account type"), admin ? "info" : "pass",
                admin ? Loc.T("Günlük kullandığınız hesap bir yönetici hesabı. UAC açık olduğu sürece bu kabul edilebilir; en güvenlisi günlük işler için standart hesap kullanmaktır.",
                              "The account you use every day is an administrator. With UAC on that is acceptable; safest is a standard account for daily work.")
                      : Loc.T("Standart kullanıcı hesabı kullanıyorsunuz.", "You are using a standard user account.")));
        }
        catch { /* identity unavailable */ }
    }

    private static void Proxy(List<SecCheck> list)
    {
        const string key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        string? server = Reg.GetInt(key, "ProxyEnable") == 1 ? Reg.GetStr(key, "ProxyServer") : null;
        string? script = Reg.GetStr(key, "AutoConfigURL");
        if (!string.IsNullOrEmpty(server) || !string.IsNullOrEmpty(script))
            list.Add(Check("proxy", "network", Loc.T("Vekil sunucu (proxy)", "Proxy server"), "warn",
                Loc.T("Sistem genelinde bir vekil sunucu ayarlı: {0}. Bunu siz ya da iş yeriniz ayarlamadıysa internet trafiğiniz bilmediğiniz bir sunucudan geçiyor olabilir.",
                      "A system-wide proxy is configured: {0}. If neither you nor your workplace set it, your internet traffic may be passing through a server you do not know.").F(server ?? script!), 8,
                Loc.T("Ayarlar > Ağ ve İnternet > Ara sunucu.", "Settings > Network & internet > Proxy."), "ms-settings:network-proxy"));
        else
            list.Add(Check("proxy", "network", Loc.T("Vekil sunucu (proxy)", "Proxy server"), "pass", Loc.T("Trafiği başka bir sunucuya yönlendiren bir ayar yok.", "No setting redirects traffic through another server.")));
    }

    // ------------------------------------------------------------------ hosts file
    private static readonly string[] SecurityDomains = ["microsoft.com", "windowsupdate.com", "windows.com", "defender", "virustotal", "kaspersky", "avast", "avg.com", "eset", "bitdefender", "malwarebytes", "norton", "mcafee", "sophos", "trendmicro", "symantec"];
    private static readonly string[] SensitiveDomains = ["google.", "facebook.", "paypal.", "apple.com", "amazon.", "microsoft.", "live.com", "outlook.", "bank", "garanti", "isbank", "akbank", "yapikredi", "ziraat", "halkbank", "vakifbank", "edevlet", "turkiye.gov.tr", "binance", "steam"];

    public static object Hosts()
    {
        string path = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
        var redirects = new List<object>();
        var blockedSecurity = new List<object>();
        int entries = 0, blocks = 0;
        DateTime? modified = null;
        try
        {
            modified = File.GetLastWriteTimeUtc(path);
            foreach (var raw in File.ReadLines(path))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var ip)) continue;
                bool sink = IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);
                foreach (var host in parts.Skip(1))
                {
                    entries++;
                    string h = host.ToLowerInvariant();
                    if (h is "localhost" or "localhost.localdomain" or "broadcasthost") continue;
                    if (sink)
                    {
                        blocks++;
                        if (SecurityDomains.Any(h.Contains)) blockedSecurity.Add(new { host, ip = parts[0] });
                    }
                    else if (SensitiveDomains.Any(h.Contains)) redirects.Add(new { host, ip = parts[0] });
                }
            }
        }
        catch (Exception e) { return new { path, error = e.Message, entries = 0, blocks = 0, redirects, blockedSecurity, modified }; }
        return new { path, error = (string?)null, entries, blocks, redirects, blockedSecurity, modified };
    }

    private static void HostsCheck(List<SecCheck> list)
    {
        dynamic h = Hosts();
        int redirects = ((System.Collections.ICollection)h.redirects).Count, blocked = ((System.Collections.ICollection)h.blockedSecurity).Count;
        if (redirects > 0)
            list.Add(Check("hosts", "network", Loc.T("Hosts dosyası", "Hosts file"), "fail",
                Loc.T("Hosts dosyası {0} bilinen siteyi başka bir adrese yönlendiriyor. Bu, sahte giriş sayfaları göstermek için kullanılan bir yöntemdir.", "The hosts file redirects {0} well-known site(s) to another address. This is a technique used to show fake sign-in pages.").F(redirects), 25,
                Loc.T("Aşağıdaki \"Hosts dosyası\" bölümünden girdileri inceleyin.", "Review the entries in the \"Hosts file\" section below.")));
        else if (blocked > 0)
            list.Add(Check("hosts", "network", Loc.T("Hosts dosyası", "Hosts file"), "warn",
                Loc.T("Hosts dosyası {0} güvenlik ya da güncelleme adresini engelliyor. Bazı zararlılar virüsten koruma güncellemelerini bu yolla keser; bazı \"telemetri engelleme\" araçları da aynısını yapar.",
                      "The hosts file blocks {0} security or update address(es). Some malware cuts off antivirus updates this way; some \"telemetry blocking\" tools do the same.").F(blocked), 8));
        else
            list.Add(Check("hosts", "network", Loc.T("Hosts dosyası", "Hosts file"), "pass",
                (int)h.blocks > 0 ? Loc.T("Şüpheli yönlendirme yok ({0} engelleme girdisi var).", "No suspicious redirects ({0} blocking entries present).").F((int)h.blocks) : Loc.T("Temiz: özel bir yönlendirme yok.", "Clean: no custom redirects.")));
    }

    // ------------------------------------------------------------------ ports
    private static readonly Dictionary<int, (LText Name, string Risk)> KnownPorts = new()
    {
        [21] = (Loc.T("FTP sunucusu (şifrelenmemiş dosya aktarımı)", "FTP server (unencrypted file transfer)"), "high"),
        [22] = (Loc.T("SSH sunucusu", "SSH server"), "medium"),
        [23] = (Loc.T("Telnet (şifrelenmemiş uzak erişim)", "Telnet (unencrypted remote access)"), "high"),
        [53] = (Loc.T("DNS sunucusu (internet paylaşımı)", "DNS server (internet sharing)"), "medium"),
        [68] = (Loc.T("DHCP istemcisi", "DHCP client"), "low"),
        [80] = (Loc.T("Web sunucusu (HTTP)", "Web server (HTTP)"), "medium"),
        [123] = (Loc.T("Windows Saat eşitlemesi (NTP)", "Windows Time sync (NTP)"), "low"),
        [500] = (Loc.T("IPsec anahtar değişimi (IKE)", "IPsec key exchange (IKE)"), "low"),
        [546] = (Loc.T("DHCPv6 istemcisi", "DHCPv6 client"), "low"),
        [2177] = (Loc.T("qWAVE (ağ kalite ölçümü)", "qWAVE (network quality measurement)"), "low"),
        [3540] = (Loc.T("Eşler arası ad çözümleme (PNRP)", "Peer name resolution (PNRP)"), "low"),
        [4500] = (Loc.T("IPsec NAT geçişi", "IPsec NAT traversal"), "low"),
        [5050] = (Loc.T("Bağlı Cihazlar Platformu", "Connected Devices Platform"), "low"),
        [135] = (Loc.T("Windows RPC uç nokta eşleyicisi", "Windows RPC endpoint mapper"), "low"),
        [137] = (Loc.T("NetBIOS ad hizmeti", "NetBIOS name service"), "low"),
        [138] = (Loc.T("NetBIOS veri birimi", "NetBIOS datagram"), "low"),
        [139] = (Loc.T("NetBIOS oturumu (eski dosya paylaşımı)", "NetBIOS session (legacy file sharing)"), "medium"),
        [443] = (Loc.T("Web sunucusu (HTTPS)", "Web server (HTTPS)"), "medium"),
        [445] = (Loc.T("SMB dosya ve yazıcı paylaşımı", "SMB file and printer sharing"), "medium"),
        [1433] = (Loc.T("Microsoft SQL Server", "Microsoft SQL Server"), "high"),
        [1900] = (Loc.T("SSDP (UPnP cihaz keşfi)", "SSDP (UPnP device discovery)"), "low"),
        [2869] = (Loc.T("UPnP olay bildirimi", "UPnP event notification"), "low"),
        [3306] = (Loc.T("MySQL veritabanı", "MySQL database"), "high"),
        [3389] = (Loc.T("Uzak Masaüstü (RDP)", "Remote Desktop (RDP)"), "high"),
        [3702] = (Loc.T("WS-Discovery (ağ cihazı keşfi)", "WS-Discovery (network device discovery)"), "low"),
        [5040] = (Loc.T("Bağlı Cihazlar Platformu", "Connected Devices Platform"), "low"),
        [5353] = (Loc.T("mDNS (yerel ad çözümleme)", "mDNS (local name resolution)"), "low"),
        [5355] = (Loc.T("LLMNR (yerel ad çözümleme)", "LLMNR (local name resolution)"), "low"),
        [5357] = (Loc.T("Web Hizmetleri keşfi (WSD)", "Web Services discovery (WSD)"), "low"),
        [5432] = (Loc.T("PostgreSQL veritabanı", "PostgreSQL database"), "high"),
        [5900] = (Loc.T("VNC uzak masaüstü", "VNC remote desktop"), "high"),
        [5985] = (Loc.T("Windows Uzaktan Yönetim (WinRM)", "Windows Remote Management (WinRM)"), "medium"),
        [6379] = (Loc.T("Redis veritabanı", "Redis database"), "high"),
        [7680] = (Loc.T("Teslim İyileştirme (güncelleme paylaşımı)", "Delivery Optimization (update sharing)"), "low"),
        [8080] = (Loc.T("Web sunucusu (alternatif)", "Web server (alternate)"), "medium"),
        [9200] = (Loc.T("Elasticsearch", "Elasticsearch"), "high"),
        [27017] = (Loc.T("MongoDB veritabanı", "MongoDB database"), "high"),
    };

    private object Ports()
    {
        var procs = _processes.Snapshot(5000).ToDictionary(p => p.Pid);
        var listening = ConnectionTable.Snapshot().Where(c => c.State == "listen").ToList();
        var groups = listening.GroupBy(c => (Protocol: c.Protocol.TrimEnd('6'), c.LocalPort, c.Pid)).Select(g =>
        {
            bool exposed = g.Any(c => !IPAddress.TryParse(c.LocalAddress, out var ip) || !IPAddress.IsLoopback(ip));
            procs.TryGetValue(g.Key.Pid, out var p);
            KnownPorts.TryGetValue(g.Key.LocalPort, out var known);
            string risk = !exposed ? "none" : known.Risk ?? (g.Key.LocalPort >= 49152 ? "low" : "unknown");
            return new
            {
                protocol = g.Key.Protocol, port = g.Key.LocalPort, pid = g.Key.Pid, process = p?.Name ?? "?", path = p?.Path, description = p?.Description,
                services = p?.Services, addresses = g.Select(c => c.LocalAddress).Distinct().ToList(), exposed, risk,
                name = known.Name.Tr is null ? (LText?)null : known.Name,
            };
        }).OrderByDescending(x => x.risk switch { "high" => 4, "medium" => 3, "unknown" => 2, "low" => 1, _ => 0 }).ThenBy(x => x.port).ToList();
        return new
        {
            items = groups, total = groups.Count, exposed = groups.Count(g => g.exposed),
            high = groups.Count(g => g.risk == "high"), firewall = Checks().FirstOrDefault(c => c.Id == "firewall")?.Status == "pass",
        };
    }

    // ------------------------------------------------------------------ suspicious items
    private object Suspicious()
    {
        string win = Paths.Windows, pf = Paths.ProgramFiles, pf86 = Paths.ProgramFilesX86;
        var online = ConnectionTable.Snapshot().Where(c => c.State == "established" && c.RemoteAddress is not null && IPAddress.TryParse(c.RemoteAddress, out var ip) && !IPAddress.IsLoopback(ip))
            .Select(c => c.Pid).ToHashSet();
        var processes = new List<object>();
        foreach (var g in _processes.Snapshot(5000).Where(p => p.Path is not null && p.Pid > 4).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase))
        {
            string path = g.Key;
            var flags = Heuristics.Flags(null, path);
            bool trustedLocation = path.StartsWith(win, StringComparison.OrdinalIgnoreCase) || path.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || path.StartsWith(pf86, StringComparison.OrdinalIgnoreCase);
            Signature.Info? sig = null;
            if (!trustedLocation || flags.Count > 0)
            {
                sig = Signature.Check(path);
                if (!sig.Signed && g.Any(p => online.Contains(p.Pid)) && !trustedLocation)
                    flags.Add(Loc.T("Dijital imzası yok ve internete bağlanıyor", "Has no digital signature and connects to the internet"));
                else if (sig.Signed && !sig.Trusted)
                    flags.Add(Loc.T("İmzası doğrulanamıyor (süresi dolmuş, iptal edilmiş ya da kendinden imzalı)", "Its signature cannot be verified (expired, revoked or self-signed)"));
            }
            if (flags.Count == 0) continue;
            var first = g.First();
            processes.Add(new { name = first.Name, description = first.Description, company = first.Company, path, pids = g.Select(p => p.Pid).ToList(), flags, signed = sig?.Signed ?? false, signer = sig?.Signer, online = g.Any(p => online.Contains(p.Pid)) });
        }
        var startup = _startup.List().Where(e => e.Flags.Count > 0 && e.Enabled)
            .Select(e => new { e.Id, e.Name, e.Kind, e.Command, e.Path, e.Publisher, e.Flags, e.Signed, e.Signer }).ToList();
        return new { processes, startup };
    }

    public IEnumerable<Finding> Findings()
    {
        foreach (var c in Checks().Where(c => c.Status is "fail" or "warn"))
        {
            var f = new Finding
            {
                Id = "security." + c.Id, Group = "security", Severity = c.Status == "fail" ? Severity.Critical : Severity.Warning,
                Penalty = c.Penalty, Title = c.Title, Detail = c.Detail, Impact = c.Advice,
            };
            f.Go(Loc.T("Güvenlik sayfasını aç", "Open the security page"), "security");
            yield return f;
        }
        dynamic s = Suspicious();
        int count = ((System.Collections.ICollection)s.processes).Count + ((System.Collections.ICollection)s.startup).Count;
        if (count > 0)
            yield return new Finding
            {
                Id = "security.suspicious", Group = "security", Severity = Severity.Warning, Penalty = 6,
                Title = Loc.T("{0} öğe yakından bakmaya değer", "{0} items deserve a closer look").F(count),
                Detail = Loc.T("Alışılmadık konumdan çalışan, imzasız ya da gizli komut kullanan işlem ve başlangıç girdileri. Bunlar birer işarettir; tanıdığınız programlarsa sorun yoktur.",
                               "Processes and startup entries that run from unusual locations, are unsigned or use hidden commands. These are signals; if you know the programs, all is well."),
            }.Go(Loc.T("İncele", "Review"), "security/suspicious");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("security.checks", c =>
        {
            var checks = Checks(c.Bool("refresh"));
            int penalty = Math.Min(100, checks.Sum(x => x.Penalty));
            return new
            {
                checks, score = 100 - penalty,
                failed = checks.Count(x => x.Status == "fail"), warnings = checks.Count(x => x.Status == "warn"), passed = checks.Count(x => x.Status == "pass"),
            };
        });
        rpc.On("security.ports", _ => Ports());
        rpc.On("security.suspicious", _ => Suspicious());
        rpc.On("security.hosts", _ => Hosts());
        rpc.On("security.hash", c =>
        {
            string path = c.Str("path");
            if (!File.Exists(path)) throw RpcException.NotFound(Loc.S("Dosya bulunamadı.", "File not found."));
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new { sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(), bytes = fs.Length };
        });
    }
}
