using KwikKonvert.Core.Models;
using KwikKonvert.Core.Providers;

namespace KwikKonvert.Core.Services;

public enum JobStatus { Pending, Running, Succeeded, Failed, Cancelled, Skipped }

/// <summary>One file in the queue. Mutated only by <see cref="ConversionQueue"/>; read by the UI via <see cref="ConversionQueue.JobChanged"/>.</summary>
public sealed class ConversionJob
{
    internal CancellationTokenSource? Cts;

    public Guid Id { get; } = Guid.NewGuid();
    public string SourcePath { get; }
    public FormatInfo Source { get; }
    public string TargetFormat { get; internal set; }
    public JobStatus Status { get; internal set; } = JobStatus.Pending;
    public ConversionProgress Progress { get; internal set; } = new(ConversionStage.Preparing, null);
    public ConversionResult? Result { get; internal set; }
    public Exception? Error { get; internal set; }
    public long SourceBytes { get; }
    public DateTime? StartedAt { get; internal set; }

    /// <summary>How much to shrink the output (None = plain conversion).</summary>
    public SqueezeLevel Squeeze { get; init; }

    /// <summary>"Fit under" size in bytes, or null.</summary>
    public long? TargetBytes { get; init; }

    /// <summary>Overrides the configured output folder (used for clipboard images, which have no "same folder").</summary>
    public string? OutputFolder { get; }

    /// <summary>Temporary source to delete once the job is finished for good (clipboard images).</summary>
    public bool DeleteSourceWhenDone { get; init; }

    public string FileName => Path.GetFileName(SourcePath);
    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped;

    internal ConversionJob(string sourcePath, FormatInfo source, string targetFormat, string? outputFolder)
    {
        OutputFolder = outputFolder;
        SourcePath = sourcePath;
        Source = source;
        TargetFormat = targetFormat;
        try { SourceBytes = new FileInfo(sourcePath).Length; } catch { SourceBytes = -1; }
    }
}

/// <summary>
/// Runs conversions in the background with a small concurrency limit. Pure logic: no UI, no Windows APIs.
/// All events may be raised on thread-pool threads — the UI layer marshals them onto its dispatcher.
/// </summary>
public sealed class ConversionQueue
{
    private readonly IConversionProvider _provider;
    private readonly Func<ConversionJob, string> _planOutputPath;
    private readonly SemaphoreSlim _slots;
    private readonly List<ConversionJob> _jobs = [];
    private readonly object _gate = new();

    /// <param name="planOutputPath">job → desired output path. Asked when the job actually starts.</param>
    public ConversionQueue(IConversionProvider provider, Func<ConversionJob, string> planOutputPath, int maxConcurrent = 2)
    {
        _provider = provider;
        _planOutputPath = planOutputPath;
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public event EventHandler<ConversionJob>? JobChanged;
    public event EventHandler<ConversionJob>? JobFinished;

    public IReadOnlyList<ConversionJob> Jobs { get { lock (_gate) return _jobs.ToList(); } }

    public ConversionJob Enqueue(string sourcePath, FormatInfo source, string targetFormat, string? outputFolder = null,
        bool deleteSourceWhenDone = false, SqueezeLevel squeeze = SqueezeLevel.None, long? targetBytes = null)
    {
        var job = new ConversionJob(sourcePath, source, targetFormat.ToLowerInvariant(), outputFolder)
        {
            DeleteSourceWhenDone = deleteSourceWhenDone,
            Squeeze = squeeze,
            TargetBytes = targetBytes,
        };
        lock (_gate) _jobs.Add(job);

        // Already in that format and no compression asked for (can happen in a mixed batch): nothing to do.
        if (squeeze == SqueezeLevel.None && targetBytes is null && FormatService.AreEquivalent(source.Id, job.TargetFormat))
        {
            job.Status = JobStatus.Skipped;
            job.Progress = new ConversionProgress(ConversionStage.Completed, 100);
            Raise(job, finished: true);
            return job;
        }

        Start(job);
        return job;
    }

    /// <summary>Re-runs a failed or cancelled job (individual retry).</summary>
    public void Retry(ConversionJob job)
    {
        if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled)) return;
        job.Status = JobStatus.Pending;
        job.Error = null;
        job.Result = null;
        job.Progress = new ConversionProgress(ConversionStage.Preparing, null);
        Start(job);
    }

    public void Cancel(ConversionJob job)
    {
        if (job.IsFinished) return;
        try { job.Cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void CancelAll()
    {
        foreach (var j in Jobs) Cancel(j);
    }

    /// <summary>Forget finished jobs (e.g. when the user starts a fresh batch).</summary>
    public void ClearFinished()
    {
        lock (_gate) _jobs.RemoveAll(j => j.IsFinished);
    }

    public bool IsBusy => Jobs.Any(j => !j.IsFinished);

    private void Start(ConversionJob job)
    {
        job.Cts = new CancellationTokenSource();
        Raise(job);
        _ = Task.Run(() => RunAsync(job, job.Cts.Token));
    }

    private async Task RunAsync(ConversionJob job, CancellationToken ct)
    {
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(job, JobStatus.Cancelled, null);
            return;
        }

        try
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.Now;
            Raise(job);

            var output = _planOutputPath(job);
            var progress = new InlineProgress(p =>
            {
                job.Progress = p;
                Raise(job);
            });

            job.Result = await _provider.ConvertAsync(new ConversionRequest(job.SourcePath, job.TargetFormat, output, job.Squeeze, job.TargetBytes), progress, ct).ConfigureAwait(false);
            Finish(job, JobStatus.Succeeded, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(job, JobStatus.Cancelled, null);
        }
        catch (Exception ex)
        {
            Finish(job, JobStatus.Failed, ex);
        }
        finally
        {
            _slots.Release();
        }
    }

    private void Finish(ConversionJob job, JobStatus status, Exception? error)
    {
        job.Status = status;
        job.Error = error;
        job.Progress = new ConversionProgress(status switch
        {
            JobStatus.Succeeded => ConversionStage.Completed,
            JobStatus.Cancelled => ConversionStage.Cancelled,
            _ => ConversionStage.Failed,
        }, status == JobStatus.Succeeded ? 100 : null);
        var cts = job.Cts;
        job.Cts = null;
        cts?.Dispose();
        Raise(job, finished: true);
    }

    private void Raise(ConversionJob job, bool finished = false)
    {
        JobChanged?.Invoke(this, job);
        if (finished) JobFinished?.Invoke(this, job);
    }

    private sealed class InlineProgress(Action<ConversionProgress> a) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => a(value);
    }
}
