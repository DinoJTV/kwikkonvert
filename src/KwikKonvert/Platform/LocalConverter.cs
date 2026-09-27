using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using KwikKonvert.Core.Models;
using KwikKonvert.Core.Providers;
using KwikKonvert.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace KwikKonvert.Platform;

/// <summary>
/// Converts and compresses files on this PC with Windows' own codecs. Nothing is uploaded anywhere.
///   • Images: Windows Imaging Component (decode → sRGB, EXIF orientation applied → optional downscale → encode).
///   • Audio / video: Media Foundation's MediaTranscoder (real progress); compression lowers resolution/bitrate.
///   • Anything else: compressed losslessly into a ZIP.
/// "Fit under N MB" works out the bitrate from the file's length (audio/video) or searches quality/size (pictures),
/// and checks the real result: a video that comes out too big is encoded again with a lower bitrate.
/// Output is written to a temporary file next to the destination and moved into place without overwriting anything.
/// </summary>
public sealed class LocalConverter : IConversionProvider
{
    /// <summary>Attempts at hitting a target size (the encoder's real output can overshoot the requested bitrate).</summary>
    private const int MaxTargetAttempts = 3;

    private readonly FormatService _formats;

    public LocalConverter(FormatService formats) => _formats = formats;

    public string Name => "Windows (on this PC)";

    public async Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var info = new FileInfo(request.SourcePath);
        if (!info.Exists)
            throw new ProviderException(ProviderErrorKind.LocalFile, $"The file '{request.SourcePath}' no longer exists.");
        if (info.Length == 0)
            throw new ProviderException(ProviderErrorKind.LocalFile, "The file is empty (0 bytes), so there is nothing to convert.");

        var source = _formats.DetectOrGeneric(request.SourcePath);
        var target = _formats.Get(request.TargetFormat);
        if (target is not { CanWrite: true })
            throw new ProviderException(ProviderErrorKind.Unsupported, $"Windows on this PC can't write {request.TargetFormat.ToUpperInvariant()} files.");

        progress?.Report(new ConversionProgress(ConversionStage.Preparing, null));

        var outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!;
        Directory.CreateDirectory(outDir);
        var tempPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(request.OutputPath)}.kwikpart-{Guid.NewGuid():N}.{target.Extension}");

        try
        {
            if (target.Category == FormatCategory.Other)
            {
                // ZIP: works for any file, lossless. Aiming for a size means "as small as ZIP can make it".
                progress?.Report(new ConversionProgress(ConversionStage.Converting, 0));
                var zipProgress = progress is null ? null : new InlineProgress<double>(p =>
                    progress.Report(new ConversionProgress(ConversionStage.Converting, Math.Clamp(p, 0, 100))));
                var level = request.TargetBytes is null ? request.Squeeze : SqueezeLevel.Maximum;
                await ZipCompressor.CompressAsync(request.SourcePath, tempPath, level, zipProgress, ct).ConfigureAwait(false);
            }
            else if (source.Category == FormatCategory.Other)
            {
                throw new ProviderException(ProviderErrorKind.Unsupported,
                    $"Windows on this PC can't convert {info.Extension} files. They can only be compressed into a ZIP.");
            }
            else if (target.Category == FormatCategory.Image)
            {
                await ConvertImageAsync(request.SourcePath, tempPath, target, request.Squeeze, request.TargetBytes, progress, ct).ConfigureAwait(false);
            }
            else
            {
                await ConvertMediaAsync(request.SourcePath, tempPath, source, target, request.Squeeze, request.TargetBytes, progress, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            var newSize = new FileInfo(tempPath).Length;

            if (request.TargetBytes is { } limit)
            {
                if (newSize > limit)
                    throw new ProviderException(ProviderErrorKind.TooBigForTarget,
                        $"The smallest version KwikKonvert could make is {FileService.HumanSize(newSize)}, which is over the " +
                        $"{FileService.HumanSize(limit)} limit, so it was discarded. Try a bigger size" +
                        (source.Category == FormatCategory.Other ? "." : ", or a shorter clip."));
            }
            else if (request.Squeeze != SqueezeLevel.None && newSize >= info.Length)
            {
                throw new ProviderException(ProviderErrorKind.NotSmaller,
                    $"The compressed version ({FileService.HumanSize(newSize)}) wasn't smaller than the original " +
                    $"({FileService.HumanSize(info.Length)}), so it was discarded. Try a stronger level or a different format.");
            }

            progress?.Report(new ConversionProgress(ConversionStage.Saving, null));
            var finalPath = FileService.MoveIntoPlaceNoOverwrite(tempPath, request.OutputPath);
            var size = new FileInfo(finalPath).Length;
            progress?.Report(new ConversionProgress(ConversionStage.Completed, 100));
            return new ConversionResult(request.SourcePath, finalPath, target.Id, size, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ProviderException)
        {
            throw Translate(ex);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    // ------------------------------------------------------------------ images (WIC)

    private static async Task ConvertImageAsync(string sourcePath, string tempPath, FormatInfo target, SqueezeLevel squeeze,
        long? targetBytes, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        var plan = CompressionPresets.ForImage(squeeze);
        var encoderId = WindowsCodecs.EncoderFor(target.Id)
            ?? throw new ProviderException(ProviderErrorKind.Unsupported, $"Windows has no {target.Label} encoder on this PC.");
        var usesQuality = target.Id is "jpg" or "heic" or "jxr";
        var flatten = target.Id is "jpg" or "bmp" or "heic"; // no transparency: white background instead of black

        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream()).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ProviderException(ProviderErrorKind.Unsupported,
                $"Windows couldn't open this image. It may be damaged, or its codec isn't installed on this PC ({Hex(ex)}).", ex);
        }

        progress?.Report(new ConversionProgress(ConversionStage.Converting, null));

        Task<byte[]> Encode(uint w, uint h, float? quality) => EncodeImageAsync(decoder, encoderId, w, h, quality, flatten, ct);

        if (targetBytes is not { } limit)
        {
            var (w, h) = CompressionPresets.Fit(decoder.PixelWidth, decoder.PixelHeight, plan.MaxLongSide);
            await File.WriteAllBytesAsync(tempPath, await Encode(w, h, usesQuality ? plan.Quality : null).ConfigureAwait(false), ct).ConfigureAwait(false);
            return;
        }

        // Fit under a size: best quality that fits at full size; if even low quality doesn't fit, shrink by 25% and retry.
        byte[]? smallest = null;
        uint cw = decoder.PixelWidth, ch = decoder.PixelHeight;
        for (var step = 0; step < 10; step++)
        {
            ct.ThrowIfCancellationRequested();
            byte[]? fit = null;
            if (usesQuality)
            {
                var high = await Encode(cw, ch, 0.92f).ConfigureAwait(false);
                if (high.Length <= limit) fit = high;
                else
                {
                    const float floor = 0.30f;
                    var low = await Encode(cw, ch, floor).ConfigureAwait(false);
                    smallest = Smaller(smallest, low);
                    if (low.Length <= limit)
                    {
                        // Binary search for the highest quality that still fits.
                        fit = low;
                        float lo = floor, hi = 0.92f;
                        for (var i = 0; i < 5; i++)
                        {
                            var mid = (lo + hi) / 2;
                            var attempt = await Encode(cw, ch, mid).ConfigureAwait(false);
                            if (attempt.Length <= limit) { fit = attempt; lo = mid; }
                            else hi = mid;
                        }
                    }
                }
            }
            else
            {
                var bytes = await Encode(cw, ch, null).ConfigureAwait(false);
                smallest = Smaller(smallest, bytes);
                if (bytes.Length <= limit) fit = bytes;
            }

            if (fit is not null)
            {
                await File.WriteAllBytesAsync(tempPath, fit, ct).ConfigureAwait(false);
                return;
            }

            var nw = (uint)(cw * 0.75);
            var nh = (uint)(ch * 0.75);
            if (Math.Max(nw, nh) < 160 || nw == 0 || nh == 0) break;
            cw = nw;
            ch = nh;
        }

        // Nothing fitted: save the smallest attempt so the caller can report its real size.
        await File.WriteAllBytesAsync(tempPath, smallest ?? await Encode(cw, ch, usesQuality ? 0.30f : null).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    private static byte[] Smaller(byte[]? a, byte[] b) => a is null || b.Length < a.Length ? b : a;

    /// <summary>Decodes the first frame (upright, sRGB, optionally scaled) and encodes it in memory.</summary>
    private static async Task<byte[]> EncodeImageAsync(BitmapDecoder decoder, Guid encoderId, uint width, uint height, float? quality, bool flatten, CancellationToken ct)
    {
        // Scaling happens before EXIF rotation, so sizes are in the file's stored orientation.
        var transform = new BitmapTransform();
        if (width != decoder.PixelWidth || height != decoder.PixelHeight)
        {
            transform.ScaledWidth = width;
            transform.ScaledHeight = height;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
        using var toEncode = flatten ? FlattenOnWhite(bitmap) : SoftwareBitmap.Copy(bitmap);

        using var ms = new MemoryStream();
        var ras = ms.AsRandomAccessStream();
        BitmapEncoder encoder;
        if (quality is { } q)
        {
            var options = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(q, Windows.Foundation.PropertyType.Single) } };
            encoder = await BitmapEncoder.CreateAsync(encoderId, ras, options).AsTask(ct).ConfigureAwait(false);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(encoderId, ras).AsTask(ct).ConfigureAwait(false);
        }
        encoder.SetSoftwareBitmap(toEncode);
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static SoftwareBitmap FlattenOnWhite(SoftwareBitmap premultiplied)
    {
        var w = premultiplied.PixelWidth;
        var h = premultiplied.PixelHeight;
        var bytes = new byte[w * h * 4];
        premultiplied.CopyToBuffer(bytes.AsBuffer());
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var inv = 255 - bytes[i + 3];
            // Premultiplied "over" white: c + (1 - a) * 255
            bytes[i] = (byte)Math.Min(255, bytes[i] + inv);
            bytes[i + 1] = (byte)Math.Min(255, bytes[i + 1] + inv);
            bytes[i + 2] = (byte)Math.Min(255, bytes[i + 2] + inv);
            bytes[i + 3] = 255;
        }
        return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
    }

    // ------------------------------------------------------------------ audio / video (Media Foundation)

    private static async Task ConvertMediaAsync(string sourcePath, string tempPath, FormatInfo source, FormatInfo target, SqueezeLevel squeeze,
        long? targetBytes, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        var src = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(ct).ConfigureAwait(false);

        MediaEncodingProfile sourceProfile;
        try
        {
            sourceProfile = await MediaEncodingProfile.CreateFromFileAsync(src).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ProviderException(ProviderErrorKind.Unsupported,
                $"Windows can't read this {source.Label} file. It may use a codec that isn't installed on this PC ({Hex(ex)}).", ex);
        }

        var targetIsAudio = target.Category == FormatCategory.Audio;
        if (targetIsAudio && sourceProfile.Audio is null)
            throw new ProviderException(ProviderErrorKind.Unsupported, "This file has no sound track, so there's no audio to extract.");
        if (!targetIsAudio && sourceProfile.Video is null)
            throw new ProviderException(ProviderErrorKind.Unsupported, $"This file has no video track, so it can't become {target.Label}.");

        // What to ask the encoder for.
        VideoPlan? videoPlan = null;
        uint? audioBitrate = null;
        var wmaQuality = AudioEncodingQuality.High;

        if (targetBytes is { } limit)
        {
            if (target.Id is "wav" or "flac")
                throw new ProviderException(ProviderErrorKind.Unsupported,
                    $"{target.Label} is lossless, so its size can't be chosen. Use MP3 or M4A to fit a size.");

            var seconds = await DurationSecondsAsync(src, targetIsAudio || sourceProfile.Video is null).ConfigureAwait(false);
            if (seconds <= 0)
                throw new ProviderException(ProviderErrorKind.Unsupported, "Windows couldn't tell how long this file is, so KwikKonvert can't aim for a size.");

            if (!targetIsAudio)
            {
                var v = sourceProfile.Video!;
                videoPlan = CompressionPresets.ForTargetSize(limit, seconds, v.Width, v.Height, v.Bitrate, hasAudio: sourceProfile.Audio is not null)
                    ?? throw new ProviderException(ProviderErrorKind.TooBigForTarget,
                        $"{FileService.HumanSize(limit)} is too small for {FormatDuration(seconds)} of video: it would need less than " +
                        $"{CompressionPresets.MinTargetVideoBitrate / 1000} kbps. Pick a bigger size, or trim the video first.");
            }
            else
            {
                audioBitrate = CompressionPresets.AudioForTargetSize(limit, seconds, aac: target.Id == "m4a")
                    ?? throw new ProviderException(ProviderErrorKind.TooBigForTarget,
                        $"{FileService.HumanSize(limit)} is too small for {FormatDuration(seconds)} of {target.Label} audio. Pick a bigger size or MP3.");
                wmaQuality = audioBitrate >= 192_000 ? AudioEncodingQuality.High : audioBitrate >= 128_000 ? AudioEncodingQuality.Medium : AudioEncodingQuality.Low;
            }
        }
        else if (squeeze != SqueezeLevel.None)
        {
            if (!targetIsAudio)
            {
                var v = sourceProfile.Video!;
                videoPlan = CompressionPresets.ForVideo(squeeze, v.Width, v.Height, v.Bitrate);
            }
            else
            {
                audioBitrate = CompressionPresets.AudioBitrate(squeeze, sourceProfile.Audio?.Bitrate ?? 0);
                wmaQuality = squeeze <= SqueezeLevel.Light ? AudioEncodingQuality.High : squeeze == SqueezeLevel.Normal ? AudioEncodingQuality.Medium : AudioEncodingQuality.Low;
            }
        }

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(tempPath)!).AsTask(ct).ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            var profile = CreateProfile(target, wmaQuality);
            if (!targetIsAudio && sourceProfile.Audio is null)
                profile.Audio = null; // silent video stays silent instead of failing

            if (videoPlan is not null && profile.Video is not null)
                ApplyVideoPlan(profile, sourceProfile, target, videoPlan);
            if (audioBitrate is { } ab && profile.Audio is not null)
                SetAudioBitrate(profile, target, ab);

            var dst = await folder.CreateFileAsync(Path.GetFileName(tempPath),
                attempt == 1 ? CreationCollisionOption.FailIfExists : CreationCollisionOption.ReplaceExisting).AsTask(ct).ConfigureAwait(false);

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareFileTranscodeAsync(src, dst, profile).AsTask(ct).ConfigureAwait(false);
            if (!prepared.CanTranscode)
            {
                throw new ProviderException(ProviderErrorKind.Unsupported, prepared.FailureReason switch
                {
                    TranscodeFailureReason.CodecNotFound => $"Windows doesn't have the codec needed to turn this {source.Label} into {target.Label} on this PC.",
                    TranscodeFailureReason.InvalidProfile => $"Windows can't produce {target.Label} with these settings from this file.",
                    _ => $"Windows can't convert this {source.Label} to {target.Label}.",
                });
            }

            // Real progress straight from Media Foundation (0–100). A second attempt starts again from 0.
            progress?.Report(new ConversionProgress(ConversionStage.Converting, 0));
            var mfProgress = progress is null ? null : new InlineProgress<double>(p =>
                progress.Report(new ConversionProgress(ConversionStage.Converting, Math.Clamp(p, 0, 100))));
            await prepared.TranscodeAsync().AsTask(ct, mfProgress).ConfigureAwait(false);

            // Encoders don't hit a bitrate exactly. If a size target was missed, go again with a proportionally lower bitrate.
            if (targetBytes is not { } max || videoPlan is null || attempt >= MaxTargetAttempts) return;
            var actual = new FileInfo(tempPath).Length;
            if (actual <= max) return;

            var factor = max * 0.95 / actual;
            var lowered = (uint)(videoPlan.VideoBitrate * factor);
            if (lowered < CompressionPresets.MinTargetVideoBitrate) return; // caller reports the real size
            videoPlan = videoPlan with { VideoBitrate = lowered };
        }
    }

    private static MediaEncodingProfile CreateProfile(FormatInfo target, AudioEncodingQuality wmaQuality) => target.Id switch
    {
        "mp4" => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto),
        "wmv" => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto),
        "mp3" => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
        "m4a" => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High),
        "wav" => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High),
        "wma" => MediaEncodingProfile.CreateWma(wmaQuality),
        "flac" => MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High),
        _ => throw new ProviderException(ProviderErrorKind.Unsupported, $"Windows can't write {target.Label} files."),
    };

    private static void ApplyVideoPlan(MediaEncodingProfile profile, MediaEncodingProfile sourceProfile, FormatInfo target, VideoPlan plan)
    {
        var video = profile.Video!;
        if (plan.Width > 0 && plan.Height > 0)
        {
            video.Width = plan.Width;
            video.Height = plan.Height;
            // Explicit size needs explicit timing/shape too; keep the source's.
            var fr = sourceProfile.Video!.FrameRate;
            if (fr.Numerator > 0 && fr.Denominator > 0)
            {
                video.FrameRate.Numerator = fr.Numerator;
                video.FrameRate.Denominator = fr.Denominator;
            }
            var par = sourceProfile.Video.PixelAspectRatio;
            if (par.Numerator > 0 && par.Denominator > 0)
            {
                video.PixelAspectRatio.Numerator = par.Numerator;
                video.PixelAspectRatio.Denominator = par.Denominator;
            }
        }
        video.Bitrate = plan.VideoBitrate;
        if (profile.Audio is not null && plan.AudioBitrate > 0)
            SetAudioBitrate(profile, target, plan.AudioBitrate);
    }

    /// <summary>Only bitrates the Windows encoders accept: AAC 96–192 kbps, MP3 standard steps. WMA uses its quality presets instead.</summary>
    private static void SetAudioBitrate(MediaEncodingProfile profile, FormatInfo target, uint bps)
    {
        switch (target.Id)
        {
            case "mp4":
            case "m4a":
                profile.Audio!.Bitrate = CompressionPresets.SnapAac(bps);
                break;
            case "mp3":
                profile.Audio!.Bitrate = CompressionPresets.SnapMp3(bps);
                break;
        }
    }

    private static async Task<double> DurationSecondsAsync(StorageFile file, bool audioFirst)
    {
        async Task<TimeSpan> Video() { try { return (await file.Properties.GetVideoPropertiesAsync()).Duration; } catch { return TimeSpan.Zero; } }
        async Task<TimeSpan> Music() { try { return (await file.Properties.GetMusicPropertiesAsync()).Duration; } catch { return TimeSpan.Zero; } }

        var first = audioFirst ? await Music().ConfigureAwait(false) : await Video().ConfigureAwait(false);
        if (first > TimeSpan.Zero) return first.TotalSeconds;
        var second = audioFirst ? await Video().ConfigureAwait(false) : await Music().ConfigureAwait(false);
        return second.TotalSeconds;
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.Seconds} s";
    }

    // ------------------------------------------------------------------ errors

    private static ProviderException Translate(Exception ex)
    {
        var hr = (uint)ex.HResult;
        return hr switch
        {
            0xC00D36C4 or 0xC00D5212 or 0xC00D36B4 or 0x88982F50 or 0x88982F07 or 0x88982F81 =>
                new ProviderException(ProviderErrorKind.Unsupported,
                    $"Windows doesn't have a codec for this on this PC ({Hex(ex)}). Free extensions from the Microsoft Store " +
                    "(e.g. HEIF, WebP, Raw, VP9 or AV1 Video Extensions) add more formats.", ex),
            0x80070005 => new ProviderException(ProviderErrorKind.LocalFile, "Windows denied access to the file or folder.", ex),
            0x80070070 => new ProviderException(ProviderErrorKind.LocalFile, "The disk is full.", ex),
            _ => new ProviderException(ProviderErrorKind.ConversionFailed, $"{ex.Message.Trim()} ({Hex(ex)})", ex),
        };
    }

    private static string Hex(Exception ex) => $"0x{(uint)ex.HResult:X8}";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
