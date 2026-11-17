using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using OptiCore.Engine.Core;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

/// <summary>
/// Self-update from the project's GitHub releases. Checks at most once a day (only the public release
/// list is requested, nothing about the PC is sent), downloads OptiCore.exe, verifies its SHA-256 against
/// the digest GitHub publishes, and swaps the running executable: Windows allows renaming a running exe,
/// so the old file is moved aside and removed on the next start.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "mirsaidsahin/OptiCore";
    private const string AssetName = "OptiCore.exe";

    private readonly DataStore _store;
    private readonly SettingsService _settings;
    private readonly NotificationCenter _notices;
    private readonly JobManager _jobs;
    private readonly Func<string?> _exe;

    public sealed record Release(string Version, string Tag, string Notes, string Url, string? Download, long Size, string? Sha256, DateTime? Published);

    public UpdateService(DataStore store, SettingsService settings, NotificationCenter notices, JobManager jobs, Func<string?> exe)
    {
        _store = store; _settings = settings; _notices = notices; _jobs = jobs; _exe = exe;
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OptiCore/" + OptiCoreEngine.Version);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>True when the release tag (v0.2.0, 0.2.0) is a newer version than the running one.</summary>
    public static bool IsNewer(string current, string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest) && Version.TryParse(current.Split('-')[0], out var now) && latest > now;

    public static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        string? download = null, sha = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                download = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString()!.StartsWith("sha256:", StringComparison.Ordinal))
                    sha = d.GetString()![7..];
            }
        return new Release(tag.TrimStart('v', 'V'), tag, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", r.GetProperty("html_url").GetString() ?? "",
            download, size, sha, r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : null);
    }

    public Release? Latest()
    {
        using var http = Client();
        var json = http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest").GetAwaiter().GetResult();
        var release = Parse(json);
        _store.Save("updates/last-check.json", DateTime.UtcNow);
        return release;
    }

    private object Check()
    {
        Release? r;
        try { r = Latest(); }
        catch (Exception e) { throw RpcException.Failed(Loc.S("Güncelleme sunucusuna ulaşılamadı: ", "The update server could not be reached: ") + e.Message); }
        return new
        {
            current = OptiCoreEngine.Version, latest = r?.Version, newer = r is not null && IsNewer(OptiCoreEngine.Version, r.Tag),
            notes = r?.Notes, url = r?.Url, size = r?.Size, published = r?.Published, canInstall = _exe() is not null && r?.Download is not null,
        };
    }

    private object Install(JobContext job)
    {
        string exe = _exe() ?? throw RpcException.Unsupported(Loc.S("Güncelleme yalnızca masaüstü uygulamasında kurulabilir.", "Updates can only be installed in the desktop app."));
        var r = Latest() ?? throw RpcException.NotFound("No release");
        if (!IsNewer(OptiCoreEngine.Version, r.Tag)) return new { installed = false, version = r.Version };
        if (r.Download is null) throw RpcException.NotFound(Loc.S("Sürümde OptiCore.exe bulunamadı.", "The release has no OptiCore.exe."));

        string dir = Path.GetDirectoryName(exe)!;
        string next = Path.Combine(dir, "OptiCore.new.exe"), old = Path.Combine(dir, "OptiCore.old.exe");
        try
        {
            using var http = Client();
            using var response = http.GetAsync(r.Download, HttpCompletionOption.ResponseHeadersRead, job.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using (var input = response.Content.ReadAsStream(job.Token))
            using (var output = File.Create(next))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    job.ThrowIfCancelled();
                    output.Write(buffer, 0, n);
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    job.Report(r.Size > 0 ? 0.95 * done / r.Size : -1, Loc.T("İndiriliyor: {0} / {1}", "Downloading: {0} / {1}").F(new ByteSize(done), new ByteSize(r.Size)));
                }
                string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (r.Sha256 is not null && !actual.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw RpcException.Failed(Loc.S("İndirilen dosya doğrulanamadı (SHA-256 uyuşmuyor); güncelleme iptal edildi.", "The download could not be verified (SHA-256 mismatch); the update was cancelled."));
            }
            job.Say(Loc.T("Kuruluyor…", "Installing…"));
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);   // a running executable can be renamed, not overwritten
            File.Move(next, exe);
        }
        catch (UnauthorizedAccessException) { TryDelete(next); throw RpcException.Elevation(); }
        catch { TryDelete(next); throw; }
        Log.Info($"update: installed {r.Version}");
        return new { installed = true, version = r.Version };
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }

    /// <summary>Removes the executable left behind by the previous update.</summary>
    public static void CleanupAfterUpdate(string? exe)
    {
        if (exe is null) return;
        TryDelete(Path.Combine(Path.GetDirectoryName(exe)!, "OptiCore.old.exe"));
    }

    /// <summary>Daily background check; tells the user through the notification center.</summary>
    public void Start()
    {
        if (_exe() is null || !_settings.Current.CheckUpdates) return;
        _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ =>
        {
            try
            {
                var last = _store.Load<DateTime?>("updates/last-check.json", () => null);
                if (last is not null && DateTime.UtcNow - last.Value < TimeSpan.FromHours(20)) return;
                var r = Latest();
                if (r is not null && IsNewer(OptiCoreEngine.Version, r.Tag))
                    _notices.Add(NoticeLevel.Info, Loc.T("OptiCore {0} yayınlandı", "OptiCore {0} is available").F(r.Version),
                        Loc.T("Ayarlar sayfasından tek tıkla güncelleyebilirsiniz.", "You can update in one click on the Settings page."), "settings", "update." + r.Version, coolDownHours: 24 * 30);
            }
            catch (Exception e) { Log.Warn("update: check", e); }
        });
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("update.check", _ => Check());
        rpc.On("update.install", _ => _jobs.Start("update.install", Loc.T("OptiCore güncelleniyor", "Updating OptiCore"), Install));
    }
}
