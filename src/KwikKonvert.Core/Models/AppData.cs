using KwikKonvert.Core.Services;

namespace KwikKonvert.Core.Models;

public enum AppTheme { System, Light, Dark }

public sealed class InstantRule
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public override string ToString() => $"{From.ToUpperInvariant()} → {To.ToUpperInvariant()}";
}

/// <summary>User settings (settings.json). Stored locally in %LocalAppData%\KwikKonvert.</summary>
public sealed class AppSettings
{
    // General
    public bool LaunchWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool EasterEggs { get; set; } = true;

    // Konversions
    public OutputLocation OutputLocation { get; set; } = OutputLocation.SameFolder;
    public string? CustomOutputFolder { get; set; }
    public CollisionBehavior Collision { get; set; } = CollisionBehavior.AddNumber;
    public bool RememberFavourites { get; set; } = true;
    public bool SmartSuggestions { get; set; } = true;

    // Instant Konvert
    public List<InstantRule> InstantRules { get; set; } = [];

    // Explorer
    public bool ExplorerEnabled { get; set; } = true;
    public bool ExplorerQuickFormats { get; set; } = true;
    public int ExplorerQuickCount { get; set; } = 3;
    public bool ExplorerFavourites { get; set; } = true;
    public bool ExplorerMoreFormats { get; set; } = true;

    public bool ExplorerCompress { get; set; } = true;

    // Compression
    public SqueezeLevel DefaultSqueeze { get; set; } = SqueezeLevel.Normal;

    /// <summary>The user's own "fit under" size in MB (Compression › Fit under custom size).</summary>
    public int CustomTargetMB { get; set; } = 50;

    // Progress window (Explorer actions)
    public bool CloseProgressWhenDone { get; set; } = true;

    public bool FirstRunDone { get; set; }

    public InstantRule? RuleFor(string sourceFormat) =>
        InstantRules.FirstOrDefault(r => string.Equals(r.From, sourceFormat, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Favourites + usage counts for smart suggestions (preferences.json). Local only.</summary>
public sealed class Preferences
{
    public List<string> Favourites { get; set; } = [];

    /// <summary>source format → (target format → times used)</summary>
    public Dictionary<string, Dictionary<string, int>> Usage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime When { get; set; } = DateTime.Now;
    public string SourcePath { get; set; } = "";
    public string? OutputPath { get; set; }
    public string SourceFormat { get; set; } = "";
    public string TargetFormat { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public long OutputBytes { get; set; }
}

public sealed class History
{
    public List<HistoryEntry> Entries { get; set; } = [];
}
