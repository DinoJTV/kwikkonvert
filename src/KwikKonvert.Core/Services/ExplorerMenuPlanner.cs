using System.Security.Cryptography;
using System.Text;
using KwikKonvert.Core.Models;

namespace KwikKonvert.Core.Services;

public sealed record ExplorerMenuItem(string Format, string Label, bool SeparatorBefore = false);

/// <summary>What the right-click menu should look like for one file extension.</summary>
public sealed record ExplorerExtensionMenu(string Extension, string? InstantTarget, IReadOnlyList<ExplorerMenuItem> QuickItems, bool ShowMore);

/// <summary>
/// Decides the Explorer context-menu contents (pure logic, so it is testable off Windows).
/// The Windows app turns the plan into registry entries.
/// </summary>
public sealed class ExplorerMenuPlanner
{
    private readonly FormatService _formats;
    private readonly PreferencesService _prefs;

    public ExplorerMenuPlanner(FormatService formats, PreferencesService prefs)
    {
        _formats = formats;
        _prefs = prefs;
    }

    public IReadOnlyList<ExplorerExtensionMenu> Plan(AppSettings settings)
    {
        if (!settings.ExplorerEnabled) return [];

        var result = new List<ExplorerExtensionMenu>();
        foreach (var source in _formats.Readable.OrderBy(f => f.Id))
        {
            if (source.Id.Contains('.')) continue;
            var menu = PlanFor(source, settings);
            if (menu is null) continue;
            result.Add(menu with { Extension = "." + source.Id });
        }
        return result;
    }

    public ExplorerExtensionMenu? PlanFor(FormatInfo source, AppSettings settings)
    {
        var ordered = _prefs.OrderedTargets(source);
        if (ordered.Count == 0) return null;

        var picks = new List<FormatChoice>();
        if (settings.ExplorerQuickFormats)
            picks.AddRange(ordered.Take(Math.Clamp(settings.ExplorerQuickCount, 1, 8)));
        if (settings.ExplorerFavourites)
            picks.AddRange(ordered.Where(c => c.IsFavourite).Take(6));
        picks = picks.DistinctBy(c => c.Format.Id).Take(10).ToList();

        var items = picks.Select((c, i) => new ExplorerMenuItem(
            c.Format.Id,
            i == 0 ? "⚡ " + c.Format.Label : c.IsFavourite ? "⭐ " + c.Format.Label : c.Format.Label)).ToList();

        var rule = settings.RuleFor(source.Id);
        var instant = rule is not null && _formats.TargetsFor(source).Any(t => t.Id.Equals(rule.To, StringComparison.OrdinalIgnoreCase))
            ? rule.To.ToLowerInvariant()
            : null;

        // Always keep a way into the full list, otherwise the menu could end up empty.
        var showMore = settings.ExplorerMoreFormats || items.Count == 0;
        return new ExplorerExtensionMenu("", instant, items, showMore);
    }

    /// <summary>Stable fingerprint of a plan + exe path, so the registry is only rewritten when something changed.</summary>
    public static string Fingerprint(IReadOnlyList<ExplorerExtensionMenu> plan, string exePath)
    {
        var sb = new StringBuilder(exePath).Append('|').Append("v2|");
        foreach (var m in plan)
        {
            sb.Append(m.Extension).Append(':').Append(m.InstantTarget).Append(':').Append(m.ShowMore ? '1' : '0');
            foreach (var i in m.QuickItems) sb.Append(',').Append(i.Format).Append('=').Append(i.Label);
            sb.Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
