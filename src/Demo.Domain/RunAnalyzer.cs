using System.Globalization;

namespace Demo.Domain;

public enum Verdict { Pass, PassWithWarnings, Fail }

public enum FindingSeverity { Fail, Warning, Info }

/// <summary>A key finding, in words an engineer would write in a report.</summary>
public sealed record Finding(FindingSeverity Severity, string TestId, string Text);

public sealed record TestStatistics(
    string TestId,
    string Name,
    string Units,
    string Limits,
    int Count,
    int Failed,
    double Mean,
    double StdDev,
    double Lowest,
    double Highest,
    double? Cpk,
    string WorstCase,
    double? DriftPerDegreeC);

public sealed record RunSummary(
    string RunId,
    string PlanId,
    string Device,
    string Scope,
    int Measurements,
    int Failed,
    Verdict Verdict,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<TestStatistics> Tests,
    RunStatus Status,
    int Completed,
    int Total);

/// <summary>
/// Turns raw measurements into statistics and findings. Deterministic code, not a model, so the
/// numbers in a report are always right; the assistant's job is to explain them.
/// </summary>
public static class RunAnalyzer
{
    /// <summary>Within this fraction of the limit window counts as "close to the limit".</summary>
    public const double GuardBand = 0.10;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static RunSummary Analyze(TestRun run, TestPlan plan, IReadOnlyList<Measurement> all, MeasurementFilter filter)
    {
        var rows = all.Where(filter.Matches).ToList();
        var stats = new List<TestStatistics>();
        var findings = new List<Finding>();

        foreach (var test in plan.Tests)
        {
            var values = rows.Where(r => r.TestId == test.Id).ToList();
            if (values.Count == 0) continue;
            var definition = Bench.Measurement(test.Id);
            string F(double v) => v.ToString("F" + definition.Decimals, Inv);

            var mean = values.Average(v => v.Value);
            var sd = values.Count > 1 ? Math.Sqrt(values.Sum(v => (v.Value - mean) * (v.Value - mean)) / (values.Count - 1)) : 0;
            var cpk = sd > 0 ? Cpk(test, mean, sd) : null;
            var worst = values.MinBy(v => Margin(test, v.Value))!;
            var drift = Drift(values);
            var failed = values.Where(v => !v.Pass).ToList();

            stats.Add(new TestStatistics(test.Id, test.Name, test.Units, test.LimitText, values.Count, failed.Count,
                Math.Round(mean, definition.Decimals + 1), Math.Round(sd, definition.Decimals + 1),
                values.Min(v => v.Value), values.Max(v => v.Value), cpk is null ? null : Math.Round(cpk.Value, 2),
                $"{F(worst.Value)} {test.Units} ({worst.ConditionText})", drift is null ? null : Math.Round(drift.Value, definition.Decimals + 2)));

            if (failed.Count > 0)
            {
                var text = $"{test.Name} ({test.Id}) is outside its limit ({test.LimitText}) in {failed.Count} of {values.Count} measurements; worst {F(worst.Value)} {test.Units} on {worst.ConditionText}.";
                var duts = failed.Select(f => f.Dut).Distinct().ToList();
                var temps = failed.Select(f => f.TemperatureC).Distinct().ToList();
                if (duts.Count == 1 && values.Select(v => v.Dut).Distinct().Count() > 1)
                    text += $" Every failure is DUT {duts[0]}; the other units pass, which points to a defective unit rather than a design margin problem.";
                if (temps.Count == 1 && values.Select(v => v.TemperatureC).Distinct().Count() > 1)
                    text += string.Create(Inv, $" All failures are at {temps[0]:0.#} °C.");
                findings.Add(new Finding(FindingSeverity.Fail, test.Id, text));
            }
            else if (Window(test) > 0 && Margin(test, worst.Value) < GuardBand) // a one-sided limit of 0 has no window to be "close" in
            {
                findings.Add(new Finding(FindingSeverity.Warning, test.Id,
                    $"{test.Name} ({test.Id}) passes but is close to its limit ({test.LimitText}): {F(worst.Value)} {test.Units} on {worst.ConditionText}."));
            }
            else if (cpk is < 1.33 && drift is null) // across a temperature sweep the spread is the sweep, not the process
            {
                findings.Add(new Finding(FindingSeverity.Warning, test.Id,
                    string.Create(Inv, $"{test.Name} ({test.Id}) passes everywhere, but Cpk is {cpk:0.00} (below 1.33), so some production parts would likely fail.")));
            }

            if (drift is { } slope && Window(test) > 0)
            {
                var temps = values.Select(v => v.TemperatureC).ToList();
                var change = slope * (temps.Max() - temps.Min());
                var share = Math.Abs(change) / Window(test);
                if (share >= 0.2)
                    findings.Add(new Finding(FindingSeverity.Info, test.Id,
                        string.Create(Inv, $"{test.Name} ({test.Id}) drifts {slope:+0.#####;-0.#####} {test.Units}/°C: {F(Math.Abs(change))} {test.Units} across {temps.Min():0.#} to {temps.Max():0.#} °C, which uses {share * 100:0}% of the limit window.")));
            }
        }

        if (findings.Count == 0 && rows.Count > 0)
            findings.Add(new Finding(FindingSeverity.Info, "", $"All {rows.Count} measurements pass with comfortable margin."));
        // A stopped run's numbers are real but partial: never let it read as a clean pass.
        if (run.Status != RunStatus.Completed)
            findings.Add(new Finding(FindingSeverity.Warning, "",
                $"This run {Stopped(run)} after {all.Count} of {run.Total} measurements; conditions not reached are not covered."));
        var verdict = findings.Any(f => f.Severity == FindingSeverity.Fail) ? Verdict.Fail
            : findings.Any(f => f.Severity == FindingSeverity.Warning) ? Verdict.PassWithWarnings
            : Verdict.Pass;

        return new RunSummary(run.Id, plan.Id, plan.Device, filter.Describe(), rows.Count, rows.Count(r => !r.Pass), verdict,
            findings.OrderBy(f => f.Severity).ToList(), stats, run.Status, all.Count, run.Total);
    }

    private static string Stopped(TestRun run) => run.Status switch
    {
        RunStatus.Cancelled => "was cancelled",
        RunStatus.Failed => $"failed ({run.Error ?? "bench error"})",
        RunStatus.Interrupted => "was interrupted (its program stopped)",
        _ => $"is {run.Status}",
    };

    /// <summary>
    /// Distance to the nearest limit as a fraction of the limit window (negative when failing).
    /// A one-sided limit of 0 has no window; then the distance is in the test's units, which still ranks values.
    /// </summary>
    public static double Margin(PlannedTest test, double value)
    {
        var window = Window(test) is > 0 and var w ? w : 1;
        var toMax = test.Max is { } hi ? (hi - value) / window : double.PositiveInfinity;
        var toMin = test.Min is { } lo ? (value - lo) / window : double.PositiveInfinity;
        return Math.Min(toMax, toMin);
    }

    // Two-sided: the span between limits. One-sided: the distance from zero to the limit.
    private static double Window(PlannedTest test) =>
        test is { Min: { } lo, Max: { } hi } ? hi - lo : Math.Abs(test.Max ?? test.Min ?? 1);

    private static double? Cpk(PlannedTest test, double mean, double sd)
    {
        var upper = test.Max is { } hi ? (hi - mean) / (3 * sd) : double.PositiveInfinity;
        var lower = test.Min is { } lo ? (mean - lo) / (3 * sd) : double.PositiveInfinity;
        return Math.Min(upper, lower);
    }

    /// <summary>Least-squares slope of value against temperature, when at least three temperatures were tested.</summary>
    private static double? Drift(IReadOnlyList<Measurement> values)
    {
        if (values.Select(v => v.TemperatureC).Distinct().Count() < 3) return null;
        var mx = values.Average(v => v.TemperatureC);
        var my = values.Average(v => v.Value);
        var sxx = values.Sum(v => (v.TemperatureC - mx) * (v.TemperatureC - mx));
        return values.Sum(v => (v.TemperatureC - mx) * (v.Value - my)) / sxx;
    }
}
