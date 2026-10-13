using System.Globalization;
using System.Text.Json;

namespace Demo.Domain;

/// <summary>One row of a device datasheet: a parameter and its guaranteed limits.</summary>
public sealed record SpecParameter(string Parameter, string Name, string Units, double? Min, double? Typical, double? Max);

/// <summary>
/// A customer's design file: the device's spec limits, as a CSV or JSON table.
/// Plans can be generated from one so limits are copied, never retyped.
/// </summary>
public sealed record SpecSheet(string FileName, string Device, IReadOnlyList<SpecParameter> Parameters)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static SpecSheet Load(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".csv" => ParseCsv(name, File.ReadAllLines(path)),
                ".json" => ParseJson(name, File.ReadAllText(path)),
                _ => throw new DomainException($"'{name}' is not a spec sheet; use .csv or .json."),
            };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DomainException($"Could not read spec sheet '{name}': {ex.Message}");
        }
    }

    /// <summary>
    /// <code>
    /// # device: LDO-3300
    /// parameter,name,units,min,typ,max
    /// VOUT,Output voltage,V,3.267,3.300,3.333
    /// </code>
    /// </summary>
    public static SpecSheet ParseCsv(string fileName, IEnumerable<string> lines)
    {
        string? device = null;
        var rows = new List<SpecParameter>();
        var headerSeen = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var comment = line.TrimStart('#').Trim();
                if (comment.StartsWith("device:", StringComparison.OrdinalIgnoreCase)) device = comment["device:".Length..].Trim();
                continue;
            }
            if (!headerSeen) { headerSeen = true; continue; }
            var f = line.Split(',');
            rows.Add(new SpecParameter(f[0].Trim(), f[1].Trim(), f[2].Trim(), Number(f[3]), Number(f[4]), Number(f[5])));
        }
        return Checked(new SpecSheet(fileName, device ?? throw new DomainException($"'{fileName}' has no '# device: ...' line."), rows));
    }

    /// <summary><code>{ "device": "LDO-3300", "parameters": [ { "parameter": "VOUT", "name": "...", "units": "V", "min": 3.267, "typ": 3.3, "max": 3.333 } ] }</code></summary>
    public static SpecSheet ParseJson(string fileName, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        return Checked(new SpecSheet(
            fileName,
            root.GetProperty("device").GetString()!,
            root.GetProperty("parameters").EnumerateArray().Select(p => new SpecParameter(
                p.GetProperty("parameter").GetString()!,
                p.GetProperty("name").GetString()!,
                p.GetProperty("units").GetString()!,
                Num(p, "min"), Num(p, "typ"), Num(p, "max"))).ToList()));
    }

    private static double? Number(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : double.Parse(text.Trim(), Inv);

    private static SpecSheet Checked(SpecSheet sheet) =>
        sheet.Parameters.Count == 0 ? throw new DomainException($"'{sheet.FileName}' lists no parameters.") : sheet;
}
