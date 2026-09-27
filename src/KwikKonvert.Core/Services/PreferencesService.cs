using KwikKonvert.Core.Models;

namespace KwikKonvert.Core.Services;

/// <summary>One entry in a format picker, already ordered and badged.</summary>
public sealed record FormatChoice(FormatInfo Format, bool IsFavourite, bool IsSuggested)
{
    public string Display => (IsFavourite || IsSuggested ? "⭐ " : "") + Format.Label;
    public override string ToString() => Display;
}

/// <summary>Favourite formats and "smart suggestions" learned from the user's own conversions. Stored locally only.</summary>
public sealed class PreferencesService
{
    private readonly JsonStore<Preferences> _store;
    private readonly JsonStore<AppSettings> _settings;
    private readonly FormatService _formats;

    public PreferencesService(JsonStore<Preferences> store, JsonStore<AppSettings> settings, FormatService formats)
    {
        _store = store;
        _settings = settings;
        _formats = formats;
    }

    public event EventHandler? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    public bool IsFavourite(string format) =>
        _settings.Read().RememberFavourites && _store.Read().Favourites.Contains(format.ToLowerInvariant());

    public IReadOnlyList<string> Favourites => _settings.Read().RememberFavourites ? _store.Read().Favourites : [];

    public void ToggleFavourite(string format)
    {
        var id = format.ToLowerInvariant();
        _store.Update(p =>
        {
            if (!p.Favourites.Remove(id)) p.Favourites.Add(id);
        });
    }

    public void RecordUse(string sourceFormat, string targetFormat)
    {
        var s = sourceFormat.ToLowerInvariant();
        var t = targetFormat.ToLowerInvariant();
        _store.Update(p =>
        {
            if (!p.Usage.TryGetValue(s, out var map)) p.Usage[s] = map = new Dictionary<string, int>();
            map[t] = map.GetValueOrDefault(t) + 1;
        });
    }

    /// <summary>The user's most-used target for this source, if they have a habit (used at least twice, or the only one used).</summary>
    public string? Habit(string sourceFormat)
    {
        if (!_settings.Read().SmartSuggestions) return null;
        if (!_store.Read().Usage.TryGetValue(sourceFormat.ToLowerInvariant(), out var map) || map.Count == 0) return null;
        var best = map.OrderByDescending(kv => kv.Value).First();
        return best.Value >= 2 || map.Count == 1 ? best.Key : null;
    }

    /// <summary>
    /// Ordered target list for a source: user's habit first, then favourites, then popular picks, then A–Z.
    /// Only targets this PC can produce for this source are included.
    /// </summary>
    public IReadOnlyList<FormatChoice> OrderedTargets(FormatInfo source) => Order(source, _formats.TargetsFor(source));

    public IReadOnlyList<FormatChoice> Order(FormatInfo? source, IEnumerable<FormatInfo> targets)
    {
        var list = targets.ToList();
        var favs = Favourites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var habit = source is null ? null : Habit(source.Id);
        var popular = source is null ? [] : _formats.PopularTargets(source);

        int Rank(FormatInfo f)
        {
            if (habit is not null && f.Id.Equals(habit, StringComparison.OrdinalIgnoreCase)) return 0;
            if (favs.Contains(f.Id)) return 1;
            var i = IndexOf(popular, f.Id);
            return i >= 0 ? 10 + i : 1000;
        }

        return list
            .OrderBy(Rank)
            .ThenBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FormatChoice(f, favs.Contains(f.Id), habit is not null && f.Id.Equals(habit, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Best guess for the pre-selected target.</summary>
    public FormatChoice? DefaultChoice(IReadOnlyList<FormatChoice> ordered) => ordered.Count > 0 ? ordered[0] : null;

    public void Clear() => _store.Reset();

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
