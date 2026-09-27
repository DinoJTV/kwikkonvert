using KwikKonvert.Core.Models;

namespace KwikKonvert.Core.Providers;

/// <summary>
/// A conversion engine. The UI and queue only talk to this interface. The Windows app supplies one built on
/// Windows' own codecs (WIC for images, Media Foundation for audio/video), so nothing leaves the PC.
/// </summary>
public interface IConversionProvider
{
    string Name { get; }

    /// <summary>Converts and writes the result to <see cref="ConversionRequest.OutputPath"/> (or the next free name).</summary>
    Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Why a conversion failed, so the UI can pick an honest heading.</summary>
public enum ProviderErrorKind
{
    /// <summary>Windows has no codec for this input or output on this PC.</summary>
    Unsupported,
    /// <summary>The codec started but failed (corrupt file, odd encoding…). The message is Windows' own.</summary>
    ConversionFailed,
    /// <summary>Missing, empty or unreadable file / folder.</summary>
    LocalFile,
    /// <summary>Compressing didn't make the file smaller, so the result was thrown away.</summary>
    NotSmaller,
    /// <summary>Even the smallest attempt was bigger than the requested target size.</summary>
    TooBigForTarget,
}

public sealed class ProviderException : Exception
{
    public ProviderErrorKind Kind { get; }

    public ProviderException(ProviderErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
