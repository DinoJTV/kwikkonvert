using KwikKonvert.Core.Models;
using KwikKonvert.Core.Providers;

namespace KwikKonvert.Core.Services;

/// <summary>All the doge-speak in one place. Status flavour text only — real state is always shown separately.</summary>
public static class DogeMessages
{
    public const string Tagline = "such files. many formats. wow.";

    public static readonly string[] Preparing =
        ["doge sniffs file...", "such inspect...", "reading many byte...", "much looking", "doge opening file..."];

    public static readonly string[] Converting =
        ["such converting...", "many format...", "very processing", "doge is working...", "computer go brrrr...",
         "pixel go brrrr...", "much technology", "wow compression", "doing computer things..."];

    public static readonly string[] Saving =
        ["bringing file home...", "such save...", "writing the goods...", "almost there fren...", "many byte landing..."];

    public static readonly string[] Subtitles = ["very speed ⚡", "much local", "so offline", "wow"];

    public static readonly string[] ErrorHeadings = ["much error.", "doge confused.", "conversion went bonk.", "doge has encountered problem"];

    public const string Complete = "WOW. KONVERTED.";
    public const string CompleteSub = "very success.";
    public const string BatchComplete = "many file. much success.";
    public const string Speed = "S P E E D";
    public const string Chonky = "that is a chonky file";
    public const string EmptyFile = "bruh where file";
    public const string NoCodec = "doge does not speak this format 😔";
    public const string PatDoge = "wow. pls let doge work.";
    public const string BatchWorking = "🐕 such many files...";

    public static string[] ForStage(ConversionStage stage) => stage switch
    {
        ConversionStage.Preparing => Preparing,
        ConversionStage.Saving => Saving,
        _ => Converting,
    };

    /// <summary>Plain-language label for the real stage, shown under the progress bar.</summary>
    public static string StageLabel(ConversionStage stage) => stage switch
    {
        ConversionStage.Preparing => "OPENING FILE",
        ConversionStage.Converting => "CONVERTING ON THIS PC",
        ConversionStage.Saving => "SAVING",
        ConversionStage.Completed => "DONE",
        ConversionStage.Failed => "FAILED",
        ConversionStage.Cancelled => "CANCELLED",
        _ => stage.ToString().ToUpperInvariant(),
    };

    /// <summary>A human sentence for the real error. The meme heading goes above it; this never gets replaced.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        ProviderException { Kind: ProviderErrorKind.Unsupported } p => p.Message,
        ProviderException { Kind: ProviderErrorKind.LocalFile } p => p.Message,
        ProviderException { Kind: ProviderErrorKind.NotSmaller or ProviderErrorKind.TooBigForTarget } p => p.Message,
        ProviderException p => $"Windows reported:\n{p.Message}",
        UnauthorizedAccessException u => $"Windows would not let KwikKonvert write the file: {u.Message}",
        IOException io => $"File problem: {io.Message}",
        _ => ex.Message,
    };
}

/// <summary>Picks a message for the current stage and rotates it every few seconds, never repeating back-to-back.</summary>
public sealed class MessageRotator
{
    private readonly Random _rng;
    private string? _last;

    public MessageRotator(Random? rng = null) => _rng = rng ?? Random.Shared;

    public string Next(string[] pool)
    {
        if (pool.Length == 1) return _last = pool[0];
        string pick;
        do pick = pool[_rng.Next(pool.Length)]; while (pick == _last);
        return _last = pick;
    }

    /// <summary>2–4 seconds, as requested.</summary>
    public TimeSpan NextInterval() => TimeSpan.FromMilliseconds(2000 + _rng.Next(2000));
}
