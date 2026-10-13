namespace Demo.Domain;

/// <summary>What the (simulated) bench hardware can do: its measurements, conditions and fixtures.</summary>
public static class Bench
{
    public const string Temperature = "Temperature";
    public const string SupplyVoltage = "SupplyVoltage";
    public const string LoadCurrent = "LoadCurrent";

    public const int MaxMeasurementsPerRun = 5000;
    public const int MaxSamples = 10;

    public static readonly IReadOnlyList<MeasurementDefinition> Measurements =
    [
        new("VOUT", "Output voltage", "V", 4, "Regulated output voltage under load."),
        new("IQ", "Quiescent current", "µA", 1, "Current the regulator itself draws from the supply."),
        new("DROPOUT", "Dropout voltage", "mV", 1, "Minimum input-output difference that keeps regulation, at the test load."),
        new("LOAD_REG", "Load regulation", "mV", 2, "Output change from 1 mA to the test load."),
        new("PSRR", "Ripple rejection at 1 kHz", "dB", 1, "Power-supply ripple rejection at 1 kHz; higher is better."),
    ];

    public static readonly IReadOnlyList<ConditionDefinition> Conditions =
    [
        new(Temperature, "Chamber temperature", "°C", -55, 150, 25),
        new(SupplyVoltage, "Supply voltage", "V", 3.6, 6.0, 5.0),
        new(LoadCurrent, "Load current", "mA", 0, 300, 100),
    ];

    /// <summary>Devices the bench has a fixture (socket board) for.</summary>
    public static readonly IReadOnlyList<string> Fixtures = ["LDO-3300"];

    public static MeasurementDefinition Measurement(string id) =>
        Measurements.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new DomainException($"The bench cannot measure '{id}'. Available: {string.Join(", ", Measurements.Select(m => m.Id))}.");

    public static ConditionDefinition Condition(string id) =>
        Conditions.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new DomainException($"Unknown condition '{id}'. Available: {string.Join(", ", Conditions.Select(c => c.Id))}.");

    /// <summary>Rough bench time for a plan: chamber soak per temperature plus time per measurement.</summary>
    public static TimeSpan EstimateDuration(TestPlan plan, BenchOptions options) =>
        options.SoakTime * plan.ValuesOf(Temperature).Count + options.MeasurementTime * plan.MeasurementCount;
}

/// <summary>How fast the simulated bench runs. Real benches take minutes; the demo takes seconds; tests take none.</summary>
public sealed record BenchOptions(TimeSpan SoakTime, TimeSpan MeasurementTime)
{
    public static readonly BenchOptions Demo = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(25));
    public static readonly BenchOptions Instant = new(TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>DEMO_BENCH_DELAY_MS=0 makes runs instant (used by tests); otherwise demo speed.</summary>
    public static BenchOptions FromEnvironment() =>
        Environment.GetEnvironmentVariable("DEMO_BENCH_DELAY_MS") == "0" ? Instant : Demo;
}
