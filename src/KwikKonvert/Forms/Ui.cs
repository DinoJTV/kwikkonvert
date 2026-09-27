using KwikKonvert.Core.Models;
using KwikKonvert.Core.Providers;
using KwikKonvert.Core.Services;
using KwikKonvert.Platform;

namespace KwikKonvert.Forms;

/// <summary>Shared bits for the plain Win32-style windows: icon, doge pictures, status wording, layout helpers.</summary>
internal static class Ui
{
    private static byte[]? _gifBytes;
    private static byte[]? _stillBytes;
    private static Icon? _icon;

    public static Icon? AppIcon
    {
        get
        {
            if (_icon is not null) return _icon;
            using var s = ShellHelper.OpenAsset("KwikKonvert.ico");
            return _icon = s is null ? null : new Icon(s);
        }
    }

    /// <summary>A fresh animated doge (each doge gets its own copy so they animate independently).</summary>
    public static Image? DogeGif() => LoadImage(ref _gifBytes, "doge.gif");

    public static Image? DogeStill() => LoadImage(ref _stillBytes, "doge-still.png");

    private static Image? LoadImage(ref byte[]? cache, string name)
    {
        if (cache is null)
        {
            using var s = ShellHelper.OpenAsset(name);
            if (s is null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            cache = ms.ToArray();
        }
        // GDI+ needs the stream to stay open for the image's lifetime (animated GIF frames are read lazily).
        return Image.FromStream(new MemoryStream(cache));
    }

    /// <summary>Standard look for every window: the system dialog font, the app icon, DPI scaling.</summary>
    public static void Prepare(Form form, string title)
    {
        form.Text = title;
        form.Font = SystemFonts.MessageBoxFont ?? form.Font;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.Icon = AppIcon;
        form.StartPosition = FormStartPosition.CenterScreen;
    }

    public static Label MakeLabel(string text, bool wrap = false) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = wrap ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left,
        Margin = new Padding(3, 6, 3, 3),
    };

    public static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(88, 26), UseVisualStyleBackColor = true };
        b.Click += onClick;
        return b;
    }

    public static DogeBox MakeDoge(int size) => new(size);

    /// <summary>Switches the doge between dancing (busy) and sitting still (idle).</summary>
    public static void SetDancing(DogeBox box, bool dancing) => box.Dancing = dancing;

    public static void EnableDoubleBuffer(ListView list)
    {
        // Stops flicker while many rows update. Protected property, so set it via reflection.
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(list, true);
    }

    // ------------------------------------------------------------------ wording

    public static string ActionWord(ConversionJob job) =>
        job.Squeeze != SqueezeLevel.None || job.TargetBytes is not null ? "Compressing" : "Converting";

    /// <summary>Real state of a job in plain words, e.g. "Converting 45%".</summary>
    public static string Status(ConversionJob job) => job.Status switch
    {
        JobStatus.Pending => "Waiting",
        JobStatus.Running => job.Progress.Stage switch
        {
            ConversionStage.Preparing => "Opening...",
            ConversionStage.Saving => "Saving...",
            _ => job.Progress.Percent is { } p ? $"{ActionWord(job)} {p:0}%" : ActionWord(job) + "...",
        },
        JobStatus.Succeeded => "Done",
        JobStatus.Skipped => $"Skipped (already {job.TargetFormat.ToUpperInvariant()})",
        JobStatus.Cancelled => "Cancelled",
        JobStatus.Failed when job.Error is ProviderException { Kind: ProviderErrorKind.NotSmaller } => "Not smaller - kept original",
        JobStatus.Failed when job.Error is ProviderException { Kind: ProviderErrorKind.TooBigForTarget } => "Can't fit - kept original",
        JobStatus.Failed => "Failed",
        _ => job.Status.ToString(),
    };

    /// <summary>What came out: size change on success, the reason on failure.</summary>
    public static string Result(ConversionJob job) => job.Status switch
    {
        JobStatus.Succeeded when job.Result is { } r => $"{Path.GetFileName(r.OutputPath)}   {CompressionPresets.SizeChange(job.SourceBytes, r.OutputBytes)}",
        JobStatus.Failed when job.Error is { } e => FirstLine(e is ProviderException ? e.Message : DogeMessages.Explain(e)),
        _ => "",
    };

    public static string FirstLine(string s)
    {
        var i = s.IndexOfAny(['\r', '\n']);
        return (i >= 0 ? s[..i] : s).Trim();
    }

    /// <summary>Overall percentage for a set of jobs, or null when nothing measurable has happened yet.</summary>
    public static int? OverallPercent(IReadOnlyCollection<ConversionJob> jobs)
    {
        if (jobs.Count == 0) return null;
        double sum = 0;
        var measurable = false;
        foreach (var j in jobs)
        {
            if (j.IsFinished) { sum += 100; measurable = true; }
            else if (j.Status == JobStatus.Running && j.Progress.Percent is { } p) { sum += p; measurable = true; }
        }
        return measurable ? (int)Math.Clamp(sum / jobs.Count, 0, 100) : null;
    }

    public static void ShowProgress(ProgressBar bar, int? percent)
    {
        if (percent is { } p)
        {
            if (bar.Style != ProgressBarStyle.Continuous) bar.Style = ProgressBarStyle.Continuous;
            bar.Value = p;
        }
        else if (bar.Style != ProgressBarStyle.Marquee)
        {
            bar.Style = ProgressBarStyle.Marquee;
        }
    }

    public static void ResetProgress(ProgressBar bar)
    {
        bar.Style = ProgressBarStyle.Continuous;
        bar.Value = 0;
    }

    /// <summary>"Video 720p at 2.5 Mbps, audio 128 kbps; photos JPEG 75%, max 2560 px; other files: ZIP"</summary>
    public static string LevelHint(SqueezeLevel level)
    {
        if (level == SqueezeLevel.None)
            return "Full quality. Files are converted, not made smaller.";
        var v = CompressionPresets.ForVideo(level, 3840, 2160, 0)!;
        var img = CompressionPresets.ForImage(level);
        return $"Video {v.Height}p at {v.VideoBitrate / 1_000_000.0:0.#} Mbps, audio {CompressionPresets.AudioBitrate(level) / 1000} kbps; " +
               $"photos {img.Quality * 100:0}% quality, max {img.MaxLongSide} px; other files go into a ZIP.";
    }

    /// <summary>What "fit under a size" does, for the hint under the Compression box.</summary>
    public static string TargetHint(long bytes) =>
        $"Aims for just under {bytes / (double)CompressionPresets.Megabyte:0.#} MB: video and audio bitrate is worked out from the length " +
        "(and checked afterwards), pictures get the best quality that fits, other files go into a ZIP. " +
        "With Automatic, files already small enough are left alone.";

    /// <summary>"about 3 min left", from real progress so far. Null until there's enough to go on.</summary>
    public static string? Remaining(DateTime startedAt, double? percent)
    {
        if (percent is not { } p || p < 2 || p >= 100) return null;
        var elapsed = DateTime.Now - startedAt;
        if (elapsed.TotalSeconds < 3) return null;
        var left = TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - p) / p);
        return left.TotalSeconds < 10 ? "a few seconds left"
            : left.TotalSeconds < 60 ? $"about {Math.Ceiling(left.TotalSeconds / 5) * 5:0} s left"
            : left.TotalMinutes < 90 ? $"about {Math.Ceiling(left.TotalMinutes):0} min left"
            : $"about {left.TotalHours:0.#} h left";
    }

    public static string Elapsed(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

    public static void TryOpen(IWin32Window? owner, string path, bool reveal)
    {
        try
        {
            if (reveal) ShellHelper.Reveal(path);
            else ShellHelper.Open(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "KwikKonvert", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>An entry in the Compression box: a quality level, or "fit under N bytes".</summary>
internal sealed record CompressionChoice(SqueezeLevel Level, long? TargetBytes = null, bool IsCustom = false)
{
    public bool Shrinks => Level != SqueezeLevel.None || TargetBytes is not null;

    /// <summary>The level handed to the queue: size targets count as compressing (names get " (small)").</summary>
    public SqueezeLevel QueueLevel => TargetBytes is not null ? SqueezeLevel.Normal : Level;
}

/// <summary>A value with display text, for combo boxes.</summary>
internal sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
