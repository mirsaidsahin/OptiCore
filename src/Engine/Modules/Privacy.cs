using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

/// <summary>Display names of Store packages, looked up in the per-user package repository.</summary>
public static class PackageNames
{
    private const string Repo = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static string[]? _packages;

    /// <param name="familyName">Package family name, e.g. Microsoft.WindowsCamera_8wekyb3d8bbwe.</param>
    public static string Display(string familyName) => Cache.GetOrAdd(familyName, static family =>
    {
        int cut = family.LastIndexOf('_');
        string name = cut > 0 ? family[..cut] : family, publisher = cut > 0 ? family[(cut + 1)..] : "";
        _packages ??= Reg.SubKeys(Repo);
        string? full = _packages.FirstOrDefault(p => p.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) && p.EndsWith("_" + publisher, StringComparison.OrdinalIgnoreCase));
        string? display = full is null ? null : ShellOps.PackageString(full, Reg.GetStr(Repo + "\\" + full, "DisplayName"));
        return string.IsNullOrWhiteSpace(display) ? ShellOps.PrettyPackageName(name) : display!;
    });
}

/// <summary>Privacy audit: which apps may use the camera, microphone, location and other sensitive capabilities, and when they last did.</summary>
public sealed class PrivacyService(ChangeJournal journal)
{
    private const string Store = @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private static readonly (string Id, LText Title, string Icon, string Settings, bool Sensitive)[] Capabilities =
    [
        ("webcam", Loc.T("Kamera", "Camera"), "camera", "ms-settings:privacy-webcam", true),
        ("microphone", Loc.T("Mikrofon", "Microphone"), "mic", "ms-settings:privacy-microphone", true),
        ("location", Loc.T("Konum", "Location"), "pin", "ms-settings:privacy-location", true),
        ("contacts", Loc.T("Kişiler", "Contacts"), "user", "ms-settings:privacy-contacts", true),
        ("appointments", Loc.T("Takvim", "Calendar"), "calendar", "ms-settings:privacy-calendar", false),
        ("email", Loc.T("E-posta", "Email"), "file", "ms-settings:privacy-email", true),
        ("chat", Loc.T("Mesajlar", "Messaging"), "file", "ms-settings:privacy-messaging", true),
        ("phoneCallHistory", Loc.T("Arama geçmişi", "Call history"), "list", "ms-settings:privacy-callhistory", true),
        ("userAccountInformation", Loc.T("Hesap bilgileri", "Account info"), "user", "ms-settings:privacy-accountinfo", false),
        ("userNotificationListener", Loc.T("Bildirimleri okuma", "Reading notifications"), "bell", "ms-settings:privacy-notifications", true),
        ("broadFileSystemAccess", Loc.T("Tüm dosyalara erişim", "Access to all files"), "folder", "ms-settings:privacy-broadfilesystemaccess", true),
        ("documentsLibrary", Loc.T("Belgeler", "Documents"), "folder", "ms-settings:privacy-documents", false),
        ("picturesLibrary", Loc.T("Resimler", "Pictures"), "folder", "ms-settings:privacy-pictures", false),
        ("videosLibrary", Loc.T("Videolar", "Videos"), "folder", "ms-settings:privacy-videos", false),
        ("graphicsCaptureProgrammatic", Loc.T("Ekran görüntüsü alma", "Screen capture"), "monitor", "ms-settings:privacy-graphicscaptureprogrammatic", true),
        ("activity", Loc.T("Hareket verisi", "Motion data"), "activity", "ms-settings:privacy-motion", false),
        ("radios", Loc.T("Bluetooth ve Wi-Fi denetimi", "Bluetooth and Wi-Fi control"), "wifi", "ms-settings:privacy-radios", false),
    ];

    private sealed record AppPermission(string Id, string Name, bool Packaged, bool Allowed, DateTime? LastUsed, bool InUse, bool Stale);

    private sealed record BrowserExtension(string Browser, string Profile, string Id, string Name, string Version, string Description, bool AllSites,
        List<string> Sensitive, bool FromStore, string Risk, bool? Enabled, string? StoreUrl, List<string> Hosts);

    private static DateTime? FileTime(object? value)
    {
        long v = value switch { long l => l, int i => i, _ => 0 };
        if (v <= 0) return null;
        try { return DateTime.FromFileTimeUtc(v); } catch { return null; }
    }

    private object Permissions()
    {
        var result = new List<object>();
        foreach (var (id, title, icon, settings, sensitive) in Capabilities)
        {
            string key = Store + "\\" + id;
            if (!Reg.Exists(key)) continue;
            bool globalAllowed = !string.Equals(Reg.GetStr(key, "Value"), "Deny", StringComparison.OrdinalIgnoreCase);
            var apps = new List<AppPermission>();

            void Read(string appKey, string name, bool packaged, string appId)
            {
                string? value = Reg.GetStr(appKey, "Value");
                var start = FileTime(Reg.Get(appKey, "LastUsedTimeStart"));
                var stop = FileTime(Reg.Get(appKey, "LastUsedTimeStop"));
                if (value is null && start is null) return;
                bool allowed = !string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase);
                bool inUse = start is not null && (stop is null || stop < start);
                DateTime? lastUsed = inUse ? DateTime.UtcNow : stop ?? start;
                // An allowed sensitive permission nobody has used for three months (or ever) is a candidate for removal.
                bool stale = sensitive && packaged && allowed && (lastUsed is null || (DateTime.UtcNow - lastUsed.Value).TotalDays > 90);
                apps.Add(new AppPermission(appId, name, packaged, allowed, lastUsed, inUse, stale));
            }

            foreach (var sub in Reg.SubKeys(key))
            {
                if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var desktop in Reg.SubKeys(key + @"\NonPackaged"))
                    {
                        string path = desktop.Replace('#', '\\');
                        Read($@"{key}\NonPackaged\{desktop}", Path.GetFileName(path), false, path);
                    }
                }
                else Read(key + "\\" + sub, PackageNames.Display(sub), true, sub);
            }
            if (apps.Count == 0) continue;
            result.Add(new
            {
                id, title, icon, settings, sensitive, globalAllowed,
                apps = apps.OrderByDescending(a => a.InUse).ThenByDescending(a => a.LastUsed ?? DateTime.MinValue).ToList(),
                stale = apps.Count(a => a.Stale), inUse = apps.Count(a => a.InUse),
            });
        }
        return result;
    }

    private object SetPermission(string capability, string app, bool allow)
    {
        if (!Capabilities.Any(c => c.Id == capability)) throw RpcException.Invalid("Unknown capability");
        if (app.Contains('\\') || app.Contains('#')) throw RpcException.Denied(Loc.S("Masaüstü programlarının izni tek tek değiştirilemez; Windows bunları yalnızca topluca açıp kapatır.", "Desktop programs cannot be changed one by one; Windows only switches them all together."));
        string key = $@"{Store}\{capability}\{app}";
        if (!Reg.Exists(key)) throw RpcException.NotFound("App not found");
        using var scope = journal.Begin("privacy", (allow ? Loc.T("İzin verildi: {0} → {1}", "Permission granted: {0} → {1}") : Loc.T("İzin kaldırıldı: {0} → {1}", "Permission removed: {0} → {1}")).F(PackageNames.Display(app), capability));
        var entry = scope.RegSet(key, "Value", RegistryValueKind.String, allow ? "Allow" : "Deny");
        if (entry.Error is not null) throw RpcException.Failed(entry.Error);
        return new { simulated = scope.DryRun };
    }

    // ------------------------------------------------------------------ browser extensions
    private static readonly HashSet<string> SensitivePermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "webRequest", "webRequestBlocking", "cookies", "history", "tabs", "clipboardRead", "nativeMessaging", "debugger", "proxy", "management",
        "downloads", "scripting", "browsingData", "privacy", "webNavigation", "declarativeNetRequestWithHostAccess", "desktopCapture", "tabCapture",
    };
    private static readonly string[] AllHosts = ["<all_urls>", "*://*/*", "http://*/*", "https://*/*", "*://*/"];

    private static List<BrowserExtension> Extensions()
    {
        string local = Paths.Local;
        var browsers = new (string Name, string UserData, string StoreUrl)[]
        {
            ("Chrome", Path.Combine(local, @"Google\Chrome\User Data"), "https://chromewebstore.google.com/detail/"),
            ("Edge", Path.Combine(local, @"Microsoft\Edge\User Data"), "https://microsoftedge.microsoft.com/addons/detail/"),
            ("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "https://chromewebstore.google.com/detail/"),
            ("Vivaldi", Path.Combine(local, @"Vivaldi\User Data"), "https://chromewebstore.google.com/detail/"),
        };
        var list = new List<BrowserExtension>();
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (var (browser, userData, storeUrl) in browsers)
        {
            if (!Directory.Exists(userData)) continue;
            foreach (var profile in Directory.EnumerateDirectories(userData).Where(d => Path.GetFileName(d) == "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)))
            {
                string extRoot = Path.Combine(profile, "Extensions");
                if (!Directory.Exists(extRoot)) continue;
                var states = ReadStates(profile, options);
                foreach (var extDir in Directory.EnumerateDirectories(extRoot))
                {
                    try
                    {
                        string id = Path.GetFileName(extDir);
                        var version = Directory.EnumerateDirectories(extDir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                        if (version is null || !File.Exists(Path.Combine(version, "manifest.json"))) continue;
                        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(version, "manifest.json")), options);
                        var m = doc.RootElement;
                        string Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        IEnumerable<string> Arr(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!) : [];

                        var permissions = Arr("permissions").Concat(Arr("optional_permissions")).ToList();
                        var hosts = Arr("host_permissions").Concat(permissions.Where(p => p.Contains("://") || p == "<all_urls>")).ToList();
                        if (m.TryGetProperty("content_scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Array)
                            foreach (var s in scripts.EnumerateArray())
                                if (s.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
                                    hosts.AddRange(matches.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));

                        bool allSites = hosts.Any(h => AllHosts.Contains(h));
                        var sensitive = permissions.Where(SensitivePermissions.Contains).Distinct().ToList();
                        string updateUrl = Str("update_url");
                        bool fromStore = updateUrl.Contains("clients2.google.com") || updateUrl.Contains("edge.microsoft.com");
                        bool powerful = sensitive.Any(p => p is "webRequest" or "webRequestBlocking" or "cookies" or "scripting" or "debugger" or "proxy" or "nativeMessaging" or "history");
                        string risk = allSites && powerful && !fromStore ? "high" : allSites && sensitive.Count > 0 ? "medium" : "low";

                        string name = Localize(Str("name"), version, Str("default_locale"), options);
                        list.Add(new BrowserExtension(browser, Path.GetFileName(profile), id, name, Str("version"),
                            Localize(Str("description"), version, Str("default_locale"), options), allSites, sensitive, fromStore, risk,
                            states.TryGetValue(id, out var enabled) ? enabled : null, fromStore ? storeUrl + id : null, [.. hosts.Distinct().Take(12)]));
                    }
                    catch { /* unreadable or malformed extension */ }
                }
            }
        }
        return [.. list.OrderByDescending(x => x.Risk switch { "high" => 2, "medium" => 1, _ => 0 }).ThenBy(x => x.Name)];
    }

    /// <summary>Resolves "__MSG_key__" placeholders from the extension's default locale.</summary>
    private static string Localize(string value, string versionDir, string defaultLocale, JsonDocumentOptions options)
    {
        if (!value.StartsWith("__MSG_", StringComparison.OrdinalIgnoreCase) || !value.EndsWith("__")) return value;
        string key = value[6..^2];
        foreach (var locale in new[] { defaultLocale, "en", "en_US" }.Where(l => l.Length > 0))
        {
            try
            {
                string file = Path.Combine(versionDir, "_locales", locale, "messages.json");
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file), options);
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var msg)) return msg.GetString() ?? value;
            }
            catch { /* try the next locale */ }
        }
        return key;
    }

    /// <summary>Enabled/disabled state per extension id, from the profile's preference files.</summary>
    private static Dictionary<string, bool> ReadStates(string profile, JsonDocumentOptions options)
    {
        var states = new Dictionary<string, bool>();
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            try
            {
                string path = Path.Combine(profile, file);
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs, options);
                if (!doc.RootElement.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("settings", out var settings)) continue;
                foreach (var e in settings.EnumerateObject())
                {
                    if (states.ContainsKey(e.Name)) continue;
                    bool disabled = e.Value.TryGetProperty("disable_reasons", out var reasons)
                        && (reasons.ValueKind == JsonValueKind.Number ? reasons.GetInt32() != 0 : reasons.ValueKind == JsonValueKind.Array && reasons.GetArrayLength() > 0);
                    if (e.Value.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Number) disabled |= state.GetInt32() != 1;
                    states[e.Name] = !disabled;
                }
            }
            catch { /* the browser may be rewriting the file */ }
        }
        return states;
    }

    public IEnumerable<Finding> Findings()
    {
        var risky = Extensions().Where(e => e.Risk == "high" && e.Enabled != false).Select(e => e.Name).ToList();
        if (risky.Count > 0)
            yield return new Finding
            {
                Id = "privacy.extensions", Group = "security", Severity = Severity.Warning, Penalty = 8,
                Title = Loc.T("{0} tarayıcı eklentisi mağaza dışından kurulmuş ve tüm sitelere erişebiliyor", "{0} browser extension(s) were installed from outside the store and can access every site").F(risky.Count),
                Detail = Loc.T("Bu eklentiler ziyaret ettiğiniz her sayfayı okuyup değiştirebilir ve mağazanın denetiminden geçmemiştir. Tanımıyorsanız tarayıcının eklentiler sayfasından kaldırın.",
                               "These extensions can read and change every page you visit and were never reviewed by the store. If you do not recognize them, remove them on the browser's extensions page."),
            }.Ev(Loc.T("Eklentiler", "Extensions"), string.Join(", ", risky.Take(4))).Go(Loc.T("Eklentileri incele", "Review extensions"), "security/extensions");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("privacy.permissions", _ => Permissions());
        rpc.On("privacy.setPermission", c => SetPermission(c.Str("capability"), c.Str("app"), c.Bool("allow")));
        rpc.On("privacy.extensions", _ => Extensions());
    }
}
