using System.Diagnostics;
using OptiCore.Engine.Core;
using OptiCore.Engine.Modules;
using OptiCore.Engine.Platform;

namespace OptiCore.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oc-clean-" + Guid.NewGuid().ToString("N"));

    public CleanupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    [Fact]
    public void Protected_roots_are_refused()
    {
        Assert.False(FileCleaner.IsSafeRoot(@"C:\"));
        Assert.False(FileCleaner.IsSafeRoot(Paths.Windows));
        Assert.False(FileCleaner.IsSafeRoot(Paths.Profile));
        Assert.False(FileCleaner.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert.False(FileCleaner.IsSafeRoot(Paths.Local));
        Assert.True(FileCleaner.IsSafeRoot(_dir));
    }

    [Fact]
    public void Only_files_older_than_the_limit_are_deleted_and_simulation_deletes_nothing()
    {
        string oldFile = Path.Combine(_dir, "old.tmp"), newFile = Path.Combine(_dir, "new.tmp");
        File.WriteAllBytes(oldFile, new byte[100]);
        File.WriteAllBytes(newFile, new byte[50]);
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-3));
        var target = new CleanTarget(_dir, MinAgeDays: 1);

        var dry = FileCleaner.Delete(target, dryRun: true, CancellationToken.None);
        Assert.Equal((100L, 1L), (dry.Bytes, dry.Files));
        Assert.True(File.Exists(oldFile));

        var real = FileCleaner.Delete(target, dryRun: false, CancellationToken.None);
        Assert.Equal(100, real.Bytes);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public void Junctions_are_never_followed()
    {
        string outside = Path.Combine(Path.GetTempPath(), "oc-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string precious = Path.Combine(outside, "keep.txt");
        File.WriteAllText(precious, "do not delete");
        try
        {
            var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_dir, "link")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            mk.WaitForExit();
            Assert.True(Directory.Exists(Path.Combine(_dir, "link")));
            FileCleaner.Delete(new CleanTarget(_dir), dryRun: false, CancellationToken.None);
            Assert.True(File.Exists(precious));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public void Build_output_needs_its_project_file()
    {
        string project = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(Path.Combine(project, "bin"));
        Directory.CreateDirectory(Path.Combine(project, "node_modules"));
        Directory.CreateDirectory(Path.Combine(project, "dist"));
        Assert.Null(CleanupService.ArtifactConfidence(Path.Combine(project, "bin"))); // "bin" alone may hold scripts
        Assert.True(CleanupService.ArtifactConfidence(Path.Combine(project, "node_modules")));
        File.WriteAllText(Path.Combine(project, "app.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(project, "package.json"), "{}");
        Assert.True(CleanupService.ArtifactConfidence(Path.Combine(project, "bin")));
        Assert.False(CleanupService.ArtifactConfidence(Path.Combine(project, "dist"))); // listed but never preselected
        Assert.Null(CleanupService.ArtifactConfidence(Path.Combine(project, "src")));
    }
}

public class InsightsTests
{
    private static Finding F(string group, Severity severity, int penalty, bool ignored = false) =>
        new() { Id = Guid.NewGuid().ToString(), Group = group, Severity = severity, Penalty = penalty, Ignored = ignored, Title = "x" };

    [Fact]
    public void Clean_system_scores_100()
    {
        var r = InsightsEngine.Score([]);
        Assert.Equal(100, r.Score);
        Assert.Equal("excellent", r.Grade);
    }

    [Fact]
    public void A_critical_finding_caps_the_score()
    {
        var r = InsightsEngine.Score([F("storage", Severity.Critical, 5)]);
        Assert.True(r.Score <= 74);
        Assert.Equal(1, r.Critical);
    }

    [Fact]
    public void Ignored_and_informational_findings_do_not_cost_points()
    {
        var r = InsightsEngine.Score([F("performance", Severity.Warning, 30, ignored: true), F("performance", Severity.Info, 30)]);
        Assert.Equal(100, r.Score);
    }

    [Fact]
    public void Group_scores_are_weighted()
    {
        var r = InsightsEngine.Score([F("performance", Severity.Warning, 40)]);
        Assert.Equal(60, r.Parts.First(p => p.Id == "performance").Score);
        Assert.Equal(90, r.Score); // 25% weight × 40 points
    }
}

public class GamingTests
{
    [Fact]
    public void Frame_statistics_follow_the_usual_definitions()
    {
        var frames = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToList(); // 1% of frames at 50 ms
        var s = FrameCapture.Stats(frames);
        Assert.Equal(1000, s.Frames);
        Assert.Equal(1000.0 * 1000 / (990 * 10 + 10 * 50), s.AvgFps, 3);
        Assert.Equal(20, s.Low1, 3); // slowest 1% average 50 ms → 20 FPS
        Assert.Equal(10, s.Stutters);
        Assert.Equal(10, s.MedianMs);
    }

    [Theory]
    [InlineData(40, 95, 98, 50, "gpu")]
    [InlineData(35, 97, 60, 50, "cpu-thread")]
    [InlineData(80, 95, 60, 50, "cpu")]
    [InlineData(40, 60, 70, 95, "memory")]
    public void Bottleneck_rules(double cpu, double maxCore, double gpu, double mem, string expected)
    {
        var s = new GameSession { Seconds = 600, AvgCpu = cpu, AvgMaxCore = maxCore, AvgGpu = gpu, AvgMem = mem };
        Assert.Equal(expected, GamingService.Analyze(s, 100).Item1);
    }

    [Fact]
    public void Heat_beats_everything()
    {
        var s = new GameSession { Seconds = 600, AvgGpu = 99, ThrottledSeconds = 300 };
        Assert.Equal("thermal", GamingService.Analyze(s, 100).Item1);
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Winget_table_is_parsed_by_header_columns_in_any_language()
    {
        const string output = """
            Ad                            Kimlik                       Sürüm     Kullanılabilir Kaynak
            -------------------------------------------------------------------------------------------
            LibreOffice 26.8.2.2          TheDocumentFoundation.LibreOffice 26.8.2.2 26.8.3.1 winget
            Microsoft Teams               Microsoft.Teams              26072.519 26288.2004     winget
            2 yükseltme kullanılabilir.
            """;
        var items = DiagnosticsService.ParseWinget(output).Select(x => x.GetType().GetProperty("id")!.GetValue(x)).ToList();
        Assert.Contains("Microsoft.Teams", items);
    }
}

public class AutomationTests
{
    [Fact]
    public void Weekly_task_is_due_since_the_last_matching_weekday()
    {
        var task = new MaintenanceTask { Schedule = "weekly", Day = (int)DayOfWeek.Sunday, Hour = 12 };
        var wednesday = new DateTime(2026, 11, 18, 9, 0, 0); // a Wednesday
        Assert.Equal(new DateTime(2026, 11, 15, 12, 0, 0), AutomationService.DueSince(task, wednesday));
    }

    [Fact]
    public void Monthly_task_on_a_day_that_does_not_exist_uses_the_last_day()
    {
        var task = new MaintenanceTask { Schedule = "monthly", Day = 31, Hour = 8 };
        Assert.Equal(new DateTime(2026, 2, 28, 8, 0, 0), AutomationService.DueSince(task, new DateTime(2026, 3, 5)));
    }

    [Fact]
    public void Daily_task_before_its_hour_is_due_since_yesterday()
    {
        var task = new MaintenanceTask { Schedule = "daily", Hour = 20 };
        Assert.Equal(new DateTime(2026, 11, 16, 20, 0, 0), AutomationService.DueSince(task, new DateTime(2026, 11, 17, 10, 0, 0)));
    }
}
