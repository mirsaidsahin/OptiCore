using System.Text.Json;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class OptimizeStep
{
    public string Id { get; set; } = "";
    /// <summary>cleanup | tweaks | fix</summary>
    public string Kind { get; set; } = "";
    public LText Title { get; set; }
    public LText Detail { get; set; }
    public long Bytes { get; set; }
    public bool Reversible { get; set; } = true;
    public string[] Ids { get; set; } = [];
    public string? Method { get; set; }
    public JsonElement? Params { get; set; }
}

public sealed class OptimizeSnapshot
{
    public int Score { get; set; }
    public long JunkBytes { get; set; }
    public int StartupItems { get; set; }
    public int TweaksApplied { get; set; }
    public long SystemFree { get; set; }
}

/// <summary>
/// One-click optimization: collects only the safe, measured actions OptiCore already knows
/// (recommended cleanup, the "recommended for everyone" tweaks, safe finding fixes), lets the user
/// review them, applies them and measures before and after. Every step goes through the journal,
/// so the whole run can be undone.
/// </summary>
public sealed class OptimizeService(InsightsEngine insights, CleanupService cleanup, TweakService tweaks, StartupService startup,
    SystemInfoService system, ChangeJournal journal, JobManager jobs, RpcRouter rpc)
{
    private OptimizeSnapshot Measure(long junk) => new()
    {
        Score = insights.Analyze(false).Score,
        JunkBytes = junk,
        StartupItems = startup.List(true).Count(e => e.Enabled && e.Kind != "service"),
        TweaksApplied = tweaks.AppliedIds().Count,
        SystemFree = system.Volumes().FirstOrDefault(v => v.IsSystem)?.FreeBytes ?? 0,
    };

    private object Plan(JobContext job)
    {
        job.Report(0.1, Loc.T("Temizlenebilir dosyalar ölçülüyor…", "Measuring what can be cleaned…"));
        var scan = cleanup.Scan(job);
        var steps = new List<OptimizeStep>();

        var junk = scan.Categories.Where(c => c.DefaultOn && c.Available && c.Bytes > 0).ToList();
        if (junk.Count > 0)
            steps.Add(new OptimizeStep
            {
                Id = "cleanup", Kind = "cleanup", Bytes = junk.Sum(c => c.Bytes), Reversible = false, Ids = [.. junk.Select(c => c.Id)],
                Title = Loc.T("Gereksiz dosyaları temizle ({0})", "Clean junk files ({0})").F(new ByteSize(junk.Sum(c => c.Bytes))),
                Detail = Loc.T("Geçici dosyalar ve yeniden oluşturulabilen önbellekler: {0}. Kişisel dosyalara, tarayıcı geçmişine ve oturumlara dokunulmaz.", "Temporary files and caches that can be rebuilt: {0}. Personal files, browser history and sign-ins are not touched.")
                    .F(string.Join(", ", junk.Take(6).Select(c => c.Title.ToString()))),
            });

        job.Report(0.7, Loc.T("Ayarlar ve bulgular inceleniyor…", "Reviewing tweaks and findings…"));
        using var doc = JsonDocument.Parse(rpc.HandleAsync($"{{\"id\":0,\"method\":\"tweaks.list\",\"lang\":\"{Loc.Current}\"}}").GetAwaiter().GetResult());
        var essentials = doc.RootElement.GetProperty("result").GetProperty("items").EnumerateArray()
            .Where(t => t.GetProperty("presets").EnumerateArray().Any(p => p.GetString() == "essentials") && t.GetProperty("state").GetString() != "on"
                && (!t.GetProperty("admin").GetBoolean() || Elevation.IsElevated))
            .Select(t => (Id: t.GetProperty("id").GetString()!, Title: t.GetProperty("title").GetString()!)).ToList();
        if (essentials.Count > 0)
            steps.Add(new OptimizeStep
            {
                Id = "tweaks", Kind = "tweaks", Ids = [.. essentials.Select(e => e.Id)],
                Title = Loc.T("{0} önerilen Windows ayarını uygula", "Apply {0} recommended Windows tweaks").F(essentials.Count),
                Detail = string.Join(", ", essentials.Select(e => e.Title)),
            });

        var report = insights.Analyze(false);
        foreach (var f in report.Findings.Where(f => !f.Ignored))
            foreach (var a in f.Actions.Where(a => a.Safe && a.Method is not null))
                steps.Add(new OptimizeStep
                {
                    Id = "fix:" + f.Id, Kind = "fix", Title = f.Title, Detail = a.Label, Method = a.Method,
                    Params = a.Params is null ? null : JsonSerializer.SerializeToElement(a.Params, Json.Rpc),
                });
        return new { steps, elevated = Elevation.IsElevated };
    }

    private object Run(List<OptimizeStep> steps, JobContext job)
    {
        var started = DateTime.UtcNow;
        job.Report(0.02, Loc.T("Önce ölçülüyor…", "Measuring before…"));
        long junkBefore = steps.Where(s => s.Kind == "cleanup").Sum(s => s.Bytes);
        var before = Measure(junkBefore);
        long freed = 0;
        var results = new List<object>();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            job.ThrowIfCancelled();
            job.Report(0.1 + 0.75 * i / steps.Count, s.Title);
            try
            {
                switch (s.Kind)
                {
                    case "cleanup":
                        var r = JsonSerializer.SerializeToElement(cleanup.Clean(s.Ids, job), Json.Rpc);
                        freed += r.GetProperty("freedBytes").GetInt64();
                        break;
                    case "tweaks":
                        tweaks.ApplyMany(s.Ids, true, Loc.T("Tek tık optimizasyon: önerilen ayarlar", "One-click optimization: recommended tweaks"));
                        break;
                    case "fix":
                        string request = JsonSerializer.Serialize(new { id = 0, method = s.Method, @params = s.Params, lang = Loc.Current });
                        using (var resp = JsonDocument.Parse(rpc.HandleAsync(request).GetAwaiter().GetResult()))
                            if (!resp.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(resp.RootElement.GetProperty("error").GetProperty("message").GetString());
                        break;
                }
                results.Add(new { s.Id, s.Title, ok = true, error = (string?)null });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { results.Add(new { s.Id, s.Title, ok = false, error = e.Message }); }
        }
        job.Report(0.9, Loc.T("Sonra ölçülüyor…", "Measuring after…"));
        var after = Measure(Math.Max(0, junkBefore - freed));
        var sets = journal.List().Where(x => x.Time >= started).Select(x => x.Id).ToList();
        return new { before, after, freed, results, changeSets = sets, simulated = journal.DryRun };
    }

    public void Register(RpcRouter router)
    {
        router.On("optimize.plan", _ => jobs.Start("optimize.plan", Loc.T("Optimizasyon planı", "Optimization plan"), Plan));
        router.On("optimize.run", c =>
        {
            var steps = c.Get<List<OptimizeStep>>("steps");
            return jobs.Start("optimize.run", Loc.T("Tek tık optimizasyon", "One-click optimization"), job => Run(steps, job));
        });
    }
}
