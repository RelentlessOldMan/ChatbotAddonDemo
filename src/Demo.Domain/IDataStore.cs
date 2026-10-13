using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demo.Domain;

public interface IDataStore
{
    DataSnapshot Load();
    void Save(DataSnapshot snapshot);
}

public sealed class InMemoryDataStore(DataSnapshot initial) : IDataStore
{
    private DataSnapshot _snapshot = initial;
    public DataSnapshot Load() => _snapshot;
    public void Save(DataSnapshot snapshot) => _snapshot = snapshot;
}

/// <summary>The legacy tool's on-disk format: one JSON file. Seeded on first use.</summary>
public sealed class JsonFileDataStore(string path, TimeProvider? time = null) : IDataStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public DataSnapshot Load()
    {
        if (!File.Exists(Path))
            return SeedData.Create(DateOnly.FromDateTime((time ?? TimeProvider.System).GetLocalNow().Date));
        using var stream = File.OpenRead(Path);
        return JsonSerializer.Deserialize<DataSnapshot>(stream, Options)
            ?? throw new InvalidDataException($"'{Path}' is empty or invalid.");
    }

    public void Save(DataSnapshot snapshot)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options));
        File.Move(temp, Path, overwrite: true);
    }
}
