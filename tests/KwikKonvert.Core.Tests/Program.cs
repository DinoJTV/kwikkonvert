using KwikKonvert.Core.Models;
using KwikKonvert.Core.Providers;
using KwikKonvert.Core.Services;

// Tiny dependency-free test runner:  dotnet run --project tests/KwikKonvert.Core.Tests
var tests = new List<(string name, Func<Task> body)>();
void Test(string name, Func<Task> body) => tests.Add((name, body));
void Check(bool cond, string what) { if (!cond) throw new Exception("Expected: " + what); }

var tmpRoot = Path.Combine(Path.GetTempPath(), "kwik-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmpRoot);
string NewDir() { var d = Path.Combine(tmpRoot, Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

var formats = LocalFormats.CreateStockWindows();

// ------------------------------------------------------------------ formats (Windows built-in codecs)

Test("format detection from extension", () =>
{
    Check(formats.Detect(@"C:\x\holiday.MOV")!.Id == "mov", "mov");
    Check(formats.Detect("photo.tif")!.Category == FormatCategory.Image, "tif is an image");
    Check(formats.Detect("song.flac")!.Category == FormatCategory.Audio, "flac is audio");
    Check(formats.Detect("noext") is null, "no extension");
    Check(formats.Detect("doc.pdf") is null, "PDF is not something Windows' codecs convert");
    return Task.CompletedTask;
});

Test("targets: only what Windows can write, category-appropriate", () =>
{
    var mov = formats.TargetsFor(formats.Get("mov")!).Select(f => f.Id).ToHashSet();
    Check(mov.SetEquals(["mp4", "wmv", "mp3", "m4a", "wav", "wma", "flac"]), "MOV → video + audio extraction: " + string.Join(",", mov));
    var png = formats.TargetsFor(formats.Get("png")!).Select(f => f.Id).ToHashSet();
    Check(png.SetEquals(["jpg", "bmp", "gif", "tiff", "jxr"]), "PNG → other images: " + string.Join(",", png));
    var jpeg = formats.TargetsFor(formats.Get("jpeg")!).Select(f => f.Id).ToHashSet();
    Check(!jpeg.Contains("jpg") && jpeg.Contains("png"), "JPEG → JPG is not a conversion");
    var wav = formats.TargetsFor(formats.Get("wav")!).Select(f => f.Id).ToHashSet();
    Check(wav.Contains("mp3") && !wav.Contains("mp4"), "audio → audio only");
    var webp = formats.TargetsFor(formats.Get("webp")!).Select(f => f.Id).ToHashSet();
    Check(webp.Contains("png") && !webp.Contains("webp"), "WebP is read-only on stock Windows");
    return Task.CompletedTask;
});

Test("detected codecs extend the list (e.g. HEIF extension installed)", () =>
{
    var withHeif = LocalFormats.Create(["png", "jpg", "heic", "heif"], ["png", "jpg", "heic"]);
    Check(withHeif.TargetsFor(withHeif.Get("png")!).Any(f => f.Id == "heic"), "HEIC becomes a target");
    Check(withHeif.TargetsFor(withHeif.Get("heif")!).All(f => f.Id != "heic"), "HEIF → HEIC is not a conversion");
    return Task.CompletedTask;
});

Test("popular targets are valid and ordered", () =>
{
    Check(formats.PopularTargets(formats.Get("mov")!)[0] == "mp4", "MOV → MP4 first");
    Check(formats.PopularTargets(formats.Get("webp")!)[0] == "png", "WEBP → PNG first");
    Check(formats.PopularTargets(formats.Get("wav")!)[0] == "mp3", "WAV → MP3 first");
    foreach (var f in formats.Readable)
    {
        var valid = formats.TargetsFor(f).Select(x => x.Id).ToHashSet();
        Check(formats.PopularTargets(f).All(valid.Contains), "popular subset of valid for " + f.Id);
        Check(valid.Count > 0, "every readable format has a target: " + f.Id);
    }
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ files

Test("never overwrites: holiday.mp4 exists → holiday (1).mp4", () =>
{
    var dir = NewDir();
    var src = Path.Combine(dir, "holiday.mov");
    File.WriteAllText(src, "x");
    File.WriteAllText(Path.Combine(dir, "holiday.mp4"), "ORIGINAL");
    var files = new FileService(_ => null);
    Check(Path.GetFileName(files.PlanOutputPath(src, "mp4", OutputLocation.SameFolder, null, CollisionBehavior.AddNumber)) == "holiday (1).mp4", "planned (1)");

    var tmp = Path.Combine(dir, "tmp.part");
    File.WriteAllText(tmp, "NEW");
    var final = FileService.MoveIntoPlaceNoOverwrite(tmp, Path.Combine(dir, "holiday.mp4"));
    Check(Path.GetFileName(final) == "holiday (1).mp4", "race-safe move picks next free name");
    Check(File.ReadAllText(Path.Combine(dir, "holiday.mp4")) == "ORIGINAL", "existing file untouched");
    return Task.CompletedTask;
});

Test("output folder falls back to the source folder when unusable", () =>
{
    var dir = NewDir();
    var src = Path.Combine(dir, "a.png");
    var files = new FileService(loc => loc == OutputLocation.Desktop ? Path.Combine(dir, "nope") : null);
    Check(files.OutputFolderFor(src, OutputLocation.Desktop, null) == dir, "missing Desktop → same folder");
    Check(files.OutputFolderFor(src, OutputLocation.Custom, dir) == dir, "custom folder");
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ prefs / history / stores

Test("smart suggestions + favourites ordering, persisted locally", () =>
{
    var dir = NewDir();
    var settings = new JsonStore<AppSettings>(Path.Combine(dir, "settings.json"));
    var prefs = new PreferencesService(new JsonStore<Preferences>(Path.Combine(dir, "prefs.json")), settings, formats);
    var webp = formats.Get("webp")!;
    Check(prefs.OrderedTargets(webp)[0].Format.Id == "png", "popular default first");
    prefs.RecordUse("webp", "gif"); prefs.RecordUse("webp", "gif");
    var ordered = prefs.OrderedTargets(webp);
    Check(ordered[0].Format.Id == "gif" && ordered[0].Display == "⭐ GIF", "habit comes first with a star");
    prefs.ToggleFavourite("tiff");
    Check(prefs.OrderedTargets(webp)[1].Format.Id == "tiff" && prefs.OrderedTargets(webp)[1].IsFavourite, "favourite next");

    var prefs2 = new PreferencesService(new JsonStore<Preferences>(Path.Combine(dir, "prefs.json")), settings, formats);
    Check(prefs2.Habit("WEBP") == "gif" && prefs2.IsFavourite("TIFF"), "persisted");
    settings.Update(s => s.RememberFavourites = false);
    Check(!prefs2.IsFavourite("tiff"), "favourites respect setting");
    prefs2.Clear();
    Check(prefs2.Habit("webp") is null, "cleared");
    return Task.CompletedTask;
});

Test("history capped, grouped and clearable; no file contents stored", () =>
{
    var dir = NewDir();
    var h = new HistoryService(new JsonStore<History>(Path.Combine(dir, "history.json")));
    for (var i = 0; i < 205; i++) h.Add(new HistoryEntry { SourcePath = $"f{i}.mov", OutputPath = $"f{i}.mp4", Success = true });
    Check(h.Entries.Count == HistoryService.MaxEntries && h.Entries[0].SourcePath == "f204.mov", "capped, newest first");
    var now = new DateTime(2026, 9, 26, 12, 0, 0);
    Check(HistoryService.GroupLabel(now.AddHours(-1), now) == "TODAY" && HistoryService.GroupLabel(now.AddDays(-1), now) == "YESTERDAY", "group labels");
    h.Clear();
    Check(h.Entries.Count == 0 && !File.Exists(Path.Combine(dir, "history.json")), "cleared");
    return Task.CompletedTask;
});

Test("corrupt or old settings files do not crash", () =>
{
    var dir = NewDir();
    var path = Path.Combine(dir, "settings.json");
    File.WriteAllText(path, "{ not json");
    Check(new JsonStore<AppSettings>(path).Read().MinimizeToTray, "defaults used");
    // A settings file from the Convertio-era build (extra fields) still loads.
    File.WriteAllText(path, "{\"LastKnownMinutes\": 12, \"Theme\": \"Dark\"}");
    Check(new JsonStore<AppSettings>(path).Read().Theme == AppTheme.Dark, "old file loads, unknown fields ignored");
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ queue (with a fake local converter)

Test("queue: batch with concurrency, skip same-format, individual retry", async () =>
{
    var dir = NewDir();
    var provider = new FakeProvider();
    var files = new FileService(_ => null);
    var q = new ConversionQueue(provider, j => files.PlanOutputPath(j.SourcePath, j.TargetFormat, j.OutputFolder is null ? OutputLocation.SameFolder : OutputLocation.Custom, j.OutputFolder, CollisionBehavior.AddNumber));
    var done = new List<ConversionJob>();
    var all = new TaskCompletionSource();
    q.JobFinished += (_, j) => { lock (done) { done.Add(j); if (done.Count == 4) all.TrySetResult(); } };

    for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(dir, $"img{i}.webp"), "x");
    File.WriteAllText(Path.Combine(dir, "already.png"), "x");
    foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
        q.Enqueue(f, formats.Detect(f)!, "png");
    await all.Task.WaitAsync(TimeSpan.FromSeconds(20));

    Check(done.Count(j => j.Status == JobStatus.Succeeded) == 3, "3 converted");
    Check(done.Single(j => j.FileName == "already.png").Status == JobStatus.Skipped, "same format skipped");
    Check(File.Exists(Path.Combine(dir, "img0.png")) && File.Exists(Path.Combine(dir, "img2.png")), "outputs beside originals");
    Check(provider.MaxConcurrent <= 2, "at most 2 at once");

    provider.FailWith = "The codec could not decode the file.";
    File.WriteAllText(Path.Combine(dir, "bad.webp"), "x");
    var failed = new TaskCompletionSource<ConversionJob>();
    q.JobFinished += (_, j) => { if (j.FileName == "bad.webp") failed.TrySetResult(j); };
    var job = q.Enqueue(Path.Combine(dir, "bad.webp"), formats.Get("webp")!, "png");
    await failed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(job.Status == JobStatus.Failed && job.Error!.Message == "The codec could not decode the file.", "failed with real error");
    Check(DogeMessages.Explain(job.Error!).Contains("The codec could not decode the file."), "UI text includes real error");

    provider.FailWith = null;
    var retried = new TaskCompletionSource();
    q.JobFinished += (_, j) => { if (j == job && j.Status == JobStatus.Succeeded) retried.TrySetResult(); };
    q.Retry(job);
    await retried.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(File.Exists(Path.Combine(dir, "bad.png")), "retry succeeded");
});

Test("queue: cancel stops a running conversion", async () =>
{
    var dir = NewDir();
    var provider = new FakeProvider { Delay = TimeSpan.FromSeconds(30) };
    var q = new ConversionQueue(provider, j => Path.ChangeExtension(j.SourcePath, j.TargetFormat));
    var src = Path.Combine(dir, "long.mov");
    File.WriteAllText(src, "x");
    var finished = new TaskCompletionSource<ConversionJob>();
    q.JobFinished += (_, j) => finished.TrySetResult(j);
    var job = q.Enqueue(src, formats.Get("mov")!, "mp4");
    await Task.Delay(200);
    q.Cancel(job);
    var j2 = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(j2.Status == JobStatus.Cancelled, "cancelled");
    Check(!File.Exists(Path.Combine(dir, "long.mp4")), "no output");
});

Test("message rotator never repeats back-to-back and waits 2–4 s", () =>
{
    var r = new MessageRotator(new Random(1));
    string? last = null;
    for (var i = 0; i < 200; i++)
    {
        var m = r.Next(DogeMessages.Converting);
        Check(m != last, "no repeat");
        last = m;
        var t = r.NextInterval();
        Check(t >= TimeSpan.FromSeconds(2) && t <= TimeSpan.FromSeconds(4), "interval");
    }
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ Explorer menu plan

Test("explorer plan: quick formats, favourites, instant rules, more", () =>
{
    var dir = NewDir();
    var settings = new JsonStore<AppSettings>(Path.Combine(dir, "settings.json"));
    var prefs = new PreferencesService(new JsonStore<Preferences>(Path.Combine(dir, "prefs.json")), settings, formats);
    var planner = new ExplorerMenuPlanner(formats, prefs);

    var plan = planner.Plan(settings.Read());
    var mov = plan.Single(m => m.Extension == ".mov");
    Check(mov.QuickItems.Count == 3 && mov.QuickItems[0].Label == "⚡ MP4", "MOV → ⚡ MP4 first, 3 quick formats");
    Check(mov.ShowMore && mov.InstantTarget is null, "'such more formats...' shown, no rule");
    Check(plan.Any(m => m.Extension == ".tif") && plan.Any(m => m.Extension == ".webp"), "all readable extensions hooked");
    Check(!plan.Any(m => m.Extension == ".pdf"), "unsupported formats not hooked");

    prefs.ToggleFavourite("wmv");
    settings.Update(s => s.InstantRules.Add(new InstantRule { From = "webp", To = "png" }));
    plan = planner.Plan(settings.Read());
    Check(plan.Single(m => m.Extension == ".mov").QuickItems[0].Label == "⚡ WMV", "favourite moves to the top");
    Check(plan.Single(m => m.Extension == ".webp").InstantTarget == "png", "instant rule");

    settings.Update(s => s.InstantRules.Add(new InstantRule { From = "wav", To = "mp4" }));
    Check(planner.Plan(settings.Read()).Single(m => m.Extension == ".wav").InstantTarget is null, "impossible rule target ignored");

    settings.Update(s => { s.ExplorerQuickFormats = false; s.ExplorerFavourites = false; s.ExplorerMoreFormats = false; });
    Check(planner.Plan(settings.Read()).All(m => m.ShowMore), "never an empty menu");

    var fp1 = ExplorerMenuPlanner.Fingerprint(plan, @"C:\a\KwikKonvert.exe");
    Check(fp1 == ExplorerMenuPlanner.Fingerprint(plan, @"C:\a\KwikKonvert.exe") && fp1 != ExplorerMenuPlanner.Fingerprint(plan, @"C:\b\KwikKonvert.exe"), "fingerprint stable, changes with exe path");

    settings.Update(s => s.ExplorerEnabled = false);
    Check(planner.Plan(settings.Read()).Count == 0, "disabled → nothing");
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ compression

Test("compression presets: smaller, never upscaled, even video sizes", () =>
{
    Check(CompressionPresets.ForVideo(SqueezeLevel.None, 1920, 1080, 8_000_000) is null, "None keeps the source settings");
    var n = CompressionPresets.ForVideo(SqueezeLevel.Normal, 1920, 1080, 8_000_000)!;
    Check(n.Width == 1280 && n.Height == 720 && n.VideoBitrate == 2_500_000, $"1080p → 720p @2.5 Mbps, got {n.Width}x{n.Height}@{n.VideoBitrate}");
    var portrait = CompressionPresets.ForVideo(SqueezeLevel.Normal, 1080, 1920, 8_000_000)!;
    Check(portrait.Width == 720 && portrait.Height == 1280, "portrait limited by its short side");
    var small = CompressionPresets.ForVideo(SqueezeLevel.Light, 640, 360, 1_000_000)!;
    Check(small.Width == 640 && small.Height == 360, "never upscaled");
    Check(small.VideoBitrate < 1_000_000, "bitrate always below the source's: " + small.VideoBitrate);
    var odd = CompressionPresets.ForVideo(SqueezeLevel.Strong, 1917, 1079, 0)!;
    Check(odd.Width % 2 == 0 && odd.Height % 2 == 0, "even dimensions");
    Check(CompressionPresets.AudioBitrate(SqueezeLevel.Normal) == 128_000, "normal audio 128k");
    Check(CompressionPresets.AudioBitrate(SqueezeLevel.Light, 128_000) < 128_000, "audio never above the source");
    Check(CompressionPresets.Fit(4000, 3000, 2560) == (2560u, 1920u), "fit keeps aspect");
    Check(CompressionPresets.Fit(800, 600, 2560) == (800u, 600u), "fit never enlarges");
    var levels = Enum.GetValues<SqueezeLevel>().Select(CompressionPresets.ForImage).ToList();
    Check(levels.Zip(levels.Skip(1)).All(p => p.Second.Quality < p.First.Quality), "each level lowers JPEG quality");
    Check(CompressionPresets.SizeChange(10_000_000, 2_500_000).EndsWith("(-75%)"), CompressionPresets.SizeChange(10_000_000, 2_500_000));
    return Task.CompletedTask;
});

Test("ZIP compressor really shrinks compressible files and round-trips", async () =>
{
    var dir = NewDir();
    var src = Path.Combine(dir, "report.log");
    await File.WriteAllTextAsync(src, string.Concat(Enumerable.Repeat("such log line. many repeat. wow.\n", 20_000)));
    var zip = Path.Combine(dir, "report.log.zip");
    var reports = new List<double>();
    await ZipCompressor.CompressAsync(src, zip, SqueezeLevel.Maximum, new InlineProgress<double>(reports.Add), CancellationToken.None);
    Check(new FileInfo(zip).Length < new FileInfo(src).Length / 20, "at least 20x smaller for repetitive text");
    Check(reports.Count > 0 && Math.Abs(reports[^1] - 100) < 0.001, "progress reaches 100%");
    using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
    using var reader = new StreamReader(archive.Entries.Single().Open());
    Check(await reader.ReadToEndAsync() == await File.ReadAllTextAsync(src), "content identical after unzip");
    Check(archive.Entries.Single().Name == "report.log", "entry keeps the file name");
});

Test("compress targets and generic files", () =>
{
    Check(formats.CompressTargetFor(formats.Get("mov")!) == "mp4", "video → MP4");
    Check(formats.CompressTargetFor(formats.Get("wav")!) == "mp3", "audio → MP3");
    Check(formats.CompressTargetFor(formats.Get("m4a")!) == "m4a", "M4A stays M4A");
    Check(formats.CompressTargetFor(formats.Get("png")!) == "jpg", "image → JPG");
    var pdf = formats.DetectOrGeneric("contract.pdf");
    Check(pdf.Category == FormatCategory.Other && formats.CompressTargetFor(pdf) == "zip", "other files → ZIP");
    Check(formats.TargetsFor(pdf).Select(f => f.Id).SequenceEqual(["zip"]), "other files can only become ZIP");
    Check(formats.DetectOrGeneric("archive.zip").Category == FormatCategory.Other, "zip files are just files");
    return Task.CompletedTask;
});

Test("queue: compressing to the same format is not skipped", async () =>
{
    var dir = NewDir();
    var provider = new FakeProvider();
    var q = new ConversionQueue(provider, j => Path.Combine(dir, "out-" + j.Squeeze + "." + j.TargetFormat));
    var src = Path.Combine(dir, "clip.mp4");
    File.WriteAllText(src, "x");
    var done = new TaskCompletionSource<ConversionJob>();
    q.JobFinished += (_, j) => done.TrySetResult(j);
    q.Enqueue(src, formats.Get("mp4")!, "mp4", squeeze: SqueezeLevel.Strong);
    var job = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(job.Status == JobStatus.Succeeded && job.Squeeze == SqueezeLevel.Strong, "ran with squeeze");
    Check(provider.LastSqueeze == SqueezeLevel.Strong, "level reached the converter");
});

Test("target size: video bitrate fits the size, resolution follows bitrate", () =>
{
    // 60 s clip to 10 MB: 10e6*8*0.92/60 ≈ 1.23 Mbps total, 96k audio → ~1.13 Mbps video → 540p
    var p = CompressionPresets.ForTargetSize(10 * CompressionPresets.Megabyte, 60, 3840, 2160, 20_000_000, hasAudio: true)!;
    var bytes = (p.VideoBitrate + p.AudioBitrate) * 60.0 / 8;
    Check(bytes < 10 * CompressionPresets.Megabyte, $"estimated {bytes:0} bytes is under 10 MB");
    Check(bytes > 8.5 * CompressionPresets.Megabyte, "and uses most of the budget");
    Check(p.AudioBitrate == 96_000 && p.Height == 540 && p.Width == 960, $"540p with 96k audio ({p.Width}x{p.Height})");
    // Portrait phone video: short side limited, orientation kept
    var portrait = CompressionPresets.ForTargetSize(25 * CompressionPresets.Megabyte, 30, 1080, 1920, 15_000_000, true)!;
    Check(portrait.Width < portrait.Height && portrait.Width % 2 == 0, "portrait stays portrait, even sizes");
    // Never above the source's own bitrate
    var tiny = CompressionPresets.ForTargetSize(25 * CompressionPresets.Megabyte, 10, 640, 360, 800_000, true)!;
    Check(tiny.VideoBitrate == 800_000 && tiny.Width == 640, "capped at source bitrate, not enlarged");
    // Impossible: an hour into 10 MB
    Check(CompressionPresets.ForTargetSize(10 * CompressionPresets.Megabyte, 3600, 1920, 1080, 5_000_000, true) is null, "too small → null");
    return Task.CompletedTask;
});

Test("target size: audio bitrates snap to what Windows' encoders accept", () =>
{
    Check(CompressionPresets.SnapAac(64_000) == 96_000 && CompressionPresets.SnapAac(150_000) == 128_000, "AAC 96/128/160/192 only");
    Check(CompressionPresets.SnapMp3(100_000) == 96_000 && CompressionPresets.SnapMp3(500_000) == 320_000, "MP3 standard steps");
    // 5 minutes of audio into 10 MB → 245 kbps allowed → MP3 224k
    Check(CompressionPresets.AudioForTargetSize(10 * CompressionPresets.Megabyte, 300, aac: false) == 224_000, "mp3 224k");
    Check(CompressionPresets.AudioForTargetSize(10 * CompressionPresets.Megabyte, 3 * 3600, aac: true) is null, "3 h into 10 MB impossible for AAC");
    return Task.CompletedTask;
});

Test("queue: target size reaches the converter and disables same-format skipping", async () =>
{
    var dir = NewDir();
    var provider = new FakeProvider();
    var q = new ConversionQueue(provider, j => Path.Combine(dir, "out." + j.TargetFormat));
    var src = Path.Combine(dir, "clip.mp4");
    File.WriteAllText(src, "x");
    var done = new TaskCompletionSource<ConversionJob>();
    q.JobFinished += (_, j) => done.TrySetResult(j);
    q.Enqueue(src, formats.Get("mp4")!, "mp4", targetBytes: 10 * CompressionPresets.Megabyte);
    var job = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(job.Status == JobStatus.Succeeded && provider.LastTarget == 10 * CompressionPresets.Megabyte, "target passed through");
});

Test("one ZIP: files and folders keep their structure, empty folders kept", async () =>
{
    var dir = NewDir();
    var photos = Directory.CreateDirectory(Path.Combine(dir, "Photos")).FullName;
    Directory.CreateDirectory(Path.Combine(photos, "2026", "empty"));
    File.WriteAllText(Path.Combine(photos, "a.jpg"), new string('a', 5000));
    File.WriteAllText(Path.Combine(photos, "2026", "b.jpg"), new string('b', 3000));
    var notes = Path.Combine(dir, "notes.txt");
    File.WriteAllText(notes, "hello");

    var plan = ZipArchiver.Plan([photos, notes]);
    var names = plan.Entries.Select(e => e.EntryName).ToHashSet();
    Check(names.SetEquals(["Photos/a.jpg", "Photos/2026/b.jpg", "Photos/2026/empty/", "notes.txt"]), "entries: " + string.Join(", ", names));
    Check(plan.SuggestedName == Path.GetFileName(dir) && plan.FileCount == 3 && plan.TotalBytes == 8005, "name, count and size");
    Check(ZipArchiver.Plan([notes]).SuggestedName == "notes" && ZipArchiver.Plan([photos]).SuggestedName == "Photos", "single item names");

    var zip = Path.Combine(dir, "out.zip");
    double last = 0;
    await ZipArchiver.CreateAsync(plan, zip, SqueezeLevel.Maximum, new InlineProgress<ArchiveProgress>(p => last = p.Percent), CancellationToken.None);
    using var z = System.IO.Compression.ZipFile.OpenRead(zip);
    Check(z.Entries.Count == 4 && z.GetEntry("Photos/2026/b.jpg")!.Length == 3000, "archive readable with all entries");
    Check(new FileInfo(zip).Length < 8005 && last == 100, "smaller than the files, progress reached 100%");
});

Test("one ZIP: common parent of different folders", () =>
{
    var root = NewDir();
    var a = Path.Combine(root, "x", "a");
    var b = Path.Combine(root, "x", "b", "c");
    Check(ZipArchiver.CommonParent([a, b]) == Path.Combine(root, "x"), "deepest common folder");
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ run

var failedCount = 0;
foreach (var (name, body) in tests)
{
    try
    {
        await body().WaitAsync(TimeSpan.FromSeconds(60));
        Console.WriteLine($"  PASS  {name}");
    }
    catch (Exception ex)
    {
        failedCount++;
        Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}");
    }
}
try { Directory.Delete(tmpRoot, true); } catch { }
Console.WriteLine(failedCount == 0 ? $"\nAll {tests.Count} tests passed. wow." : $"\n{failedCount} of {tests.Count} failed.");
return failedCount == 0 ? 0 : 1;

/// <summary>Stands in for the Windows codec converter: writes a file, reports progress, can fail or be slow.</summary>
sealed class FakeProvider : IConversionProvider
{
    private int _running;
    public int MaxConcurrent;
    public SqueezeLevel LastSqueeze;
    public long? LastTarget;
    public string? FailWith;
    public TimeSpan Delay = TimeSpan.FromMilliseconds(50);
    public string Name => "Fake";

    public async Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        LastSqueeze = request.Squeeze;
        LastTarget = request.TargetBytes;
        var now = Interlocked.Increment(ref _running);
        lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            progress?.Report(new ConversionProgress(ConversionStage.Converting, 50));
            await Task.Delay(Delay, ct);
            if (FailWith is { } msg) throw new ProviderException(ProviderErrorKind.ConversionFailed, msg);
            var tmp = request.OutputPath + ".part";
            await File.WriteAllTextAsync(tmp, "converted", ct);
            var final = FileService.MoveIntoPlaceNoOverwrite(tmp, request.OutputPath);
            return new ConversionResult(request.SourcePath, final, request.TargetFormat, 9, TimeSpan.FromMilliseconds(50));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

sealed class InlineProgress<T>(Action<T> a) : IProgress<T>
{
    public void Report(T value) => a(value);
}
