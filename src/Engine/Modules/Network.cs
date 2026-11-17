using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Native;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

/// <summary>Network Optimizer: latency and packet loss, DNS benchmark and switching, Wi-Fi, per-app traffic, diagnosis and repair tools.</summary>
public sealed class NetworkService
{
    public static readonly (string Id, string Name, string V4a, string V4b, LText Note)[] DnsProviders =
    [
        ("cloudflare", "Cloudflare", "1.1.1.1", "1.0.0.1", Loc.T("Hızlı, kayıt tutmama taahhüdü", "Fast, no-logging commitment")),
        ("google", "Google", "8.8.8.8", "8.8.4.4", Loc.T("Yaygın ve kararlı", "Ubiquitous and stable")),
        ("quad9", "Quad9", "9.9.9.9", "149.112.112.112", Loc.T("Zararlı alan adlarını engeller", "Blocks malicious domains")),
        ("adguard", "AdGuard", "94.140.14.14", "94.140.15.15", Loc.T("Reklam ve izleyicileri engeller", "Blocks ads and trackers")),
        ("cf-family", "Cloudflare Family", "1.1.1.3", "1.0.0.3", Loc.T("Zararlı ve yetişkin içerik filtresi", "Malware and adult content filter")),
        ("opendns", "OpenDNS", "208.67.222.222", "208.67.220.220", Loc.T("Cisco tarafından işletilir", "Operated by Cisco")),
    ];

    private static readonly string[] Domains = ["google.com", "youtube.com", "wikipedia.org", "microsoft.com", "github.com", "cloudflare.com", "amazon.com", "steampowered.com", "netflix.com", "reddit.com"];

    private readonly JobManager _jobs;
    private readonly ChangeJournal _journal;
    private readonly ProcessSampler _processes;

    public NetworkService(JobManager jobs, ChangeJournal journal, ProcessSampler processes)
    {
        _jobs = jobs; _journal = journal; _processes = processes;
        journal.RegisterUndo("dns.servers", e =>
        {
            string servers = e.Before.GetValueOrDefault("servers") ?? "";
            SetDns(e.Target, servers.Length == 0 ? [] : servers.Split(','));
        });
    }

    // ------------------------------------------------------------------ ping
    private static async Task<(double? Avg, double? Min, double? Max, double Loss, double? Jitter, List<double?> Samples)> Ping(string host, int count, int interval = 200)
    {
        using var ping = new Ping();
        var samples = new List<double?>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                var r = await ping.SendPingAsync(host, 1500);
                samples.Add(r.Status == IPStatus.Success ? r.RoundtripTime : null);
            }
            catch { samples.Add(null); }
            if (i < count - 1) await Task.Delay(interval);
        }
        var ok = samples.Where(s => s is not null).Select(s => s!.Value).ToList();
        double? jitter = ok.Count > 1 ? ok.Zip(ok.Skip(1), (a, b) => Math.Abs(a - b)).Average() : null;
        return (ok.Count > 0 ? ok.Average() : null, ok.Count > 0 ? ok.Min() : null, ok.Count > 0 ? ok.Max() : null, 100.0 * (count - ok.Count) / count, jitter, samples);
    }

    private object PingTest(string? target, int count, JobContext job)
    {
        var gateway = SystemInfoService.Adapters().FirstOrDefault(a => a.Status == "up" && a.Gateway is not null)?.Gateway;
        var targets = new List<(string Label, string Host)>();
        if (!string.IsNullOrWhiteSpace(target)) targets.Add((target!, target!));
        else
        {
            if (gateway is not null) targets.Add((Loc.S("Modem / yönlendirici", "Router"), gateway));
            targets.Add(("Cloudflare", "1.1.1.1"));
            targets.Add(("Google", "8.8.8.8"));
            targets.Add(("Quad9", "9.9.9.9"));
        }
        var results = new List<object>();
        for (int i = 0; i < targets.Count; i++)
        {
            job.ThrowIfCancelled();
            job.Report(i / (double)targets.Count, Loc.T("Ölçülüyor: {0}", "Measuring: {0}").F(targets[i].Label));
            var r = Ping(targets[i].Host, count).GetAwaiter().GetResult();
            results.Add(new { label = targets[i].Label, host = targets[i].Host, avg = r.Avg, min = r.Min, max = r.Max, loss = r.Loss, jitter = r.Jitter, samples = r.Samples });
        }
        return new { results };
    }

    // ------------------------------------------------------------------ DNS
    /// <summary>Sends a raw DNS query over UDP and measures the answer time; no OS cache involved.</summary>
    private static double? DnsQuery(IPAddress server, string domain)
    {
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var packet = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.')) { packet.Add((byte)label.Length); packet.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
        packet.AddRange([0, 0, 1, 0, 1]);
        using var udp = new UdpClient(server.AddressFamily);
        udp.Client.ReceiveTimeout = 2000;
        var sw = Stopwatch.StartNew();
        try
        {
            udp.Send([.. packet], packet.Count, new IPEndPoint(server, 53));
            IPEndPoint? from = null;
            var reply = udp.Receive(ref from);
            sw.Stop();
            return reply.Length >= 12 && reply[0] == (byte)(id >> 8) && reply[1] == (byte)id && (reply[3] & 0x0F) == 0 ? sw.Elapsed.TotalMilliseconds : null;
        }
        catch { return null; }
    }

    private object DnsBenchmark(JobContext job)
    {
        var current = SystemInfoService.Adapters().FirstOrDefault(a => a.Status == "up" && a.Gateway is not null)?.Dns
            .Where(d => IPAddress.TryParse(d, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork).ToList() ?? [];
        var servers = new List<(string Id, string Name, string Ip, LText Note)>();
        foreach (var c in current.Take(2)) servers.Add(("current", Loc.S("Şu anki", "Current") + " (" + c + ")", c, Loc.T("Bağlantınızın kullandığı sunucu", "The server your connection uses")));
        foreach (var p in DnsProviders) servers.Add((p.Id, p.Name, p.V4a, p.Note));

        var results = new List<object>();
        for (int i = 0; i < servers.Count; i++)
        {
            job.ThrowIfCancelled();
            job.Report(i / (double)servers.Count, Loc.T("Deneniyor: {0}", "Testing: {0}").F(servers[i].Name));
            var ip = IPAddress.Parse(servers[i].Ip);
            var times = new List<double>();
            int failed = 0;
            // Each domain twice: the first answer may be a cache miss at the resolver, the second shows its cached speed.
            foreach (var domain in Domains)
                for (int k = 0; k < 2; k++)
                {
                    var t = DnsQuery(ip, domain);
                    if (t is null) failed++; else times.Add(t.Value);
                }
            times.Sort();
            results.Add(new
            {
                id = servers[i].Id, name = servers[i].Name, ip = servers[i].Ip, note = servers[i].Note,
                median = times.Count > 0 ? times[times.Count / 2] : (double?)null, avg = times.Count > 0 ? times.Average() : (double?)null,
                p90 = times.Count > 0 ? times[(int)(times.Count * 0.9)] : (double?)null, failed, total = Domains.Length * 2,
            });
        }
        return new { results = results.OrderBy(r => ((dynamic)r).median ?? 99999.0).ToList(), current };
    }

    private static void SetDns(string adapterName, string[] servers)
    {
        string name = adapterName.Replace("\"", "");
        var r = servers.Length == 0
            ? Proc.Run(Proc.Sys("netsh.exe"), $"interface ipv4 set dnsservers name=\"{name}\" source=dhcp")
            : Proc.Run(Proc.Sys("netsh.exe"), $"interface ipv4 set dnsservers name=\"{name}\" source=static address={servers[0]} register=primary validate=no");
        if (!r.Ok) throw r.Text.Contains("elevation", StringComparison.OrdinalIgnoreCase) || r.Text.Contains("yönetici", StringComparison.OrdinalIgnoreCase) || r.ExitCode == 1 && !Elevation.IsElevated
            ? new UnauthorizedAccessException(r.Text) : new InvalidOperationException(r.Text);
        if (servers.Length > 1) Proc.Run(Proc.Sys("netsh.exe"), $"interface ipv4 add dnsservers name=\"{name}\" address={servers[1]} index=2 validate=no");
        Win32.DnsFlushResolverCache();
    }

    private object ChangeDns(string adapterId, string provider)
    {
        var adapter = SystemInfoService.Adapters().FirstOrDefault(a => a.Id == adapterId) ?? throw RpcException.NotFound("Adapter not found");
        string[] servers = provider == "auto" ? [] : DnsProviders.FirstOrDefault(p => p.Id == provider) is var p && p.Id is not null ? [p.V4a, p.V4b] : throw RpcException.Invalid("Unknown provider");
        // Before value: static servers if the adapter has them, empty (DHCP) otherwise.
        string? staticServers = Reg.GetStr($@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapter.Id}", "NameServer");
        using var scope = _journal.Begin("network", Loc.T("DNS: {0} → {1}", "DNS: {0} → {1}").F(adapter.Name, provider == "auto" ? "DHCP" : string.Join(", ", servers)));
        var entry = scope.Custom("dns.servers", adapter.Name, new() { ["servers"] = (staticServers ?? "").Replace(' ', ',') }, new() { ["servers"] = string.Join(",", servers) }, () => SetDns(adapter.Name, servers));
        scope.Commit();
        if (entry.Error == "ELEVATION_REQUIRED") throw RpcException.Elevation();
        if (entry.Error is not null) throw RpcException.Failed(entry.Error);
        return new { simulated = scope.DryRun };
    }

    // ------------------------------------------------------------------ Wi-Fi
    private static object? Wifi()
    {
        if (Win32.WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return null;
        try
        {
            if (Win32.WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return null;
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = list + 8 + i * 532; // WLAN_INTERFACE_INFO: GUID(16) + WCHAR[256](512) + state(4)
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    int state = Marshal.ReadInt32(item, 528);
                    if (state != 1) continue; // connected
                    if (Win32.WlanQueryInterface(handle, ref guid, Win32.WLAN_OPCODE_CURRENT_CONNECTION, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) continue;
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state(4) mode(4) profile WCHAR[256](512) then WLAN_ASSOCIATION_ATTRIBUTES
                        IntPtr assoc = data + 520;
                        int ssidLen = Marshal.ReadInt32(assoc);
                        var ssidBytes = new byte[Math.Clamp(ssidLen, 0, 32)];
                        Marshal.Copy(assoc + 4, ssidBytes, 0, ssidBytes.Length);
                        // WLAN_ASSOCIATION_ATTRIBUTES: ssid(36) bssType(4) bssid(6, padded to 8) phyType(4) phyIndex(4) quality(4) rx(4) tx(4)
                        int phy = Marshal.ReadInt32(assoc, 48);
                        uint quality = (uint)Marshal.ReadInt32(assoc, 56);
                        uint rx = (uint)Marshal.ReadInt32(assoc, 60), tx = (uint)Marshal.ReadInt32(assoc, 64);
                        // WLAN_SECURITY_ATTRIBUTES follows: securityEnabled(4) oneX(4) authAlgorithm(4) cipher(4)
                        int auth = Marshal.ReadInt32(assoc, 68 + 8);
                        int? channel = null;
                        if (Win32.WlanQueryInterface(handle, ref guid, Win32.WLAN_OPCODE_CHANNEL_NUMBER, IntPtr.Zero, out _, out var ch, IntPtr.Zero) == 0)
                        {
                            channel = Marshal.ReadInt32(ch);
                            Win32.WlanFreeMemory(ch);
                        }
                        // Windows defines signal quality as a linear map of -100..-50 dBm onto 0..100.
                        int rssi = (int)quality / 2 - 100;
                        string phyName = phy switch { 7 => "802.11n (Wi-Fi 4)", 8 => "802.11ac (Wi-Fi 5)", 10 => "802.11ax (Wi-Fi 6)", 11 => "802.11be (Wi-Fi 7)", 4 => "802.11a", 5 => "802.11b", 6 => "802.11g", _ => "802.11" };
                        string authName = auth switch { 1 => "Open", 3 => "WPA", 4 => "WPA-PSK", 6 => "WPA2", 7 => "WPA2-PSK", 8 => "WPA3", 9 => "WPA3-SAE", 10 => "OWE", 11 => "WPA3-Enterprise", _ => "?" };
                        return new
                        {
                            ssid = System.Text.Encoding.UTF8.GetString(ssidBytes), quality = (int)quality, rssi, rxMbps = rx / 1000.0, txMbps = tx / 1000.0, phy = phyName, channel,
                            band = channel is null ? null : channel <= 14 ? "2.4 GHz" : "5/6 GHz",
                            security = authName, open = auth == 1,
                        };
                    }
                    finally { Win32.WlanFreeMemory(data); }
                }
            }
            finally { Win32.WlanFreeMemory(list); }
        }
        catch { /* wlan service off */ }
        finally { Win32.WlanCloseHandle(handle, IntPtr.Zero); }
        return null;
    }

    // ------------------------------------------------------------------ per-app traffic
    /// <summary>
    /// Per-process network rates. Windows has no unprivileged per-process byte counter, so this uses the
    /// process I/O "other" transfer counters as a proxy and attributes them only to processes with open sockets.
    /// </summary>
    private object Apps()
    {
        var conns = ConnectionTable.Snapshot();
        var withSockets = conns.Where(c => c.State is "established" or "listen").GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => (Established: g.Count(c => c.State == "established"), Remote: g.Where(c => c.RemoteAddress is not null).Select(c => c.RemoteAddress!).Distinct().Count()));
        var procs = _processes.Snapshot(1500).Where(p => withSockets.ContainsKey(p.Pid) && p.Pid > 4).ToList();
        return procs.GroupBy(p => p.Description ?? p.Name).Select(g => new
        {
            name = g.Key, path = g.First().Path, pids = g.Select(p => p.Pid).ToList(),
            connections = g.Sum(p => withSockets[p.Pid].Established), remotes = g.Sum(p => withSockets[p.Pid].Remote),
        }).Where(x => x.connections > 0).OrderByDescending(x => x.connections).Take(40).ToList();
    }

    // ------------------------------------------------------------------ diagnosis
    private object Diagnose(JobContext job)
    {
        var steps = new List<object>();
        void Step(string id, LText title, bool ok, LText detail, LText? fix = null) => steps.Add(new { id, title, ok, detail, fix });

        job.Report(0.1, Loc.T("Ağ bağdaştırıcıları denetleniyor…", "Checking adapters…"));
        var adapters = SystemInfoService.Adapters().Where(a => a.Physical).ToList();
        var up = adapters.FirstOrDefault(a => a.Status == "up" && a.Ipv4 is not null);
        Step("adapter", Loc.T("Ağ bağdaştırıcısı", "Network adapter"), up is not null,
            up is not null ? Loc.T("{0} bağlı ({1})", "{0} connected ({1})").F(up.Name, up.Ipv4!) : Loc.T("Bağlı bir kablolu ya da kablosuz bağdaştırıcı yok.", "No wired or wireless adapter is connected."),
            up is null ? Loc.T("Wi-Fi'nin açık olduğunu ya da kablonun takılı olduğunu denetleyin.", "Check that Wi-Fi is on or the cable is plugged in.") : (LText?)null);
        if (up is null) return new { steps, ok = false };

        bool apipa = up.Ipv4!.StartsWith("169.254.");
        Step("ip", Loc.T("IP adresi", "IP address"), !apipa, apipa ? Loc.T("Modemden adres alınamadı (169.254.x.x).", "No address from the router (169.254.x.x).") : Loc.T("Adres alındı: {0}", "Address assigned: {0}").F(up.Ipv4),
            apipa ? Loc.T("Modemi yeniden başlatın; ardından aşağıdaki \"IP adresini yenile\" aracını çalıştırın.", "Restart the router, then run the \"Renew IP address\" tool below.") : (LText?)null);

        job.Report(0.35, Loc.T("Modeme ulaşılıyor…", "Reaching the router…"));
        if (up.Gateway is not null)
        {
            var g = Ping(up.Gateway, 4, 100).GetAwaiter().GetResult();
            Step("gateway", Loc.T("Modem / yönlendirici", "Router"), g.Avg is not null, g.Avg is not null ? Loc.T("{0} ms, kayıp %{1:0}", "{0} ms, {1:0}% loss").F(Math.Round(g.Avg.Value), g.Loss) : Loc.T("Modem yanıt vermiyor.", "The router does not respond."),
                g.Avg is null ? Loc.T("Modemi yeniden başlatın.", "Restart the router.") : g.Loss > 10 ? Loc.T("Kablosuz sinyali zayıf olabilir; modeme yaklaşın.", "The wireless signal may be weak; move closer to the router.") : (LText?)null);
        }

        job.Report(0.55, Loc.T("İnternete ulaşılıyor…", "Reaching the internet…"));
        var net = Ping("1.1.1.1", 4, 100).GetAwaiter().GetResult();
        Step("internet", Loc.T("İnternet bağlantısı", "Internet connection"), net.Avg is not null, net.Avg is not null ? Loc.T("{0} ms gecikme", "{0} ms latency").F(Math.Round(net.Avg.Value)) : Loc.T("İnternete ulaşılamıyor.", "The internet cannot be reached."),
            net.Avg is null ? Loc.T("Sorun büyük olasılıkla internet sağlayıcınızda ya da modemde.", "The problem is most likely at your provider or the router.") : (LText?)null);

        job.Report(0.75, Loc.T("DNS denetleniyor…", "Checking DNS…"));
        bool dnsOk;
        try { dnsOk = Dns.GetHostAddresses("www.microsoft.com").Length > 0; } catch { dnsOk = false; }
        Step("dns", Loc.T("Ad çözümleme (DNS)", "Name resolution (DNS)"), dnsOk, dnsOk ? Loc.T("Alan adları çözümleniyor.", "Domain names resolve.") : Loc.T("Alan adları çözümlenemiyor; internet var ama siteler açılmaz.", "Domain names do not resolve; the internet works but sites will not open."),
            dnsOk ? (LText?)null : Loc.T("DNS önbelleğini temizleyin ya da DNS sunucusunu Cloudflare/Google olarak değiştirin.", "Flush the DNS cache or switch the DNS server to Cloudflare/Google."));

        job.Report(0.9, Loc.T("HTTPS denetleniyor…", "Checking HTTPS…"));
        bool https;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            https = http.Send(new HttpRequestMessage(HttpMethod.Head, "http://www.msftconnecttest.com/connecttest.txt")).IsSuccessStatusCode;
        }
        catch { https = false; }
        Step("web", Loc.T("Web erişimi", "Web access"), https, https ? Loc.T("Web sayfalarına erişilebiliyor.", "Web pages are reachable.") : Loc.T("Web erişimi engelli; bir giriş sayfası (otel, okul ağı) ya da vekil sunucu olabilir.", "Web access is blocked; there may be a sign-in page (hotel, campus) or a proxy."));
        return new { steps, ok = steps.All(s => ((dynamic)s).ok) };
    }

    private object Repair(string action)
    {
        var tools = new Dictionary<string, (string File, string Args, bool Admin, LText Title)>
        {
            ["flushdns"] = ("", "", false, Loc.T("DNS önbelleği temizlendi", "DNS cache flushed")),
            ["renew"] = (Proc.Sys("ipconfig.exe"), "/renew", false, Loc.T("IP adresi yenilendi", "IP address renewed")),
            ["winsock"] = (Proc.Sys("netsh.exe"), "winsock reset", true, Loc.T("Winsock sıfırlandı (yeniden başlatma gerekir)", "Winsock reset (restart required)")),
            ["tcpip"] = (Proc.Sys("netsh.exe"), "int ip reset", true, Loc.T("TCP/IP yığını sıfırlandı (yeniden başlatma gerekir)", "TCP/IP stack reset (restart required)")),
        };
        if (!tools.TryGetValue(action, out var tool)) throw RpcException.Invalid("Unknown tool");
        if (tool.Admin) Elevation.Require();
        using var scope = _journal.Begin("network", tool.Title);
        scope.Note("network.repair", action);
        if (scope.DryRun) return new { simulated = true, output = "" };
        if (action == "flushdns") { Win32.DnsFlushResolverCache(); return new { simulated = false, output = "" }; }
        var r = Proc.Run(tool.File, tool.Args, 60000);
        if (!r.Ok) throw RpcException.Failed(r.Text);
        return new { simulated = false, output = r.Text };
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("network.ping", c => { string? target = c.StrOrNull("target"); int count = Math.Clamp(c.Int("count", 20), 4, 100); return _jobs.Start("network.ping", Loc.T("Gecikme ve paket kaybı testi", "Latency and packet loss test"), job => PingTest(target, count, job)); });
        rpc.On("network.dnsBenchmark", _ => _jobs.Start("network.dns", Loc.T("DNS karşılaştırması", "DNS benchmark"), DnsBenchmark));
        rpc.On("network.dnsProviders", _ => DnsProviders.Select(p => new { p.Id, p.Name, primary = p.V4a, secondary = p.V4b, p.Note }).ToList());
        rpc.On("network.setDns", c => ChangeDns(c.Str("adapter"), c.Str("provider")));
        rpc.On("network.wifi", _ => Wifi());
        rpc.On("network.apps", _ => Apps());
        rpc.On("network.diagnose", _ => _jobs.Start("network.diagnose", Loc.T("Bağlantı teşhisi", "Connection diagnosis"), Diagnose));
        rpc.On("network.repair", c => Repair(c.Str("action")));
    }
}
