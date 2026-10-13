using System.Globalization;

namespace Demo.Cli;

/// <summary>A malformed command line. The tool prints the message and exits with code 2 (domain errors exit with 1).</summary>
public sealed class UsageException(string message) : Exception(message);

public enum OptionKind { Flag, Text, WholeNumber, Number, NumberList }

/// <summary>
/// demo-cli's arguments: every <c>--option</c> (and its value) is taken first, and what remains are the
/// positional arguments, so options may appear anywhere. Values are checked here, before anything touches
/// the workspace, so a typo never runs a command with a default in its place.
/// </summary>
public sealed class CommandLine
{
    public static readonly IReadOnlyDictionary<string, OptionKind> Known = new Dictionary<string, OptionKind>
    {
        ["--help"] = OptionKind.Flag,
        ["--workspace"] = OptionKind.Text,
        ["--name"] = OptionKind.Text,
        ["--temps"] = OptionKind.NumberList,
        ["--supplies"] = OptionKind.NumberList,
        ["--loads"] = OptionKind.NumberList,
        ["--samples"] = OptionKind.WholeNumber,
        ["--tests"] = OptionKind.Text,
        ["--plan"] = OptionKind.Text,
        ["--test"] = OptionKind.Text,
        ["--temp"] = OptionKind.Number,
        ["--supply"] = OptionKind.Number,
        ["--dut"] = OptionKind.WholeNumber,
        ["--failed"] = OptionKind.Flag,
        ["--allow-writes"] = OptionKind.Flag,
        ["--allow-runs"] = OptionKind.Flag,
    };

    /// <summary>Accepted by every command.</summary>
    private static readonly string[] Global = ["--help", "--workspace"];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, string> _values = [];
    private readonly HashSet<string> _flags = [];
    private readonly List<string> _positionals = [];

    private CommandLine() { }

    public IReadOnlyList<string> Positionals => _positionals;

    public bool WantsHelp => _flags.Contains("--help") || _positionals is [] or ["help" or "-h", ..];

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var line = new CommandLine();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                line._positionals.Add(arg);
                continue;
            }
            if (!Known.TryGetValue(arg, out var kind))
                throw new UsageException($"unknown option '{arg}'.{Suggest(arg)}");
            if (line._flags.Contains(arg) || line._values.ContainsKey(arg))
                throw new UsageException($"{arg} is given more than once.");
            if (kind == OptionKind.Flag)
            {
                line._flags.Add(arg);
                continue;
            }
            // Negative numbers ("-40,25") are values; another "--option" is not.
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"{arg} needs a value.");
            var value = args[++i];
            Check(arg, kind, value);
            line._values[arg] = value;
        }
        return line;
    }

    /// <summary>
    /// Rejects options the command doesn't take and the wrong number of positional arguments
    /// (the command words included, e.g. 2 for <c>run &lt;TP-id&gt;</c>).
    /// </summary>
    public void Expect(string usage, int positionals, params string[] options)
    {
        var unexpected = _values.Keys.Concat(_flags).FirstOrDefault(o => !options.Contains(o) && !Global.Contains(o));
        if (unexpected is not null)
            throw new UsageException($"{unexpected} does not apply here. Usage: demo-cli {usage}");
        if (_positionals.Count < positionals)
            throw new UsageException($"missing argument. Usage: demo-cli {usage}");
        if (_positionals.Count > positionals)
            throw new UsageException($"unexpected argument '{_positionals[positionals]}'. Usage: demo-cli {usage}");
    }

    public string? Text(string name) => _values.GetValueOrDefault(name);

    public bool Flag(string name) => _flags.Contains(name);

    public int? WholeNumber(string name) => Text(name) is { } v ? int.Parse(v, NumberStyles.Integer, Inv) : null;

    public double? Number(string name) => Text(name) is { } v ? double.Parse(v, NumberStyles.Float, Inv) : null;

    public double[]? Numbers(string name) => Text(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, NumberStyles.Float, Inv)).ToArray();

    private static void Check(string name, OptionKind kind, string value)
    {
        var ok = kind switch
        {
            OptionKind.WholeNumber => int.TryParse(value, NumberStyles.Integer, Inv, out _),
            OptionKind.Number => double.TryParse(value, NumberStyles.Float, Inv, out _),
            OptionKind.NumberList => value.Split(',', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
                && parts.All(p => double.TryParse(p, NumberStyles.Float, Inv, out _)),
            _ => value.Length > 0,
        };
        if (!ok)
            throw new UsageException(kind switch
            {
                OptionKind.WholeNumber => $"{name} must be a whole number.",
                OptionKind.Number => $"{name} must be a number, e.g. 3.6.",
                OptionKind.NumberList => $"{name} must be a comma-separated list of numbers, e.g. -40,25,125.",
                _ => $"{name} needs a value.",
            });
    }

    private static string Suggest(string unknown)
    {
        var closest = Known.Keys.MinBy(k => Distance(unknown, k))!;
        return Distance(unknown, closest) <= 2 ? $" Did you mean {closest}?" : " Run 'demo-cli help' for the options.";
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}
