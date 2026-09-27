namespace KwikKonvert.Core.Models;

/// <summary>Which part of a conversion is running. Everything happens on this PC.</summary>
public enum ConversionStage
{
    /// <summary>Opening the file and checking Windows can convert it.</summary>
    Preparing,
    Converting,
    /// <summary>Writing the result next to the original.</summary>
    Saving,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// A progress report. <see cref="Percent"/> is null whenever there is no real number (e.g. image encoding is a single
/// step), in which case the UI shows an indeterminate bar. We never invent a percentage.
/// </summary>
public sealed record ConversionProgress(ConversionStage Stage, double? Percent)
{
    public bool IsIndeterminate => Percent is null;
}

/// <summary>
/// How much to shrink the file. <see cref="None"/> converts at full quality; the others trade quality for size
/// (lower resolution / bitrate / JPEG quality for media, stronger ZIP compression for everything else).
/// </summary>
public enum SqueezeLevel { None, Light, Normal, Strong, Maximum }

/// <param name="TargetBytes">When set, the output must end up at or below this size ("fit under 10 MB").</param>
public sealed record ConversionRequest(string SourcePath, string TargetFormat, string OutputPath, SqueezeLevel Squeeze = SqueezeLevel.None, long? TargetBytes = null);

public sealed record ConversionResult(string SourcePath, string OutputPath, string TargetFormat, long OutputBytes, TimeSpan Elapsed);
