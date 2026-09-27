using KwikKonvert.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace KwikKonvert.Platform;

/// <summary>
/// Asks Windows which image codecs are installed on this PC, so KwikKonvert only offers formats that really work here.
/// Installing e.g. "HEIF Image Extensions" or "Raw Image Extension" from the Microsoft Store makes them appear.
/// </summary>
public static class WindowsCodecs
{
    /// <summary>Our format id → WIC encoder, for the encoders Windows ships (HEIF only works with the HEVC extension).</summary>
    public static readonly (string id, Guid encoder)[] ImageEncoders =
    [
        ("png", BitmapEncoder.PngEncoderId),
        ("jpg", BitmapEncoder.JpegEncoderId),
        ("bmp", BitmapEncoder.BmpEncoderId),
        ("gif", BitmapEncoder.GifEncoderId),
        ("tiff", BitmapEncoder.TiffEncoderId),
        ("jxr", BitmapEncoder.JpegXREncoderId),
        ("heic", BitmapEncoder.HeifEncoderId),
    ];

    public static Guid? EncoderFor(string id)
    {
        foreach (var (fid, enc) in ImageEncoders)
            if (FormatService.AreEquivalent(fid, id)) return enc;
        return null;
    }

    public static FormatService CreateFormatService()
    {
        try
        {
            var readExts = new List<string>();
            foreach (var info in BitmapDecoder.GetDecoderInformationEnumerator())
                readExts.AddRange(info.FileExtensions);

            var installed = BitmapEncoder.GetEncoderInformationEnumerator().Select(i => i.CodecId).ToHashSet();
            var writeIds = new List<string>();
            foreach (var (id, enc) in ImageEncoders)
            {
                if (!installed.Contains(enc)) continue;
                // HEIF's encoder can be registered while the HEVC codec it needs is missing: prove it works first.
                if (id == "heic" && !Task.Run(() => CanEncodeAsync(enc)).GetAwaiter().GetResult()) continue;
                writeIds.Add(id);
            }
            return LocalFormats.Create(readExts, writeIds);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Codec detection failed, using stock list: " + ex.Message);
            return LocalFormats.CreateStockWindows();
        }
    }

    private static async Task<bool> CanEncodeAsync(Guid encoderId)
    {
        try
        {
            using var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 16, 16, BitmapAlphaMode.Premultiplied);
            using var mem = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(encoderId, mem);
            enc.SetSoftwareBitmap(bmp);
            await enc.FlushAsync();
            return mem.Size > 0;
        }
        catch
        {
            return false;
        }
    }
}
