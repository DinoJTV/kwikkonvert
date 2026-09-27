namespace KwikKonvert.Core.Services;

public enum OutputLocation { SameFolder, Downloads, Desktop, Custom }

public enum CollisionBehavior
{
    /// <summary>holiday.mp4 → holiday (1).mp4</summary>
    AddNumber,
    /// <summary>holiday.mp4 → holiday 2026-09-26 17.42.10.mp4</summary>
    AddTimestamp,
}

/// <summary>Works out where converted files go and guarantees nothing is ever silently overwritten.</summary>
public sealed class FileService
{
    private readonly Func<OutputLocation, string?> _knownFolder;

    /// <param name="knownFolderResolver">Resolves Downloads/Desktop on the current OS (the Windows app passes the shell's real paths).</param>
    public FileService(Func<OutputLocation, string?> knownFolderResolver) => _knownFolder = knownFolderResolver;

    public const long ChonkyBytes = 500L * 1024 * 1024;

    /// <summary>The folder the output should go to, falling back to the source folder if the configured one is unusable.</summary>
    public string OutputFolderFor(string sourcePath, OutputLocation location, string? customFolder)
    {
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        string? dir = location switch
        {
            OutputLocation.SameFolder => sourceDir,
            OutputLocation.Custom => customFolder,
            _ => _knownFolder(location),
        };
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : sourceDir;
    }

    /// <summary>The first free output path, e.g. holiday.mp4, then holiday (1).mp4, holiday (2).mp4 …</summary>
    public static string FreeOutputPath(string folder, string stem, string extension, CollisionBehavior behavior = CollisionBehavior.AddNumber)
    {
        extension = extension.TrimStart('.');
        var candidate = Path.Combine(folder, $"{stem}.{extension}");
        if (!Exists(candidate)) return candidate;

        if (behavior == CollisionBehavior.AddTimestamp)
        {
            var stamped = Path.Combine(folder, $"{stem} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.{extension}");
            if (!Exists(stamped)) return stamped;
        }

        for (var i = 1; i < 10_000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}).{extension}");
            if (!Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}).{extension}");
    }

    public string PlanOutputPath(string sourcePath, string targetExtension, OutputLocation location, string? customFolder, CollisionBehavior behavior)
    {
        var folder = OutputFolderFor(sourcePath, location, customFolder);
        var stem = FormatService.StripKnownExtension(Path.GetFileName(sourcePath));
        return FreeOutputPath(folder, stem, targetExtension, behavior);
    }

    /// <summary>
    /// Moves a finished temp file to <paramref name="desiredPath"/>, or to the next free "name (n).ext" if something
    /// appeared there in the meantime (e.g. another conversion in the same batch). Never overwrites. Returns the final path.
    /// </summary>
    public static string MoveIntoPlaceNoOverwrite(string tempPath, string desiredPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(desiredPath))!;
        var ext = Path.GetExtension(desiredPath);
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var target = desiredPath;
        for (var i = 1; i < 10_000; i++)
        {
            try
            {
                if (!Exists(target))
                {
                    File.Move(tempPath, target, overwrite: false);
                    return target;
                }
            }
            catch (IOException) when (Exists(target))
            {
                // Lost a race with another writer; try the next name.
            }
            target = Path.Combine(folder, $"{StripNumberSuffix(stem)} ({i}){ext}");
        }
        throw new IOException($"Could not find a free file name for {desiredPath}.");
    }

    private static string StripNumberSuffix(string stem)
    {
        // "holiday (1)" → "holiday" so retries count up cleanly instead of "holiday (1) (1)".
        var open = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && stem.EndsWith(')') && int.TryParse(stem.AsSpan(open + 2, stem.Length - open - 3), out _))
            return stem[..open];
        return stem;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
