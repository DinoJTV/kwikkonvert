using KwikKonvert.Core.Models;

namespace KwikKonvert.Core.Services;

/// <summary>Local list of recent konverts. Paths and formats only — never file contents.</summary>
public sealed class HistoryService
{
    public const int MaxEntries = 200;
    private readonly JsonStore<History> _store;

    public HistoryService(JsonStore<History> store) => _store = store;

    public event EventHandler? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    public IReadOnlyList<HistoryEntry> Entries => _store.Read().Entries;

    public void Add(HistoryEntry entry) => _store.Update(h =>
    {
        h.Entries.Insert(0, entry);
        if (h.Entries.Count > MaxEntries) h.Entries.RemoveRange(MaxEntries, h.Entries.Count - MaxEntries);
    });

    public void Remove(Guid id) => _store.Update(h => h.Entries.RemoveAll(e => e.Id == id));

    public void Clear() => _store.Reset();

    /// <summary>"TODAY", "YESTERDAY", or a date, for grouping in the UI.</summary>
    public static string GroupLabel(DateTime when, DateTime now)
    {
        var days = (now.Date - when.Date).Days;
        return days switch
        {
            0 => "TODAY",
            1 => "YESTERDAY",
            < 7 => when.ToString("dddd").ToUpperInvariant(),
            _ => when.ToString("d MMM yyyy").ToUpperInvariant(),
        };
    }
}
