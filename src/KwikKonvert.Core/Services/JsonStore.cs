using System.Text.Json;
using System.Text.Json.Serialization;

namespace KwikKonvert.Core.Services;

/// <summary>Small, thread-safe, atomically-written JSON file. Everything KwikKonvert remembers stays on this PC.</summary>
public sealed class JsonStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private T _value;

    public event EventHandler? Changed;

    public JsonStore(string path)
    {
        _path = path;
        _value = Load();
    }

    public string FilePath => _path;

    /// <summary>Returns a snapshot copy; mutate through <see cref="Update"/>.</summary>
    public T Read()
    {
        lock (_gate) return Clone(_value);
    }

    public void Update(Action<T> mutate)
    {
        lock (_gate)
        {
            var copy = Clone(_value);
            mutate(copy);
            _value = copy;
            Save(copy);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _value = new T();
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* ignore */ }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private T Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Options) ?? new T();
        }
        catch
        {
            // A corrupt settings file should never stop the app from starting; keep a copy for debugging.
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch { /* ignore */ }
        }
        return new T();
    }

    private void Save(T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    private static T Clone(T v) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Options), Options)!;
}
