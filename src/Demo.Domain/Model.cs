using System.Globalization;

namespace Demo.Domain;

// The "legacy" tool's domain: a bench-test station for voltage regulators.
// Engineers write test plans (XML), run them on the bench, get result files
// (CSV, PNG plots, HTML report) and an analysis with key findings.
// Nothing in this project knows about AI.

/// <summary>A quantity the bench can measure on a device, e.g. output voltage.</summary>
public sealed record MeasurementDefinition(string Id, string Name, string Units, int Decimals, string Description);

/// <summary>An operating condition the bench can set, e.g. chamber temperature.</summary>
public sealed record ConditionDefinition(string Id, string Name, string Units, double Min, double Max, double Default);

/// <summary>One test in a plan: what to measure and its pass/fail limits (at least one limit).</summary>
public sealed record PlannedTest(string Id, string Name, string Units, double? Min, double? Max)
{
    public bool Passes(double value) => (Min is null || value >= Min) && (Max is null || value <= Max);

    public string LimitText =>
        (Min, Max) switch
        {
            ({ } lo, { } hi) => string.Create(CultureInfo.InvariantCulture, $"{lo:0.####} to {hi:0.####} {Units}"),
            ({ } lo, null) => string.Create(CultureInfo.InvariantCulture, $"≥ {lo:0.####} {Units}"),
            (null, { } hi) => string.Create(CultureInfo.InvariantCulture, $"≤ {hi:0.####} {Units}"),
            _ => "no limits",
        };
}

/// <summary>The values one condition takes during a run. A single value means "held fixed".</summary>
public sealed record ConditionSweep(string Parameter, string Units, IReadOnlyList<double> Values);

/// <summary>A test plan: the device, the conditions to sweep, how many units (DUTs), and the tests.</summary>
public sealed record TestPlan(
    string Id,
    string Name,
    string Device,
    DateTimeOffset Created,
    string Source,
    IReadOnlyList<ConditionSweep> Conditions,
    int Samples,
    IReadOnlyList<PlannedTest> Tests)
{
    public int ConditionPoints => Conditions.Aggregate(1, (n, c) => n * c.Values.Count);

    public int MeasurementCount => ConditionPoints * Samples * Tests.Count;

    public IReadOnlyList<double> ValuesOf(string parameter) =>
        Conditions.FirstOrDefault(c => c.Parameter == parameter)?.Values ?? [];
}

public enum RunStatus { Running, Completed, Cancelled, Failed, Interrupted }

/// <summary>One execution of a plan. Persisted as run.json in the run's folder.</summary>
public sealed record TestRun(
    string Id,
    string PlanId,
    string PlanName,
    string Device,
    RunStatus Status,
    DateTimeOffset Started,
    DateTimeOffset? Finished,
    int Total,
    int Completed,
    int Failed,
    DateTimeOffset Heartbeat,
    string? Error = null)
{
    public int PercentComplete => Total == 0 ? 0 : Completed * 100 / Total;
}

/// <summary>One row of results.csv.</summary>
public sealed record Measurement(
    int Dut,
    double TemperatureC,
    double SupplyV,
    double LoadMa,
    string TestId,
    double Value,
    string Units,
    double? Min,
    double? Max,
    bool Pass)
{
    public string ConditionText => string.Create(CultureInfo.InvariantCulture, $"DUT {Dut} at {TemperatureC:0.#} °C, {SupplyV:0.0#} V, {LoadMa:0.#} mA");
}

/// <summary>Narrows which measurements an analysis or query looks at. Null fields match everything.</summary>
public sealed record MeasurementFilter(
    string? TestId = null,
    double? TemperatureC = null,
    double? SupplyV = null,
    double? LoadMa = null,
    int? Dut = null,
    bool FailedOnly = false)
{
    public static readonly MeasurementFilter All = new();

    public bool Matches(Measurement m) =>
        (TestId is null || string.Equals(m.TestId, TestId, StringComparison.OrdinalIgnoreCase)) &&
        (TemperatureC is null || Near(m.TemperatureC, TemperatureC.Value)) &&
        (SupplyV is null || Near(m.SupplyV, SupplyV.Value)) &&
        (LoadMa is null || Near(m.LoadMa, LoadMa.Value)) &&
        (Dut is null || m.Dut == Dut) &&
        (!FailedOnly || !m.Pass);

    public string Describe()
    {
        var parts = new List<string>();
        if (TestId is not null) parts.Add($"test {TestId}");
        if (TemperatureC is not null) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{TemperatureC:0.#} °C"));
        if (SupplyV is not null) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{SupplyV:0.0#} V"));
        if (LoadMa is not null) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{LoadMa:0.#} mA"));
        if (Dut is not null) parts.Add($"DUT {Dut}");
        if (FailedOnly) parts.Add("failures only");
        return parts.Count == 0 ? "all measurements" : string.Join(", ", parts);
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
}

/// <summary>A business-rule violation. Front ends show the message to the user (or model) as-is.</summary>
public sealed class DomainException(string message) : Exception(message);
