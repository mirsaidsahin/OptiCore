using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace OptiCore.Engine.Core;

public enum JobState { Running, Done, Failed, Cancelled }

/// <summary>A long-running operation (scan, benchmark, cleanup). The UI polls it by id.</summary>
public sealed class Job
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public LText Title { get; init; }
    public JobState State { get; internal set; } = JobState.Running;
    /// <summary>0..1, or -1 when the amount of work is unknown.</summary>
    public double Progress { get; internal set; } = -1;
    public LText? Message { get; internal set; }
    public object? Partial { get; internal set; }
    public object? Result { get; internal set; }
    public string? Error { get; internal set; }
    public string? ErrorCode { get; internal set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; internal set; }

    [JsonIgnore] internal CancellationTokenSource Cts { get; } = new();
}

public sealed class JobContext(Job job)
{
    public CancellationToken Token => job.Cts.Token;
    public bool Cancelled => job.Cts.IsCancellationRequested;

    public void Report(double progress, LText? message = null)
    {
        job.Progress = progress < 0 ? -1 : Math.Clamp(progress, 0, 1);
        if (message is not null) job.Message = message;
    }

    public void Say(LText message) => job.Message = message;
    /// <summary>Publishes an intermediate result the UI can render while the job is still running.</summary>
    public void Partial(object? value) => job.Partial = value;
    public void ThrowIfCancelled() => job.Cts.Token.ThrowIfCancellationRequested();
}

public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private int _seq;

    public Job Start(string kind, LText title, Func<JobContext, object?> work, bool unique = true)
    {
        if (unique)
        {
            var running = _jobs.Values.FirstOrDefault(j => j.Kind == kind && j.State == JobState.Running);
            if (running is not null) return running;
        }

        var job = new Job { Id = $"{kind}-{Interlocked.Increment(ref _seq)}", Kind = kind, Title = title };
        _jobs[job.Id] = job;
        Prune();

        // Task.Run captures the ExecutionContext, so Loc.Current flows into the job.
        Task.Run(() =>
        {
            try
            {
                job.Result = work(new JobContext(job));
                job.State = job.Cts.IsCancellationRequested ? JobState.Cancelled : JobState.Done;
                if (job.State == JobState.Done) job.Progress = 1;
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Cancelled;
            }
            catch (RpcException e)
            {
                job.State = JobState.Failed; job.Error = e.Message; job.ErrorCode = e.Code;
            }
            catch (Exception e)
            {
                Log.Error($"job {job.Id} failed", e);
                job.State = JobState.Failed; job.Error = e.Message; job.ErrorCode = "FAILED";
            }
            finally
            {
                job.FinishedAt = DateTime.UtcNow;
            }
        });
        return job;
    }

    public Job? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var j) || j.State != JobState.Running) return false;
        j.Cts.Cancel();
        return true;
    }

    private void Prune()
    {
        if (_jobs.Count < 60) return;
        foreach (var old in _jobs.Values.Where(j => j.State != JobState.Running).OrderBy(j => j.StartedAt).Take(_jobs.Count - 40))
            _jobs.TryRemove(old.Id, out _);
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("jobs.get", c => Get(c.Str("id")) ?? throw RpcException.NotFound("Job not found"));
        rpc.On("jobs.cancel", c => Cancel(c.Str("id")));
        rpc.On("jobs.list", _ => _jobs.Values.OrderByDescending(j => j.StartedAt)
            .Select(j => new { j.Id, j.Kind, j.Title, j.State, j.Progress, j.Message, j.StartedAt, j.FinishedAt }).ToList());
    }
}
