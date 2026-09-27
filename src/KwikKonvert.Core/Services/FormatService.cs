namespace KwikKonvert.Core.Services;

/// <summary><see cref="Other"/> = any other file (documents, data…); those can only be compressed into a ZIP.</summary>
public enum FormatCategory { Image, Video, Audio, Other }

public sealed record FormatInfo(string Id, string Description, FormatCategory Category, bool CanRead, bool CanWrite)
{
    /// <summary>Upper-case label for the UI, e.g. "MP4".</summary>
    public string Label => Id.ToUpperInvariant();

    /// <summary>File extension for output files (without the dot).</summary>
    public string Extension => Id;
}

/// <summary>
/// Knows which formats this PC can read and write, and which targets make sense for a source.
///
/// Everything is converted locally by Windows' built-in codecs:
///   • Images — Windows Imaging Component (WIC). The image list is detected at startup from the codecs actually
///     installed, so extras like the HEIF/WebP/RAW extensions from the Microsoft Store show up automatically.
///   • Audio/Video — Media Foundation via Windows.Media.Transcoding (MP4/H.264, WMV, MP3, M4A/AAC, WAV, WMA, FLAC).
/// </summary>
public sealed class FormatService
{
    private readonly Dictionary<string, FormatInfo> _formats = new(StringComparer.OrdinalIgnoreCase);

    // Extensions that are the same format, so e.g. JPEG → JPG isn't offered as a "conversion".
    private static readonly string[][] Equivalent =
    [
        ["jpg", "jpeg", "jpe", "jfif"],
        ["tif", "tiff"],
        ["heic", "heif", "hif"],
        ["jxr", "wdp", "hdp"],
    ];

    // What people usually want. Only used for ordering until the user's own habits take over.
    private static readonly Dictionary<string, string[]> Popular = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mov"] = ["mp4", "wmv", "mp3"],
        ["mkv"] = ["mp4", "wmv", "mp3"],
        ["avi"] = ["mp4", "wmv"],
        ["webm"] = ["mp4", "mp3"],
        ["wmv"] = ["mp4", "mp3"],
        ["mp4"] = ["mp3", "wmv", "m4a"],
        ["m4v"] = ["mp4", "mp3"],
        ["wav"] = ["mp3", "m4a", "flac"],
        ["flac"] = ["mp3", "m4a", "wav"],
        ["m4a"] = ["mp3", "wav"],
        ["mp3"] = ["wav", "m4a", "flac"],
        ["wma"] = ["mp3", "m4a"],
        ["webp"] = ["png", "jpg", "gif"],
        ["heic"] = ["jpg", "png"],
        ["heif"] = ["jpg", "png"],
        ["png"] = ["jpg", "bmp", "gif"],
        ["jpg"] = ["png", "bmp", "gif"],
        ["jpeg"] = ["png", "bmp", "gif"],
        ["bmp"] = ["png", "jpg"],
        ["gif"] = ["png", "jpg"],
        ["tif"] = ["jpg", "png"],
        ["tiff"] = ["jpg", "png"],
    };

    private static readonly Dictionary<FormatCategory, string[]> CategoryDefaults = new()
    {
        [FormatCategory.Image] = ["png", "jpg", "bmp", "gif", "tiff"],
        [FormatCategory.Video] = ["mp4", "mp3", "wmv"],
        [FormatCategory.Audio] = ["mp3", "m4a", "wav", "flac"],
        [FormatCategory.Other] = ["zip"],
    };

    public FormatService(IEnumerable<FormatInfo> formats)
    {
        foreach (var f in formats)
        {
            if (_formats.TryGetValue(f.Id, out var existing))
                _formats[f.Id] = existing with { CanRead = existing.CanRead || f.CanRead, CanWrite = existing.CanWrite || f.CanWrite };
            else
                _formats[f.Id] = f;
        }
    }

    public IReadOnlyCollection<FormatInfo> All => _formats.Values;

    public FormatInfo? Get(string id) => _formats.TryGetValue(id, out var f) ? f : null;

    /// <summary>Everything this PC can read, i.e. the extensions KwikKonvert hooks in Explorer.</summary>
    public IEnumerable<FormatInfo> Readable => _formats.Values.Where(f => f.CanRead);

    public IEnumerable<FormatInfo> Writable => _formats.Values.Where(f => f.CanWrite);

    public FormatInfo? Detect(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (ext.Length == 0) return null;
        var f = Get(ext);
        return f is { CanRead: true } ? f : null;
    }

    /// <summary>Like <see cref="Detect"/>, but any other file comes back as a generic "Other" format (compressible to ZIP).</summary>
    public FormatInfo DetectOrGeneric(string path)
    {
        var known = Detect(path);
        if (known is not null) return known;
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return new FormatInfo(ext.Length > 0 ? ext : "file", ext.Length > 0 ? ext.ToUpperInvariant() + " File" : "File",
            FormatCategory.Other, CanRead: true, CanWrite: false);
    }

    /// <summary>The format "Compress" produces by default: MP4 for video, MP3/M4A for audio, JPG for images, ZIP otherwise.</summary>
    public string CompressTargetFor(FormatInfo source)
    {
        string? pick = source.Category switch
        {
            FormatCategory.Video => "mp4",
            FormatCategory.Audio => source.Id is "m4a" or "aac" ? "m4a" : "mp3",
            FormatCategory.Image => "jpg",
            _ => null,
        };
        return pick is not null && Get(pick) is { CanWrite: true } ? pick : "zip";
    }

    public static string StripKnownExtension(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    public static bool AreEquivalent(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        Equivalent.Any(g => g.Contains(a.ToLowerInvariant()) && g.Contains(b.ToLowerInvariant()));

    /// <summary>Writable targets for a source, excluding the source's own format. Unordered.</summary>
    public IReadOnlyList<FormatInfo> TargetsFor(FormatInfo source)
    {
        IEnumerable<FormatInfo> candidates = source.Category switch
        {
            FormatCategory.Image => Writable.Where(f => f.Category == FormatCategory.Image),
            // Video can become another video, or have its soundtrack extracted to audio.
            FormatCategory.Video => Writable.Where(f => f.Category is FormatCategory.Video or FormatCategory.Audio),
            FormatCategory.Audio => Writable.Where(f => f.Category == FormatCategory.Audio),
            _ => Writable.Where(f => f.Category == FormatCategory.Other),
        };
        return candidates.Where(f => !AreEquivalent(f.Id, source.Id)).ToList();
    }

    /// <summary>Typical targets for the source, most likely first. Only contains formats in <see cref="TargetsFor"/>.</summary>
    public IReadOnlyList<string> PopularTargets(FormatInfo source)
    {
        var valid = TargetsFor(source).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = Popular.TryGetValue(source.Id, out var p) ? p : [];
        return list.Concat(CategoryDefaults.GetValueOrDefault(source.Category, []))
                   .Where(valid.Contains)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>Targets shared by all sources (for "Konvert all to"). Files already in the target format get skipped.</summary>
    public IReadOnlyList<FormatInfo> CommonTargets(IEnumerable<FormatInfo> sources)
    {
        HashSet<string>? common = null;
        foreach (var s in sources.DistinctBy(s => s.Id))
        {
            var set = TargetsFor(s).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (s.CanWrite) set.Add(s.Id);
            if (common is null) common = set; else common.IntersectWith(set);
        }
        return (common ?? []).Select(Get).Where(f => f is { CanWrite: true }).Cast<FormatInfo>().OrderBy(f => f.Id).ToList();
    }
}

/// <summary>
/// Builds the format list for Windows' built-in codecs. Image formats come from the codecs detected on the PC;
/// audio/video are the Media Foundation formats Windows 10/11 ship with.
/// </summary>
public static class LocalFormats
{
    /// <summary>Media Foundation inputs Windows 10/11 decode out of the box (some need the free Store extensions, e.g. WebM/VP9).</summary>
    public static readonly (string id, string desc)[] VideoInputs =
    [
        ("mp4", "MP4 Video"), ("m4v", "MPEG-4 Video"), ("mov", "QuickTime Movie"), ("wmv", "Windows Media Video"),
        ("avi", "AVI Video"), ("mkv", "Matroska Video"), ("3gp", "3GPP Video"), ("3g2", "3GPP2 Video"),
        ("mts", "AVCHD Video"), ("m2ts", "Blu-ray / AVCHD Video"), ("ts", "MPEG Transport Stream"), ("asf", "Advanced Systems Format"),
        ("webm", "WebM Video"),
    ];

    public static readonly (string id, string desc)[] VideoOutputs = [("mp4", "MP4 Video (H.264 + AAC)"), ("wmv", "Windows Media Video")];

    public static readonly (string id, string desc)[] AudioInputs =
    [
        ("mp3", "MP3 Audio"), ("m4a", "MPEG-4 Audio"), ("aac", "AAC Audio"), ("wav", "WAV Audio"),
        ("wma", "Windows Media Audio"), ("flac", "FLAC Lossless Audio"),
    ];

    public static readonly (string id, string desc)[] AudioOutputs =
    [
        ("mp3", "MP3 Audio"), ("m4a", "MPEG-4 Audio (AAC)"), ("wav", "WAV Audio"), ("wma", "Windows Media Audio"), ("flac", "FLAC Lossless Audio"),
    ];

    private static readonly Dictionary<string, string> ImageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "PNG Image", ["jpg"] = "JPEG Image", ["jpeg"] = "JPEG Image", ["jpe"] = "JPEG Image", ["jfif"] = "JPEG Image",
        ["bmp"] = "Bitmap Image", ["dib"] = "Bitmap Image", ["rle"] = "Bitmap Image", ["gif"] = "GIF Image",
        ["tif"] = "TIFF Image", ["tiff"] = "TIFF Image", ["ico"] = "Windows Icon", ["cur"] = "Windows Cursor",
        ["jxr"] = "JPEG XR Image", ["wdp"] = "JPEG XR Image", ["hdp"] = "JPEG XR Image", ["dds"] = "DirectDraw Surface",
        ["heic"] = "HEIC Image", ["heif"] = "HEIF Image", ["hif"] = "HEIF Image", ["avif"] = "AVIF Image", ["webp"] = "WebP Image",
        ["dng"] = "Digital Negative (RAW)", ["cr2"] = "Canon RAW", ["cr3"] = "Canon RAW", ["nef"] = "Nikon RAW", ["arw"] = "Sony RAW",
        ["orf"] = "Olympus RAW", ["rw2"] = "Panasonic RAW", ["raf"] = "Fujifilm RAW", ["pef"] = "Pentax RAW", ["srw"] = "Samsung RAW",
    };

    public static string ImageDescription(string ext) =>
        ImageNames.TryGetValue(ext, out var n) ? n : ext.ToUpperInvariant() + " Image";

    /// <param name="imageReadExtensions">Extensions of installed WIC decoders (e.g. from BitmapDecoder.GetDecoderInformationEnumerator()).</param>
    /// <param name="imageWriteIds">Output image formats with an installed WIC encoder (png, jpg, bmp, gif, tiff, jxr, heic…).</param>
    public static FormatService Create(IEnumerable<string> imageReadExtensions, IEnumerable<string> imageWriteIds)
    {
        var list = new List<FormatInfo>();
        foreach (var ext in imageReadExtensions.Select(Norm).Where(e => e.Length > 0).Distinct())
            list.Add(new FormatInfo(ext, ImageDescription(ext), FormatCategory.Image, CanRead: true, CanWrite: false));
        foreach (var id in imageWriteIds.Select(Norm).Where(e => e.Length > 0).Distinct())
            list.Add(new FormatInfo(id, ImageDescription(id), FormatCategory.Image, CanRead: true, CanWrite: true));

        foreach (var (id, d) in VideoInputs) list.Add(new FormatInfo(id, d, FormatCategory.Video, true, false));
        foreach (var (id, d) in VideoOutputs) list.Add(new FormatInfo(id, d, FormatCategory.Video, true, true));
        foreach (var (id, d) in AudioInputs) list.Add(new FormatInfo(id, d, FormatCategory.Audio, true, false));
        foreach (var (id, d) in AudioOutputs) list.Add(new FormatInfo(id, d, FormatCategory.Audio, true, true));
        // ZIP is output-only: any file can be compressed into one (zip files themselves are treated as ordinary files).
        list.Add(new FormatInfo("zip", "ZIP Archive (compressed)", FormatCategory.Other, CanRead: false, CanWrite: true));
        return new FormatService(list);
    }

    /// <summary>What a stock Windows 10/11 install provides — used by tests and as a fallback.</summary>
    public static FormatService CreateStockWindows() => Create(
        ["bmp", "dib", "rle", "gif", "ico", "jpeg", "jpe", "jpg", "jfif", "png", "tiff", "tif", "jxr", "wdp", "dds", "webp"],
        ["png", "jpg", "bmp", "gif", "tiff", "jxr"]);

    private static string Norm(string ext) => ext.Trim().TrimStart('.').ToLowerInvariant();
}
