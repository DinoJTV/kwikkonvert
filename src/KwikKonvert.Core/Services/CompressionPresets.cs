using KwikKonvert.Core.Models;

namespace KwikKonvert.Core.Services;

/// <summary>Target video settings for a compression level. Sizes are even numbers (H.264 needs that).</summary>
public sealed record VideoPlan(uint Width, uint Height, uint VideoBitrate, uint AudioBitrate);

/// <summary>Target image settings: encoder quality (0–1, for JPEG/HEIC/JPEG-XR) and the longest side in pixels (0 = keep).</summary>
public sealed record ImagePlan(float Quality, uint MaxLongSide);

/// <summary>
/// What each compression level means. Numbers follow common "good enough" presets:
///   Light   ≈ 1080p / 5 Mbps video, 160 kbps audio, JPEG 85 %, images up to 3840 px
///   Normal  ≈  720p / 2.5 Mbps,     128 kbps,       JPEG 75 %, up to 2560 px
///   Strong  ≈  480p / 1.2 Mbps,      96 kbps,       JPEG 60 %, up to 1920 px
///   Maximum ≈  360p / 0.6 Mbps,      64 kbps,       JPEG 45 %, up to 1280 px
/// Nothing is ever scaled up, and video bitrate is always kept below the source's.
/// </summary>
public static class CompressionPresets
{
    public static string Describe(SqueezeLevel level) => level switch
    {
        SqueezeLevel.None => "None - keep quality",
        SqueezeLevel.Light => "Light - barely noticeable",
        SqueezeLevel.Normal => "Normal - good balance",
        SqueezeLevel.Strong => "Strong - visibly lower quality",
        SqueezeLevel.Maximum => "Maximum - smallest file",
        _ => level.ToString(),
    };

    /// <summary>null for <see cref="SqueezeLevel.None"/> (convert at the source's own quality).</summary>
    public static VideoPlan? ForVideo(SqueezeLevel level, uint srcWidth, uint srcHeight, uint srcBitrate)
    {
        if (level == SqueezeLevel.None) return null;
        var (maxShortSide, bitrate, audio) = level switch
        {
            SqueezeLevel.Light => (1080u, 5_000_000u, 160_000u),
            SqueezeLevel.Normal => (720u, 2_500_000u, 128_000u),
            SqueezeLevel.Strong => (480u, 1_200_000u, 96_000u),
            _ => (360u, 600_000u, 64_000u),
        };

        uint w = srcWidth, h = srcHeight;
        if (w > 0 && h > 0)
        {
            // Limit the SHORT side so portrait phone videos are treated like landscape ones.
            var shortSide = Math.Min(w, h);
            if (shortSide > maxShortSide)
            {
                var scale = (double)maxShortSide / shortSide;
                w = (uint)Math.Round(w * scale);
                h = (uint)Math.Round(h * scale);
            }
            w = Math.Max(2, w & ~1u);
            h = Math.Max(2, h & ~1u);
        }

        // Must end up smaller than the source: if the preset is already above the source's own bitrate, go well below it.
        if (srcBitrate > 0 && bitrate > srcBitrate * 0.8)
            bitrate = (uint)(srcBitrate * 0.6);
        bitrate = Math.Max(bitrate, 150_000u);

        return new VideoPlan(w, h, bitrate, audio);
    }

    /// <summary>Audio bitrate in bits/second. Never higher than the source's when that is known.</summary>
    public static uint AudioBitrate(SqueezeLevel level, uint srcBitrate = 0)
    {
        var target = level switch
        {
            SqueezeLevel.None => 192_000u,
            SqueezeLevel.Light => 160_000u,
            SqueezeLevel.Normal => 128_000u,
            SqueezeLevel.Strong => 96_000u,
            _ => 64_000u,
        };
        if (level != SqueezeLevel.None && srcBitrate > 0 && target >= srcBitrate)
            target = Math.Max(48_000u, (uint)(srcBitrate * 0.7));
        return target;
    }

    public static ImagePlan ForImage(SqueezeLevel level) => level switch
    {
        SqueezeLevel.None => new ImagePlan(0.92f, 0),
        SqueezeLevel.Light => new ImagePlan(0.85f, 3840),
        SqueezeLevel.Normal => new ImagePlan(0.75f, 2560),
        SqueezeLevel.Strong => new ImagePlan(0.60f, 1920),
        _ => new ImagePlan(0.45f, 1280),
    };

    /// <summary>Fits (w, h) inside a square of maxLongSide, keeping the aspect ratio. Never enlarges.</summary>
    public static (uint w, uint h) Fit(uint w, uint h, uint maxLongSide)
    {
        if (maxLongSide == 0 || w == 0 || h == 0) return (w, h);
        var longSide = Math.Max(w, h);
        if (longSide <= maxLongSide) return (w, h);
        var scale = (double)maxLongSide / longSide;
        return (Math.Max(1, (uint)Math.Round(w * scale)), Math.Max(1, (uint)Math.Round(h * scale)));
    }

    // ------------------------------------------------------------------ target size ("fit under 10 MB")

    /// <summary>Sizes are decimal megabytes (1 MB = 1,000,000 bytes), which is at or under every site's limit.</summary>
    public const long Megabyte = 1_000_000;

    /// <summary>Preset targets: Discord's free upload limit and the usual email attachment limit.</summary>
    public static readonly (int MB, string Label)[] TargetPresets =
    [
        (10, "Fit under 10 MB (Discord)"),
        (25, "Fit under 25 MB (email)"),
    ];

    public static string DescribeTarget(long bytes) => $"Fit under {bytes / (double)Megabyte:0.#} MB";

    /// <summary>Lowest video bitrate that still gives a watchable picture. Below this, KwikKonvert says the target is too small.</summary>
    public const uint MinTargetVideoBitrate = 100_000;

    /// <summary>Container overhead + encoder overshoot allowance.</summary>
    public const double TargetSafety = 0.92;

    /// <summary>
    /// Bitrates that make a video of <paramref name="seconds"/> land just under <paramref name="targetBytes"/>.
    /// Resolution follows the bitrate (a tiny bitrate at 4K looks far worse than the same bitrate at 480p).
    /// Returns null when the target is too small for this length.
    /// </summary>
    public static VideoPlan? ForTargetSize(long targetBytes, double seconds, uint srcWidth, uint srcHeight, uint srcVideoBitrate, bool hasAudio)
    {
        if (seconds <= 0 || targetBytes <= 0) return null;
        var totalBps = targetBytes * 8.0 * TargetSafety / seconds;
        uint audio = 0;
        if (hasAudio)
            audio = totalBps >= 1_500_000 ? 128_000u : 96_000u; // AAC in MP4 only offers 96-192 kbps
        var video = totalBps - audio;
        if (video < MinTargetVideoBitrate) return null;
        if (srcVideoBitrate > 0) video = Math.Min(video, srcVideoBitrate); // never "compress" upwards

        uint maxShortSide = video switch
        {
            >= 4_000_000 => 1080,
            >= 2_000_000 => 720,
            >= 1_000_000 => 540,
            >= 500_000 => 480,
            >= 250_000 => 360,
            _ => 240,
        };
        var (w, h) = FitShortSide(srcWidth, srcHeight, maxShortSide);
        return new VideoPlan(w, h, (uint)video, audio);
    }

    /// <summary>Audio-only bitrate for a target size, or null if even the lowest bitrate is too big.</summary>
    public static uint? AudioForTargetSize(long targetBytes, double seconds, bool aac)
    {
        if (seconds <= 0 || targetBytes <= 0) return null;
        var bps = (uint)Math.Min(uint.MaxValue, targetBytes * 8.0 * TargetSafety / seconds);
        var snapped = aac ? SnapAac(bps) : SnapMp3(bps);
        return snapped <= bps ? snapped : null;
    }

    /// <summary>Windows' AAC encoder only accepts 96, 128, 160 or 192 kbps: the largest one not above <paramref name="bps"/> (96k minimum).</summary>
    public static uint SnapAac(uint bps) => Snap(bps, [96_000, 128_000, 160_000, 192_000]);

    /// <summary>Standard MP3 bitrates: the largest one not above <paramref name="bps"/> (32k minimum).</summary>
    public static uint SnapMp3(uint bps) =>
        Snap(bps, [32_000, 40_000, 48_000, 56_000, 64_000, 80_000, 96_000, 112_000, 128_000, 160_000, 192_000, 224_000, 256_000, 320_000]);

    private static uint Snap(uint bps, uint[] allowed)
    {
        var pick = allowed[0];
        foreach (var a in allowed) if (a <= bps) pick = a;
        return pick;
    }

    /// <summary>Scales so the short side is at most <paramref name="maxShortSide"/>; even numbers, never enlarges.</summary>
    public static (uint w, uint h) FitShortSide(uint w, uint h, uint maxShortSide)
    {
        if (w == 0 || h == 0) return (w, h);
        var shortSide = Math.Min(w, h);
        if (shortSide > maxShortSide)
        {
            var scale = (double)maxShortSide / shortSide;
            w = (uint)Math.Round(w * scale);
            h = (uint)Math.Round(h * scale);
        }
        return (Math.Max(2, w & ~1u), Math.Max(2, h & ~1u));
    }

    /// <summary>"12.3 MB → 3.1 MB (-75%)"</summary>
    public static string SizeChange(long before, long after)
    {
        if (before <= 0) return FileService.HumanSize(after);
        var pct = (after - before) * 100.0 / before;
        return $"{FileService.HumanSize(before)} → {FileService.HumanSize(after)} ({pct:+0;-0;0}%)";
    }
}
