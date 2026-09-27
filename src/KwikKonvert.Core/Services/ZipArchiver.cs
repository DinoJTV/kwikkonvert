using System.IO.Compression;
using KwikKonvert.Core.Models;

namespace KwikKonvert.Core.Services;

/// <summary>One file (or empty folder) going into an archive, with its path inside the zip ("Photos/beach.jpg").</summary>
public sealed record ArchiveEntry(string FullPath, string EntryName, bool IsDirectory, long Bytes);

/// <summary>What <see cref="ZipArchiver.Plan"/> decided: the entries, the folder they're relative to, and a name for the zip.</summary>
public sealed record ArchivePlan(IReadOnlyList<ArchiveEntry> Entries, string BaseFolder, string SuggestedName)
{
    public long TotalBytes => Entries.Sum(e => e.Bytes);
    public int FileCount => Entries.Count(e => !e.IsDirectory);
}

public sealed record ArchiveProgress(long BytesDone, long TotalBytes, int FilesDone, int FileCount, string CurrentEntry)
{
    public double Percent => TotalBytes > 0 ? 100.0 * BytesDone / TotalBytes : FileCount > 0 ? 100.0 * FilesDone / FileCount : 100;
}

/// <summary>
/// "Add to archive", like 7-Zip: many files and folders into ONE standard .zip that Windows opens natively.
/// Folders keep their structure; paths inside the zip are relative to the folder the selection came from.
/// </summary>
public static class ZipArchiver
{
    public const int MaxEntries = 100_000;

    public static ArchivePlan Plan(IEnumerable<string> paths)
    {
        var roots = paths.Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                         .Where(p => File.Exists(p) || Directory.Exists(p))
                         .Distinct(PathComparer)
                         .ToList();
        if (roots.Count == 0) return new ArchivePlan([], "", "Archive");

        var baseFolder = CommonParent(roots.Select(r => Path.GetDirectoryName(r) ?? r));
        var entries = new List<ArchiveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string full, bool dir, long bytes)
        {
            if (entries.Count >= MaxEntries) return;
            var name = Path.GetRelativePath(baseFolder, full).Replace('\\', '/');
            if (dir) name = name.TrimEnd('/') + "/";
            if (seen.Add(name)) entries.Add(new ArchiveEntry(full, name, dir, bytes));
        }

        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var root in roots)
        {
            if (File.Exists(root))
            {
                Add(root, false, SafeLength(root));
                continue;
            }
            // Folder: walk it, keeping empty folders as folder entries.
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                var any = false;
                foreach (var f in SafeEnum(() => Directory.EnumerateFiles(dir, "*", opts)))
                {
                    Add(f, false, SafeLength(f));
                    any = true;
                }
                foreach (var d in SafeEnum(() => Directory.EnumerateDirectories(dir, "*", opts)))
                {
                    stack.Push(d);
                    any = true;
                }
                if (!any) Add(dir, true, 0);
            }
        }

        string suggested;
        if (roots.Count == 1)
            suggested = File.Exists(roots[0]) ? Path.GetFileNameWithoutExtension(roots[0]) : Path.GetFileName(roots[0]);
        else
            suggested = Path.GetFileName(baseFolder.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(suggested)) suggested = "Archive";

        return new ArchivePlan(entries, baseFolder, suggested);
    }

    /// <summary>Writes the archive to <paramref name="zipPath"/> (must not exist), reporting real progress by bytes read.</summary>
    public static async Task CreateAsync(ArchivePlan plan, string zipPath, SqueezeLevel level, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var total = plan.TotalBytes;
        var fileCount = plan.FileCount;
        var zipFull = Path.GetFullPath(zipPath);
        long done = 0;
        var filesDone = 0;
        var buffer = new byte[1 << 16];
        var lastReport = DateTime.MinValue;

        await using var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var e in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory)
            {
                archive.CreateEntry(e.EntryName);
                continue;
            }
            if (PathComparer.Equals(Path.GetFullPath(e.FullPath), zipFull)) continue; // never zip the zip itself

            progress?.Report(new ArchiveProgress(done, total, filesDone, fileCount, e.EntryName));
            var entry = archive.CreateEntry(e.EntryName, ZipCompressor.ZipLevelFor(level));
            try { entry.LastWriteTime = File.GetLastWriteTime(e.FullPath); } catch { /* keep default */ }

            await using (var input = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true))
            await using (var output = entry.Open())
            {
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
                    {
                        lastReport = DateTime.UtcNow;
                        progress.Report(new ArchiveProgress(done, total, filesDone, fileCount, e.EntryName));
                    }
                }
            }
            filesDone++;
        }
        progress?.Report(new ArchiveProgress(total, total, filesDone, fileCount, ""));
    }

    // ------------------------------------------------------------------ helpers

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The deepest folder containing all the given folders.</summary>
    public static string CommonParent(IEnumerable<string> folders)
    {
        string[]? common = null;
        var sep = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        foreach (var f in folders)
        {
            var parts = Path.GetFullPath(f).TrimEnd(sep).Split(sep);
            if (common is null) { common = parts; continue; }
            var n = 0;
            while (n < common.Length && n < parts.Length && PathComparer.Equals(common[n], parts[n])) n++;
            common = common[..n];
        }
        if (common is null || common.Length == 0) return Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";
        var joined = string.Join(Path.DirectorySeparatorChar, common);
        // "C:" alone means the drive root; "" means the Unix root.
        if (joined.Length == 0) return Path.DirectorySeparatorChar.ToString();
        if (joined.EndsWith(':')) joined += Path.DirectorySeparatorChar;
        return joined;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static IEnumerable<string> SafeEnum(Func<IEnumerable<string>> list)
    {
        try { return list().ToList(); } catch { return []; }
    }
}
