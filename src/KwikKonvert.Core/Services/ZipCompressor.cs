using System.IO.Compression;
using KwikKonvert.Core.Models;

namespace KwikKonvert.Core.Services;

/// <summary>
/// Lossless compression for files that aren't images, audio or video (documents, logs, data…): the file goes into a
/// standard .zip that Windows opens natively. Nothing about the file itself changes.
/// </summary>
public static class ZipCompressor
{
    public static CompressionLevel ZipLevelFor(SqueezeLevel level) =>
        level is SqueezeLevel.Strong or SqueezeLevel.Maximum ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;

    /// <summary>Writes <paramref name="sourcePath"/> into a new zip at <paramref name="zipPath"/>, reporting real % of bytes read.</summary>
    public static async Task CompressAsync(string sourcePath, string zipPath, SqueezeLevel level, IProgress<double>? progress, CancellationToken ct)
    {
        var total = new FileInfo(sourcePath).Length;
        await using var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);
        var entry = archive.CreateEntry(Path.GetFileName(sourcePath), ZipLevelFor(level));
        entry.LastWriteTime = File.GetLastWriteTime(sourcePath);

        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using var output = entry.Open();
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        var lastReported = -1.0;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            var pct = total > 0 ? 100.0 * done / total : 100;
            if (pct - lastReported >= 1 || done == total)
            {
                lastReported = pct;
                progress?.Report(pct);
            }
        }
    }
}
