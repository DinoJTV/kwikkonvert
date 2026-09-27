# KwikKonvert

**such files. many formats. wow.**

A small, plain Windows utility (in the spirit of 7-Zip) that **converts** and **compresses** files. Drop files in,
pick a format and/or a compression level, press Start. Or right-click files in Explorer.

Everything happens **on your PC** with Windows' own built-in codecs. Nothing is uploaded; no account, no API key.

---

## Build & run

**Needs:** Windows 10 1809+ or Windows 11, and the .NET 8, 9 or 10 SDK.

Double-click **`build.bat`** in the extracted folder. It:

1. checks for the .NET SDK (offering to install it) and runs the core tests;
2. builds **one self-contained `KwikKonvert.exe`** next to `build.bat` (or a `KwikKonvert-app` folder if this PC
   can't run the one-file version);
3. starts it once to prove it works, then deletes all build output;
4. asks whether to delete the source code too, leaving only the exe.

If anything fails, nothing is deleted and the real errors are shown (also saved to `build.log`).

```bat
build.bat            :: x64
build.bat keep       :: keep build output (for debugging)
build.bat arm64      :: ARM PCs
```

With Visual Studio: open `KwikKonvert.sln`, set **KwikKonvert** as startup project, platform **x64**, F5.

---

## The window

A standard Windows window: menu bar, file list, an *Options* box, Start/Stop/Close buttons, status bar, and the
dancing doge (it sits still when idle and dances while files are being processed).

| Control | What it does |
|---|---|
| **File list** | Name, size, type, live status (`Converting 45%`), and the result (`holiday (small).mp4  120 MB → 31 MB (-74%)`). Drag files or folders onto it, or use File › Add files / Add folder / Paste. Double-click opens the result; right-click for Show in folder, Open original, Remove. |
| **Format** | *Automatic* (picks the sensible small format for each file), a specific format (only formats every listed file can become, your usual choice and favourites first), or **One ZIP archive** (everything into a single .zip). |
| **Compression** | *None* (convert at full quality), *Light*, *Normal*, *Strong*, *Maximum*, or **Fit under 10 MB (Discord)**, **Fit under 25 MB (email)**, **Fit under … MB (custom size)**. The line underneath says exactly what the choice does. |
| **Output folder** | Same folder as the original, Desktop, Downloads, or any folder (the `...` button). Nothing is ever overwritten. |

Menus: **File** (Add files, Add folder, Paste files, Remove, Clear, Exit) · **Tools** (Options, History, Refresh
Explorer menu) · **Help** (Formats on this PC, About).

**Tools › Options** is a normal tabbed dialog: output folder, name-collision rule, default compression level,
progress window behaviour, doge messages on/off; which Explorer menus to add; favourite formats and smart
suggestions; Instant Konvert rules.

## Compression: what "smaller" means

Compressing always makes a **new file** (the original is never touched) and it is only kept if it really is smaller.
If it isn't, KwikKonvert throws it away and says so (`Not smaller - kept original`).

| Level | Video (→ MP4, H.264) | Audio | Pictures (→ JPG) | Other files |
|---|---|---|---|---|
| Light | up to 1080p, 5 Mbps | 160 kbps | 85 % quality, max 3840 px | ZIP |
| Normal | up to 720p, 2.5 Mbps | 128 kbps | 75 %, max 2560 px | ZIP |
| Strong | up to 480p, 1.2 Mbps | 96 kbps | 60 %, max 1920 px | ZIP (smallest) |
| Maximum | up to 360p, 0.6 Mbps | 64 kbps | 45 %, max 1280 px | ZIP (smallest) |

- Nothing is ever scaled *up*, and the video bitrate is always kept below the original's.
- Resolution limits apply to the short side, so portrait phone videos are treated like landscape ones.
- You can also combine a level with a specific format (e.g. PNG → JPG *Strong*, MOV → MP4 *Normal*,
  WAV → MP3 *Light*). With PNG/GIF/BMP/TIFF only the size limit applies, since those formats have no quality setting.
- Files Windows can't re-encode (documents, executables, archives…) are compressed **losslessly into a ZIP**, like
  7-Zip does. Already-compressed files (JPG, MP4, ZIP…) often won't get smaller that way.
- Compressed copies are named `name (small).ext`; ZIPs are `name.zip`.

## Fit under a size

Pick *Fit under 10 MB (Discord)*, *Fit under 25 MB (email)* or your own size, and each file is made to come in
just under it (1 MB = 1,000,000 bytes, so it also fits limits counted in MiB):

- **Video:** the bitrate is worked out from the video's length (a little is kept back for the container), and the
  resolution follows it (≈4 Mbps → 1080p … under 250 kbps → 240p). The real file is then measured; if the encoder
  overshot, it's encoded again with a proportionally lower bitrate (up to 3 attempts, with real progress each time).
  If the size is impossible (e.g. an hour into 10 MB would need under 100 kbps), it says so straight away.
- **Audio:** MP3 or M4A at the highest standard bitrate that fits. WAV and FLAC are lossless, so their size can't be chosen.
- **Pictures:** the best JPG quality that fits; if even low quality is too big, the picture is scaled down 25% at a time.
- **Other files:** the smallest ZIP. If that's still over the limit, KwikKonvert says so.
- With *Automatic*, files that are already small enough are left alone. A result over the limit is never kept.

## One ZIP archive

*Format › One ZIP archive* puts every listed file into a single .zip, keeping folder structure (like 7-Zip's
*Add to archive*). It's named after the folder the files came from (or the file, if there's only one), and saved in the
same place, or in your chosen output folder. Normal/Light use the normal ZIP setting; Strong/Maximum use the smallest.

## Progress

The green bar also shows in the **taskbar button** (red if something failed), and the status bar / progress dialog
shows the **time left**, estimated from real progress so far.

## Explorer

Right-click any file:

- **Compress with KwikKonvert ›** Light / Normal / Strong / Maximum · Fit under 10 MB (Discord) / 25 MB (email) / your
  custom size · Add to one ZIP archive (all files)
- **Add to ZIP with KwikKonvert** (folders)
- **KwikKonvert ›** Convert to MP4 / … / More formats... (pictures, audio and video)

These open a small progress dialog with real progress, elapsed and remaining time, the size change and the doge,
which closes itself when everything worked. Selecting several files puts them in one dialog, or one ZIP. On Windows 11 the entries are
under **Show more options** (Shift+F10). They're per-user registry entries, so no admin rights are needed.

## What it can convert

The format lists only show what works on *this* PC (Help › Formats on this PC).

| | Reads | Writes | Engine |
|---|---|---|---|
| **Pictures** | PNG, JPG, BMP, GIF, TIFF, ICO, JPEG-XR, DDS, WebP, plus HEIC/AVIF/RAW with their Store extensions | PNG, JPG, BMP, GIF, TIFF, JPEG-XR, plus HEIC with the HEVC extension | Windows Imaging Component |
| **Video** | MP4, MOV, M4V, MKV, AVI, WMV, 3GP, MTS/M2TS, TS, ASF, WebM* | MP4 (H.264 + AAC), WMV | Media Foundation |
| **Audio** | MP3, M4A, AAC, WAV, WMA, FLAC, plus the sound track of any video | MP3, M4A, WAV, WMA, FLAC | Media Foundation |
| **Anything else** | any file | ZIP | System.IO.Compression |

\*Some inputs need free Microsoft Store extensions (VP9, HEVC, AV1…). If a codec is missing, KwikKonvert says so.

## Command line

| Command | Used by |
|---|---|
| `KwikKonvert.exe [files or folders]` | Opens the window (with the files) |
| `--convert mp4 "file"` | Explorer › KwikKonvert › Convert to … |
| `--compress normal "file"` | Explorer › Compress with KwikKonvert (`light`, `normal`, `strong`, `maximum`) |
| `--fit 10 "file"` | Explorer › Fit under 10 MB (any whole number of MB) |
| `--zip "file or folder"` | Explorer › Add to one ZIP archive (items launched together go into one zip) |
| `--instant "file"` | Instant Konvert rule |
| `--pick "file"` | Explorer › More formats... |
| `--unregister` | Removes the Explorer menus (run before deleting the app) |

KwikKonvert runs as a single instance: extra launches hand their files to the running copy.

## Architecture

```
src/KwikKonvert.Core      net8.0, no Windows dependencies (tested)
  Services/FormatService, CompressionPresets, ZipCompressor, ZipArchiver, ConversionQueue, FileService,
           PreferencesService, HistoryService, ExplorerMenuPlanner, DogeMessages
src/KwikKonvert           WinForms (standard Win32 controls)
  Program.cs              single instance (mutex + named pipe)
  AppHost.cs              services, command routing, Explorer sync, exit rules
  Forms/                  MainForm, ProgressForm, OptionsForm, HistoryForm, FormatsForm, AboutForm, SizePromptForm
  Platform/LocalConverter WIC + Media Foundation + ZIP, with the compression settings
  Platform/ExplorerIntegration, WindowsCodecs, ShellHelper, TaskbarProgress
  Assets/                 doge GIF, icon (embedded in the exe)
tests/KwikKonvert.Core.Tests
```

## Uninstall

Run `KwikKonvert.exe --unregister`, then delete the exe and `%LocalAppData%\KwikKonvert`.

wow.
