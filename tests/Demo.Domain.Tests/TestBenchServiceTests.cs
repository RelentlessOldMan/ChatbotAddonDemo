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

    // Waits for a few measurements rather than a fixed time, so a busy machine doesn't make the test flaky.
    private static async Task MeasurementsTaken(TestBenchService bench, string runId)
    {
        for (var i = 0; i < 1000 && bench.GetRun(runId).Completed < 3; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
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
        await MeasurementsTaken(slow, run.Id);

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

    [Theory]
    [InlineData(@"..\..\secrets")]
    [InlineData("R-1001/../../x")]
    [InlineData("TP-1001")]
    [InlineData("")]
    public void Run_ids_that_are_not_R_digits_are_refused_before_touching_files(string id)
    {
        Assert.Contains("is not a valid run id", Assert.Throws<DomainException>(() => _bench.GetRun(id)).Message);
        Assert.Contains("is not a valid run id", Assert.Throws<DomainException>(() => _bench.Files(id)).Message);
        Assert.Contains("is not a valid plan id", Assert.Throws<DomainException>(() => _bench.GetPlan(id.Replace("TP", "R"))).Message);
    }

    [Fact]
    public void A_run_json_that_names_another_run_or_a_bad_plan_id_is_ignored()
    {
        void Fake(string folderId, string id, string planId)
        {
            var folder = Path.Combine(_bench.RunsFolder, folderId);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(new
            {
                Id = id, PlanId = planId, PlanName = "x", Device = "LDO-3300", Status = "Completed",
                Started = DateTimeOffset.Now, Total = 1, Completed = 1, Failed = 0, Heartbeat = DateTimeOffset.Now,
            }));
        }
        Fake("R-1600", @"..\..\outside", "TP-1001");
        Fake("R-1601", "R-1601", @"..\..\outside");

        Assert.Throws<DomainException>(() => _bench.GetRun("R-1600"));
        Assert.Throws<DomainException>(() => _bench.GetRun("R-1601"));
        Assert.Equal(["R-1001"], _bench.ListRuns().Select(r => r.Id));
    }

    [Fact]
    public async Task Another_program_polling_run_json_does_not_break_a_run()
    {
        var plan = HotCold();
        var slow = new TestBenchService(_workspace, new BenchOptions(TimeSpan.Zero, TimeSpan.FromMilliseconds(4)));
        var watcher = new TestBenchService(_workspace, BenchOptions.Instant); // stands in for the console tool or desktop app
        var run = slow.StartRun(plan.Id);

        var reads = 0;
        while (watcher.GetRun(run.Id).Status == RunStatus.Running) { reads++; Thread.Sleep(1); } // a very eager poller

        Assert.True(reads > 0);
        Assert.Equal(RunStatus.Completed, (await slow.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status);
    }

    private string PlanXml(Func<string, string> edit) => edit(_bench.GetPlanXml("TP-1001"));

    [Theory]
    [InlineData("values=\"3.6 5 5.5\"", "values=\"3.6 NaN 5.5\"", "SupplyVoltage values must be numbers; 'NaN' is not.")]
    [InlineData("min=\"3.267\"", "min=\"-INF\"", "VOUT limits must be numbers; '-Infinity' is not.")]
    [InlineData("<Samples count=\"3\"", "<Samples count=\"99999999999999999999999\"", "Samples must be 1 to 10 units.")]
    public void Non_finite_numbers_and_huge_counts_are_rejected_readably(string from, string to, string expected)
    {
        var xml = PlanXml(x => x.Replace(from, to));
        Assert.Contains(to, xml);

        Assert.Contains(expected, Assert.Throws<DomainException>(() => TestPlanXml.Parse(xml)).Message);

        File.WriteAllText(Path.Combine(_bench.PlansFolder, "TP-1999.xml"), xml.Replace("TP-1001", "TP-1999"));
        Assert.Equal(["TP-1001"], _bench.ListPlans().Select(p => p.Id)); // one bad file doesn't break the list
    }

    [Fact]
    public void Plan_files_with_a_dtd_are_refused()
    {
        var xml = PlanXml(x => x.Replace("<TestPlan ", "<!DOCTYPE TestPlan [<!ENTITY big \"aaaaaaaaaa\">]>\n<TestPlan ").Replace("room-temperature check", "&big;"));

        Assert.Contains("not valid XML", Assert.Throws<DomainException>(() => TestPlanXml.Parse(xml)).Message);
    }

    [Fact]
    public void Imported_test_names_are_replaced_by_the_bench_names()
    {
        var file = Path.Combine(_workspace, "evil.xml");
        File.WriteAllText(file, PlanXml(x => x.Replace("name=\"Output voltage\"", "name=\"=HYPERLINK(&quot;http://x&quot;)\"")));

        var imported = _bench.ImportPlan(file);

        Assert.Equal("Output voltage", imported.Tests.Single(t => t.Id == "VOUT").Name);
    }

    [Fact]
    public void Summary_csv_text_cells_that_look_like_formulas_are_defused_but_numbers_are_not()
    {
        var stats = new TestStatistics("VOUT", "=1+1", "V", "-1 to 1 V", 3, 0, -0.5, 0.1, -0.7, -0.3, null, "@x", null);
        var summary = new RunSummary("R-1", "TP-1", "LDO-3300", "all measurements", 3, 0, Verdict.Pass, [], [stats], RunStatus.Completed, 3, 3);
        var path = Path.Combine(_workspace, "summary.csv");

        ResultFiles.WriteSummaryCsv(summary, path);

        Assert.Equal("VOUT,\"'=1+1\",V,\"'-1 to 1 V\",3,0,-0.5,0.1,-0.7,-0.3,,,\"'@x\"", File.ReadAllLines(path)[1]);
    }

    [Fact]
    public void Analysis_with_a_filter_that_matches_nothing_says_what_there_is()
    {
        var ex = Assert.Throws<DomainException>(() => _bench.Analyze("R-1001", new MeasurementFilter(TemperatureC: 85)));

        Assert.Contains("No measurements in R-1001 match 85 °C", ex.Message);
        Assert.Contains("Available tests are DROPOUT, IQ, LOAD_REG, PSRR, VOUT; temperatures 25 °C", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_run_is_analysed_with_a_warning_that_it_is_partial()
    {
        var slow = new TestBenchService(_workspace, new BenchOptions(TimeSpan.Zero, TimeSpan.FromMilliseconds(10)));
        var run = slow.StartRun(_bench.ListPlans().Single().Id);
        await MeasurementsTaken(slow, run.Id);
        await slow.CancelRunAsync(run.Id);

        var summary = slow.Analyze(run.Id, MeasurementFilter.All);

        Assert.Equal(RunStatus.Cancelled, summary.Status);
        Assert.Equal(45, summary.Total);
        Assert.InRange(summary.Completed, 1, 44);
        Assert.NotEqual(Verdict.Pass, summary.Verdict);
        Assert.Contains(summary.Findings, f => f.Severity == FindingSeverity.Warning &&
            f.Text == $"This run was cancelled after {summary.Completed} of 45 measurements; conditions not reached are not covered.");
    }

    [Fact]
    public void Two_programs_creating_plans_at_once_never_reuse_an_id()
    {
        var other = new TestBenchService(_workspace, BenchOptions.Instant);
        TestPlan Make(TestBenchService bench) =>
            bench.CreatePlan("p", "LDO-3300", [new TestLimit("VOUT", 3.2, 3.4)], null, null, null, 1, "test");

        var made = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 40, i => made.Add(Make(i % 2 == 0 ? _bench : other).Id));

        Assert.Equal(40, made.Distinct().Count());
        Assert.Equal(41, Directory.GetFiles(_bench.PlansFolder, "TP-*.xml").Length);
    }

    [Fact]
    public async Task Text_in_files_and_findings_is_the_same_in_any_culture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var run = await RunToEnd(HotCold());
            var summary = _bench.Analyze(run.Id, MeasurementFilter.All);
            var vout = summary.Tests.Single(t => t.TestId == "VOUT");

            Assert.Equal("3.267 to 3.333 V", vout.Limits);
            Assert.Contains(" V, ", vout.WorstCase);
            Assert.DoesNotContain(",6", vout.WorstCase + string.Join(" ", summary.Findings.Select(f => f.Text)));
            Assert.Contains(summary.Findings, f => f.Text.Contains("drifts +0.") && f.Text.Contains("% of the limit window"));
            Assert.Equal("125 °C, 3.6 V, DUT 3", new MeasurementFilter(TemperatureC: 125, SupplyV: 3.6, Dut: 3).Describe());
            var bad = Assert.Throws<DomainException>(() => _bench.CreatePlan("x", "LDO-3300", [new TestLimit("VOUT", 3.4, 3.2)], [200.5], null, null, 1, "t"));
            Assert.Contains("min (3.4) must be below max (3.2)", bad.Message);
            Assert.Contains("Temperature 200.5 °C is outside the bench range -55 to 150 °C", bad.Message);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public async Task A_one_sided_limit_of_zero_gives_no_infinite_margins()
    {
        var plan = _bench.CreatePlan("zero", "LDO-3300", [new TestLimit("IQ", 0, null), new TestLimit("VOUT", 3.2, 3.4)], [-40, 25, 85], null, null, 2, "test");
        var run = await RunToEnd(plan);

        var summary = _bench.Analyze(run.Id, MeasurementFilter.All);

        Assert.DoesNotContain(summary.Findings, f => f.Text.Contains('∞') || f.Text.Contains("Infinity") || f.Text.Contains("NaN"));
        Assert.DoesNotContain(summary.Findings, f => f.TestId == "IQ" && f.Severity != FindingSeverity.Info);
        Assert.DoesNotContain(summary.Findings, f => f.TestId == "IQ" && f.Text.Contains("limit window"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
