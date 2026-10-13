using System.Text.Json;
using Demo.Domain;

namespace Demo.Domain.Tests;

public sealed class TestBenchServiceTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "bench-tests", Guid.NewGuid().ToString("N"));
    private readonly TestBenchService _bench;

    public TestBenchServiceTests() => _bench = new TestBenchService(_workspace, BenchOptions.Instant);

    private TestPlan HotCold(string spec = "LDO-3300_datasheet.csv") =>
        _bench.CreatePlanFromSpec(spec, "hot/cold", [-40, 25, 85, 125], [3.6, 5.0], null, samples: 3).Plan;

    private async Task<TestRun> RunToEnd(TestPlan plan)
    {
        var run = _bench.StartRun(plan.Id);
        return await _bench.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void New_workspace_is_seeded_with_specs_a_plan_and_a_completed_run_with_files()
    {
        Assert.Equal(["LDO-3300_datasheet.csv", "LDO-3300_rev-B_targets.json"], _bench.ListSpecs().Select(s => s.FileName));
        var plan = Assert.Single(_bench.ListPlans());
        Assert.Equal("TP-1001", plan.Id);
        var run = Assert.Single(_bench.ListRuns());
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(45, run.Completed);

        var files = _bench.Files(run.Id);
        Assert.True(File.Exists(files.ResultsCsv));
        Assert.True(File.Exists(files.SummaryCsv));
        Assert.Equal(5, files.Plots.Count);
        Assert.True(File.Exists(Path.Combine(_bench.PlansFolder, "TestPlan.xsd")));
    }

    [Fact]
    public void Plan_from_spec_copies_limits_and_reports_parameters_the_bench_cannot_measure()
    {
        var result = _bench.CreatePlanFromSpec("LDO-3300_rev-B_targets.json", null, [25, 125], null, null, samples: 2);

        Assert.Equal(["NOISE"], result.SkippedParameters);
        var iq = Assert.Single(result.Plan.Tests, t => t.Id == "IQ");
        Assert.Null(iq.Min);
        Assert.Equal(55, iq.Max);
        Assert.Equal("spec:LDO-3300_rev-B_targets.json", result.Plan.Source);
        Assert.Equal([5.0], result.Plan.ValuesOf(Bench.SupplyVoltage)); // not given: held at the bench default
        Assert.Equal(2 * 1 * 1 * 2 * 4, result.Plan.MeasurementCount);
    }

    [Fact]
    public void Plan_xml_round_trips_and_matches_the_schema()
    {
        var plan = HotCold();
        var xml = _bench.GetPlanXml(plan.Id);

        var parsed = TestPlanXml.Parse(xml);

        Assert.Equal(plan.Id, parsed.Id);
        Assert.Equal(plan.Tests, parsed.Tests);
        Assert.Equal(plan.MeasurementCount, parsed.MeasurementCount);
        Assert.Contains("<Sweep parameter=\"Temperature\" units=\"°C\" values=\"-40 25 85 125\" />", xml);
    }

    [Theory]
    [InlineData("<TestPlan id='TP-1' />", "does not match TestPlan.xsd")]
    [InlineData("not xml", "not valid XML")]
    public void Malformed_plan_files_are_rejected_with_a_readable_reason(string xml, string expected)
    {
        var ex = Assert.Throws<DomainException>(() => TestPlanXml.Parse(xml));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Bench_rules_reject_impossible_plans_and_list_every_problem()
    {
        var ex = Assert.Throws<DomainException>(() => _bench.CreatePlan("bad", "LDO-9999",
            [new TestLimit("IQ", null, null), new TestLimit("VOUT", 3.4, 3.2)], [200], null, null, samples: 3, "test"));

        Assert.Contains("no fixture for device 'LDO-9999'", ex.Message);
        Assert.Contains("Temperature 200 °C is outside the bench range", ex.Message);
        Assert.Contains("IQ needs a min or max limit", ex.Message);
        Assert.Contains("VOUT: min (3.4) must be below max (3.2)", ex.Message);
        Assert.Single(_bench.ListPlans()); // nothing was saved
    }

    [Fact]
    public void Unknown_measurement_names_the_ones_that_exist()
    {
        var ex = Assert.Throws<DomainException>(() => _bench.CreatePlan("x", "LDO-3300", [new TestLimit("NOISE", null, 45)], null, null, null, 1, "test"));
        Assert.Contains("cannot measure 'NOISE'. Available: VOUT, IQ, DROPOUT, LOAD_REG, PSRR", ex.Message);
    }

    [Theory]
    [InlineData(@"..\..\secrets.csv")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("sub/x.csv")]
    public void Spec_files_can_only_come_from_the_specs_folder(string name)
    {
        var ex = Assert.Throws<DomainException>(() => _bench.ReadSpec(name));
        Assert.Contains("specs folder only", ex.Message);
    }

    [Fact]
    public async Task Hot_cold_run_finds_the_weak_unit_and_explains_it()
    {
        var run = await RunToEnd(HotCold());

        Assert.Equal(RunStatus.Completed, run.Status);
        var summary = _bench.Analyze(run.Id, MeasurementFilter.All);
        Assert.Equal(Verdict.Fail, summary.Verdict);
        var fail = Assert.Single(summary.Findings, f => f.Severity == FindingSeverity.Fail);
        Assert.Equal("IQ", fail.TestId);
        Assert.Contains("Every failure is DUT 3", fail.Text);
        Assert.Contains("All failures are at 125 °C", fail.Text);
        Assert.Contains(summary.Findings, f => f.Severity == FindingSeverity.Info && f.TestId == "VOUT" && f.Text.Contains("drifts"));
    }

    [Fact]
    public async Task Analysis_can_be_narrowed_to_specific_conditions()
    {
        var run = await RunToEnd(HotCold());

        var cool = _bench.Analyze(run.Id, new MeasurementFilter(TemperatureC: 25));
        var hot = _bench.Analyze(run.Id, new MeasurementFilter(TemperatureC: 125, Dut: 3));

        Assert.Equal(30, cool.Measurements);
        Assert.Equal(0, cool.Failed);
        Assert.Equal("125 °C, DUT 3", hot.Scope);
        Assert.Equal(Verdict.Fail, hot.Verdict);
    }

    [Fact]
    public async Task Same_run_id_gives_the_same_measurements()
    {
        var plan = HotCold();
        var first = await RunToEnd(plan);
        var values = _bench.GetMeasurements(first.Id, MeasurementFilter.All).Select(m => m.Value).ToList();

        var other = new TestBenchService(Path.Combine(_workspace, "copy"), BenchOptions.Instant);
        var copy = other.CreatePlanFromSpec("LDO-3300_datasheet.csv", "hot/cold", [-40, 25, 85, 125], [3.6, 5.0], null, 3).Plan;
        var second = other.StartRun(copy.Id);
        await other.WaitForRunAsync(second.Id, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(values, other.GetMeasurements(second.Id, MeasurementFilter.All).Select(m => m.Value));
    }

    [Fact]
    public async Task The_bench_runs_one_plan_at_a_time_and_cancelling_keeps_partial_results()
    {
        var slow = new TestBenchService(_workspace, new BenchOptions(TimeSpan.Zero, TimeSpan.FromMilliseconds(20)));
        var plan = HotCold();
        var run = slow.StartRun(plan.Id);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var busy = Assert.Throws<DomainException>(() => slow.StartRun(plan.Id));
        Assert.Contains($"busy with {run.Id}", busy.Message);

        var cancelled = await slow.CancelRunAsync(run.Id);
        Assert.Equal(RunStatus.Cancelled, cancelled.Status);
        var partial = slow.GetMeasurements(run.Id, MeasurementFilter.All).Count;
        Assert.InRange(partial, 1, plan.MeasurementCount - 1);
    }

    [Fact]
    public void A_run_abandoned_by_a_crashed_process_shows_as_interrupted()
    {
        var folder = Path.Combine(_bench.RunsFolder, "R-1500");
        Directory.CreateDirectory(folder);
        var stale = DateTimeOffset.Now.AddHours(-1);
        File.WriteAllText(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(new
        {
            Id = "R-1500", PlanId = "TP-1001", PlanName = "x", Device = "LDO-3300", Status = "Running",
            Started = stale, Total = 10, Completed = 3, Failed = 0, Heartbeat = stale,
        }));

        Assert.Equal(RunStatus.Interrupted, _bench.GetRun("R-1500").Status);
        Assert.Null(_bench.BenchBusyWith());
    }

    [Fact]
    public void Report_contains_verdict_findings_and_plots()
    {
        var path = _bench.GenerateReport("R-1001");

        var html = File.ReadAllText(path);
        Assert.Contains("PASS", html);
        Assert.Contains("comfortable margin", html);
        Assert.Contains("<img src=\"plots/IQ.png\"", html);
    }

    [Fact]
    public void Imported_plans_are_validated_and_get_a_new_id()
    {
        var file = Path.Combine(_workspace, "handwritten.xml");
        File.WriteAllText(file, _bench.GetPlanXml("TP-1001").Replace("LDO-3300 room-temperature check", "Hand-written"));

        var imported = _bench.ImportPlan(file);

        Assert.Equal("TP-1002", imported.Id);
        Assert.Equal("Hand-written", imported.Name);
        Assert.Equal("import:handwritten.xml", imported.Source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
