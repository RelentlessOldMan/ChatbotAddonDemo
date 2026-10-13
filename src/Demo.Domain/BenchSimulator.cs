namespace Demo.Domain;

/// <summary>
/// Stands in for the instruments. Produces realistic LDO-regulator readings: temperature drift,
/// unit-to-unit spread, noise, and one weak unit (DUT 3) whose quiescent current runs away
/// above 85 °C, so the analysis has something real to find. Deterministic per run id.
/// </summary>
public sealed class BenchSimulator(string runId)
{
    private readonly Random _noise = new(StableHash(runId));
    private readonly Dictionary<int, DutTraits> _duts = [];

    public double Measure(string testId, int dut, double temperatureC, double supplyV, double loadMa)
    {
        var u = Traits(dut);
        var dT = temperatureC - 25;
        return testId.ToUpperInvariant() switch
        {
            "VOUT" => 3.300 + u.Vout + 0.00016 * dT + 0.0012 * (supplyV - 5) - 0.000012 * loadMa + Gaussian(0.0012),
            "IQ" => 44 + u.Iq + 0.10 * dT + 0.9 * (supplyV - 5) + 0.01 * loadMa + (dut == 3 ? 0.30 * Math.Max(0, temperatureC - 85) : 0) + Gaussian(0.5),
            "DROPOUT" => 150 + u.Dropout + 0.6 * dT + 0.35 * loadMa + Gaussian(2),
            "LOAD_REG" => 1.8 + 0.008 * dT + 0.004 * loadMa + Gaussian(0.25),
            "PSRR" => 64 + u.Psrr - 0.035 * dT - 2.0 * Math.Max(0, 4.2 - supplyV) + Gaussian(0.4),
            _ => throw new DomainException($"The bench cannot measure '{testId}'."),
        };
    }

    // Each physical unit has a fixed offset, so DUT 2 is the same part in every run.
    private DutTraits Traits(int dut)
    {
        if (!_duts.TryGetValue(dut, out var traits))
        {
            var r = new Random(1000 + dut);
            double G(double sigma) => sigma * Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
            _duts[dut] = traits = new DutTraits(G(0.003), G(1.5), G(6), G(1));
        }
        return traits;
    }

    private double Gaussian(double sigma) =>
        sigma * Math.Sqrt(-2 * Math.Log(1 - _noise.NextDouble())) * Math.Cos(2 * Math.PI * _noise.NextDouble());

    private static int StableHash(string text) => text.Aggregate(17, (h, c) => unchecked(h * 31 + c));

    private sealed record DutTraits(double Vout, double Iq, double Dropout, double Psrr);
}
