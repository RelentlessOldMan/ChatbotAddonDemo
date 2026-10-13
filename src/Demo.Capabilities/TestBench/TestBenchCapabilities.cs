using System.ComponentModel;
using Demo.Domain;

namespace Demo.Capabilities.TestBench;

/// <summary>A test to put in a new plan, as the model supplies it.</summary>
public sealed record TestLimitArgument(
    [property: Description("Measurement id from get_bench_info, e.g. 'IQ'.")] string TestId,
    [property: Description("Lower limit in the measurement's units; omit for a max-only limit.")] double? Min = null,
    [property: Description("Upper limit in the measurement's units; omit for a min-only limit.")] double? Max = null);

/// <summary>
/// What AI may do with the test bench. Each method is a thin adapter onto <see cref="TestBenchService"/>.
/// Descriptions are the model's only documentation, so they say when to use each tool and what comes back.
/// </summary>
public sealed class TestBenchCapabilities(TestBenchService bench)
{
    /// <summary>Shared by the in-app assistant (system prompt) and MCP (server instructions).</summary>
    public const string Instructions =
        """
        You are working with TestBench, a tool that plans, runs and analyzes bench tests of voltage-regulator chips.
        Workflow: a test plan (XML) lists conditions to sweep, how many units (DUTs) to test, and tests with pass/fail limits.
        Running a plan on the bench writes results.csv, summary.csv and plots/*.png into the run's folder; analysis finds failures, near-limit results and temperature drift.
        - Call get_bench_info before writing a plan: it lists what the bench can measure, condition ranges and devices.
        - When the user mentions a datasheet, spec or design file, use list_spec_files and create_test_plan_from_spec so limits are copied, not retyped.
        - Runs take time. After start_test_run, call wait_for_test_run, then get_run_summary.
        - Never compute statistics or pass/fail yourself from raw rows; get_run_summary and query_measurements do it exactly.
        - Never invent ids, values or file paths; get them from the tools.
        - If a tool returns an "error", explain it plainly.
        """;

    // ---- Read --------------------------------------------------------------------------------

    [Capability("get_bench_info", SafetyClass.Read, Title = "Check what the bench can do")]
    [Description("Returns the measurements the bench can make (ids, units), the conditions it can set with their allowed ranges and defaults, the devices it has fixtures for, and whether it is busy.")]
    public object GetBenchInfo() => new
    {
        measurements = Bench.Measurements,
        conditions = Bench.Conditions,
        fixtures = Bench.Fixtures,
        maxSamples = Bench.MaxSamples,
        maxMeasurementsPerRun = Bench.MaxMeasurementsPerRun,
        busyWith = bench.BenchBusyWith()?.Id,
    };

    [Capability("list_spec_files", SafetyClass.Read, Title = "List spec sheets")]
    [Description("Lists the datasheets/design files in the workspace's specs folder, with device and the parameters each one specifies (with limits).")]
    public IReadOnlyList<SpecSheet> ListSpecFiles() => bench.ListSpecs();

    [Capability("list_test_plans", SafetyClass.Read, Title = "List test plans")]
    [Description("Lists test plans: id, name, device, conditions, number of units, tests, and total measurement count.")]
    public object ListTestPlans() => bench.ListPlans().Select(PlanView).ToList();

    [Capability("get_test_plan", SafetyClass.Read, Title = "Open a test plan")]
    [Description("Gets one test plan with its limits, the path of its XML file, and the estimated bench time.")]
    public object GetTestPlan([Description("Plan id, e.g. 'TP-1001'.")] string planId)
    {
        var plan = bench.GetPlan(planId);
        return new { plan = PlanView(plan), xmlPath = bench.PlanPath(plan.Id), estimatedBenchSeconds = Math.Round(Bench.EstimateDuration(plan, bench.Options).TotalSeconds) };
    }

    [Capability("list_test_runs", SafetyClass.Read, Title = "List test runs")]
    [Description("Lists runs (newest last) with status, progress and failure counts. Optionally only the runs of one plan.")]
    public IReadOnlyList<TestRun> ListTestRuns([Description("Only runs of this plan id. Omit for all.")] string? planId = null) =>
        bench.ListRuns(planId);

    [Capability("wait_for_test_run", SafetyClass.Read, Title = "Wait for a run to finish")]
    [Description("Waits up to maxSeconds for a run to finish, then returns its status and progress. Call again if it is still Running.")]
    public async Task<TestRun> WaitForTestRun(
        [Description("Run id, e.g. 'R-1002'.")] string runId,
        [Description("How long to wait, 1 to 120 seconds.")] int maxSeconds = 60,
        CancellationToken cancellationToken = default) =>
        await bench.WaitForRunAsync(runId, TimeSpan.FromSeconds(Math.Clamp(maxSeconds, 1, 120)), cancellationToken);

    [Capability("get_run_summary", SafetyClass.Read, Title = "Analyze a run")]
    [Description("Analyzes a finished run: verdict, key findings (failures, results close to limits, temperature drift) and statistics per test (mean, std dev, min, max, Cpk, worst case). Optional filters narrow the analysis to specific conditions. Also returns the paths of the result files.")]
    public object GetRunSummary(
        [Description("Run id, e.g. 'R-1002'.")] string runId,
        [Description("Only this measurement id, e.g. 'IQ'.")] string? testId = null,
        [Description("Only this chamber temperature in °C.")] double? temperatureC = null,
        [Description("Only this supply voltage in V.")] double? supplyV = null,
        [Description("Only this unit number (DUT).")] int? dut = null) =>
        new { summary = bench.Analyze(runId, new MeasurementFilter(testId, temperatureC, supplyV, null, dut)), files = bench.Files(runId) };

    [Capability("query_measurements", SafetyClass.Read, Title = "Look up measurements")]
    [Description("Returns individual measurement rows from a run's results.csv that match the filters (at most maxRows), plus how many matched in total. Use for questions like 'which units failed at 125 °C?'.")]
    public object QueryMeasurements(
        [Description("Run id, e.g. 'R-1002'.")] string runId,
        [Description("Only this measurement id, e.g. 'IQ'.")] string? testId = null,
        [Description("Only this chamber temperature in °C.")] double? temperatureC = null,
        [Description("Only this supply voltage in V.")] double? supplyV = null,
        [Description("Only this unit number (DUT).")] int? dut = null,
        [Description("Only failing measurements.")] bool failedOnly = false,
        [Description("Maximum rows to return, 1 to 200.")] int maxRows = 50)
    {
        var rows = bench.GetMeasurements(runId, new MeasurementFilter(testId, temperatureC, supplyV, null, dut, failedOnly));
        var take = Math.Clamp(maxRows, 1, 200);
        return new { totalMatching = rows.Count, returned = Math.Min(take, rows.Count), rows = rows.Take(take) };
    }

    // ---- Write -------------------------------------------------------------------------------

    [Capability("create_test_plan", SafetyClass.Write, Title = "Create a test plan")]
    [Description("Creates and saves a new test plan (XML) with the given tests and limits. Conditions you leave empty are held at the bench default. The plan is validated against the bench before it is saved.")]
    public object CreateTestPlan(
        [Description("Short descriptive name, e.g. 'LDO-3300 cold start'.")] string name,
        [Description("Device part number; must have a bench fixture (see get_bench_info).")] string device,
        [Description("Tests to run, each with a min and/or max limit.")] TestLimitArgument[] tests,
        [Description("Unit count (DUTs) to test, 1 to 10.")] int samples,
        [Description("Chamber temperatures in °C.")] double[]? temperaturesC = null,
        [Description("Supply voltages in V.")] double[]? supplyVoltages = null,
        [Description("Load currents in mA.")] double[]? loadCurrentsMa = null)
    {
        var plan = bench.CreatePlan(name, device, tests.Select(t => new TestLimit(t.TestId, t.Min, t.Max)),
            temperaturesC, supplyVoltages, loadCurrentsMa, samples, "assistant");
        return new { plan = PlanView(plan), xmlPath = bench.PlanPath(plan.Id) };
    }

    [Capability("create_test_plan_from_spec", SafetyClass.Write, Title = "Create a test plan from a spec sheet")]
    [Description("Creates and saves a test plan whose tests and limits are copied from a spec sheet in the specs folder. You choose the conditions and unit count. Spec parameters the bench cannot measure are listed in skippedParameters.")]
    public object CreateTestPlanFromSpec(
        [Description("Spec file name from list_spec_files, e.g. 'LDO-3300_datasheet.csv'.")] string specFile,
        [Description("Unit count (DUTs) to test, 1 to 10.")] int samples,
        [Description("Chamber temperatures in °C.")] double[]? temperaturesC = null,
        [Description("Supply voltages in V.")] double[]? supplyVoltages = null,
        [Description("Load currents in mA.")] double[]? loadCurrentsMa = null,
        [Description("Only these parameters from the spec, e.g. ['IQ','VOUT']. Omit for all.")] string[]? onlyTests = null,
        [Description("Plan name. Omit for '<device> from <spec file>'.")] string? name = null)
    {
        var result = bench.CreatePlanFromSpec(specFile, name, temperaturesC, supplyVoltages, loadCurrentsMa, samples, onlyTests);
        return new { plan = PlanView(result.Plan), xmlPath = bench.PlanPath(result.Plan.Id), skippedParameters = result.SkippedParameters };
    }

    [Capability("generate_report", SafetyClass.Write, Title = "Write the HTML report")]
    [Description("Writes report.html for a completed run (verdict, key findings, statistics table, plots) and returns its path.")]
    public object GenerateReport([Description("Run id, e.g. 'R-1002'.")] string runId) =>
        new { report = bench.GenerateReport(runId) };

    // ---- Execute -----------------------------------------------------------------------------

    [Capability("start_test_run", SafetyClass.Execute, Title = "Start a run on the bench")]
    [Description("Starts running a plan on the bench and returns the new run id at once. The bench runs one plan at a time. Follow with wait_for_test_run.")]
    public object StartTestRun([Description("Plan id, e.g. 'TP-1002'.")] string planId)
    {
        // Read the plan first: once the run has started, nothing here may fail and report an error for a running run.
        var estimate = Bench.EstimateDuration(bench.GetPlan(planId), bench.Options);
        var run = bench.StartRun(planId);
        return new { run, estimatedBenchSeconds = Math.Round(estimate.TotalSeconds) };
    }

    [Capability("cancel_test_run", SafetyClass.Execute, Title = "Stop a run")]
    [Description("Stops a run in progress. Measurements taken so far are kept in results.csv.")]
    public Task<TestRun> CancelTestRun([Description("Run id, e.g. 'R-1002'.")] string runId) => bench.CancelRunAsync(runId);

    private static object PlanView(TestPlan p) => new
    {
        p.Id,
        p.Name,
        p.Device,
        p.Source,
        p.Created,
        conditions = p.Conditions.Select(c => new { c.Parameter, c.Units, c.Values }),
        units = p.Samples,
        tests = p.Tests.Select(t => new { t.Id, t.Name, t.Units, t.Min, t.Max }),
        measurementCount = p.MeasurementCount,
    };
}
