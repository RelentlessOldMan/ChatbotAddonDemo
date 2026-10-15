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
    private static Task MeasurementsTaken(TestBenchService bench, string runId) =>
        Until(() => bench.GetRun(runId).Completed >= 3, $"{runId} to take 3 measurements");

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 3000; i++)
        {
            if (condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"Gave up waiting 30 s for {what}.");
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

        // The file says it is UTF-8 and is: an XML reader given only the bytes reads °C back.
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        var fromFile = System.Xml.Linq.XDocument.Load(_bench.PlanPath(plan.Id));
        Assert.Equal("°C", fromFile.Descendants("Sweep").First().Attribute("units")!.Value);
    }

    [Fact]
    public void A_plan_file_whose_id_does_not_match_its_file_name_is_refused()
    {
        // e.g. someone copies TP-1001.xml to TP-1005.xml and edits the limits, but not the id inside
        File.WriteAllText(Path.Combine(_bench.PlansFolder, "TP-1005.xml"), PlanXml(x => x.Replace("min=\"3.267\"", "min=\"3.1\"")));

        Assert.Equal(["TP-1001"], _bench.ListPlans().Select(p => p.Id));
        var ex = Assert.Throws<DomainException>(() => _bench.GetPlan("TP-1005"));
        Assert.Equal("TP-1005.xml says it is TP-1001; plan ids must match the file name.", ex.Message);
        Assert.Equal(ex.Message, Assert.Throws<DomainException>(() => _bench.StartRun("TP-1005")).Message);
        Assert.Equal(3.267, _bench.GetPlan("TP-1001").Tests.Single(t => t.Id == "VOUT").Min);
    }

    [Fact]
    public void Deleted_workspace_folders_read_as_empty_and_are_recreated_without_reseeding()
    {
        Directory.Delete(_bench.RunsFolder, recursive: true);
        Directory.Delete(_bench.SpecsFolder, recursive: true);

        Assert.Empty(_bench.ListRuns());
        Assert.Empty(_bench.ListSpecs());
        Assert.Null(_bench.BenchBusyWith());

        var restarted = new TestBenchService(_workspace, BenchOptions.Instant);
        Assert.True(Directory.Exists(restarted.RunsFolder));
        Assert.True(Directory.Exists(restarted.SpecsFolder));
        Assert.Empty(restarted.ListRuns()); // plans/ was there, so this is not a new workspace
        Assert.Equal(["TP-1001"], restarted.ListPlans().Select(p => p.Id));

        Directory.Delete(_bench.PlansFolder, recursive: true);
        Assert.Empty(_bench.ListPlans());
        var plan = _bench.CreatePlan("again", "LDO-3300", [new TestLimit("VOUT", 3.2, 3.4)], null, null, null, 1, "test");
        Assert.Equal("TP-1001", plan.Id);
    }

    [Fact]
    public async Task A_run_started_after_the_runs_folder_was_deleted_still_works()
    {
        Directory.Delete(_bench.RunsFolder, recursive: true);

        var run = await RunToEnd(_bench.GetPlan("TP-1001"));

        Assert.Equal(("R-1001", RunStatus.Completed), (run.Id, run.Status));
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

    [Fact]
    public void Unknown_measurements_are_reported_with_every_other_problem()
    {
        var ex = Assert.Throws<DomainException>(() => _bench.CreatePlan("x", "LDO-3300",
            [new TestLimit("NOISE", null, 45), new TestLimit("VOUT", 3.4, 3.2), new TestLimit("BOGUS", 1, 2)], [200], null, null, 1, "test"));

        Assert.Contains("cannot measure 'NOISE'", ex.Message);
        Assert.Contains("cannot measure 'BOGUS'", ex.Message);
        Assert.Contains("VOUT: min (3.4) must be below max (3.2)", ex.Message);
        Assert.Contains("Temperature 200 °C is outside the bench range", ex.Message);
    }

    [Theory]
    [InlineData("bad.json", """{ "device": "LDO-3300", "parameters": [ { "parameter": null, "name": "Output voltage", "units": "V", "max": 3.4 } ] }""", "parameter 1 needs a parameter id, a name and units")]
    [InlineData("bad.json", """{ "device": null, "parameters": [ { "parameter": "VOUT", "name": "Output voltage", "units": "V", "max": 3.4 } ] }""", "does not say which device")]
    [InlineData("bad.json", """{ "device": "LDO-3300", "parameters": [ { "parameter": 7, "name": "Output voltage", "units": "V" } ] }""", "Could not read spec sheet 'bad.json'")]
    [InlineData("bad.json", "{ not json", "Could not read spec sheet 'bad.json'")]
    [InlineData("bad.csv", "# device: LDO-3300\nparameter,name,units,min,typ,max\nVOUT,Output voltage,V\n", "line 3 has 3 columns")]
    [InlineData("bad.csv", "# device: LDO-3300\nparameter,name,units,min,typ,max\nVOUT,Output voltage,V,low,3.3,3.4\n", "Could not read spec sheet 'bad.csv'")]
    [InlineData("bad.csv", "parameter,name,units,min,typ,max\nVOUT,Output voltage,V,3.2,3.3,3.4\n", "has no '# device: ...' line")]
    public void Malformed_spec_sheets_are_rejected_readably_and_left_out_of_the_list(string file, string content, string expected)
    {
        File.WriteAllText(Path.Combine(_bench.SpecsFolder, file), content);

        Assert.Contains(expected, Assert.Throws<DomainException>(() => _bench.ReadSpec(file)).Message);
        Assert.DoesNotContain(file, _bench.ListSpecs().Select(s => s.FileName));
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

        // A very eager poller. This test is about the run surviving it, so a read that loses the race with a
        // save on a busy machine is counted rather than failing the test.
        int reads = 0, misses = 0;
        for (var status = RunStatus.Running; status == RunStatus.Running && misses < 100; Thread.Sleep(1))
        {
            try { status = watcher.GetRun(run.Id).Status; reads++; }
            catch (DomainException) { misses++; }
        }

        Assert.True(reads > 0);
        Assert.Equal(RunStatus.Completed, (await slow.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Waiting_for_a_run_in_another_program_polls_its_run_json()
    {
        var plan = HotCold();
        var slow = new TestBenchService(_workspace, new BenchOptions(TimeSpan.Zero, TimeSpan.FromMilliseconds(5)));
        var watcher = new TestBenchService(_workspace, BenchOptions.Instant); // e.g. the desktop app, while the console tool runs the bench
        var run = slow.StartRun(plan.Id);

        Assert.Equal(RunStatus.Running, (await watcher.WaitForRunAsync(run.Id, TimeSpan.Zero, TestContext.Current.CancellationToken)).Status);
        var done = await watcher.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Equal(plan.MeasurementCount, done.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_whose_run_json_cannot_be_written_ends_with_a_status_not_an_exception(bool cancel)
    {
        var slow = new TestBenchService(_workspace, new BenchOptions(TimeSpan.Zero, TimeSpan.FromMilliseconds(20)));
        var run = slow.StartRun("TP-1001");
        // A folder where SaveRun writes its temporary file makes every save fail, as a locked or full disk would.
        Directory.CreateDirectory(Path.Combine(slow.RunsFolder, run.Id, "run.json.tmp"));

        var ended = cancel
            ? await slow.CancelRunAsync(run.Id)
            : await slow.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(cancel ? RunStatus.Cancelled : RunStatus.Failed, ended.Status);
        if (!cancel) Assert.Contains("run.json.tmp", ended.Error);
    }

    [Fact]
    public void Results_csv_reading_skips_a_half_written_last_line()
    {
        var path = Path.Combine(_workspace, "partial.csv");
        File.WriteAllText(path, ResultFiles.CsvHeader + "\n" +
            "1,25,5,100,VOUT,3.301,V,3.267,3.333,PASS\n" +
            "1,25,5,100,IQ,44.2,µA,,60,PA", ResultFiles.CsvEncoding); // the bench was mid-write

        var row = Assert.Single(ResultFiles.ReadCsv(path));

        Assert.Equal(("VOUT", 3.301, true), (row.TestId, row.Value, row.Pass));
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

    [Fact]
    public async Task A_run_seen_as_finished_already_has_all_its_files()
    {
        // demo-cli prints the findings and exits as soon as it sees the run finished, which ends the process.
        // So by then run.json, summary.csv and the plots must be written. Polled as fast as possible, many times,
        // to land in the gap between the last measurement and the last file if there is one.
        var plan = HotCold();
        for (var i = 0; i < 10; i++)
        {
            var run = _bench.StartRun(plan.Id);
            while (run.Status == RunStatus.Running)
                run = await _bench.WaitForRunAsync(run.Id, TimeSpan.Zero, TestContext.Current.CancellationToken);

            var onDisk = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(_bench.Files(run.Id).Folder, "run.json")));
            Assert.Equal(RunStatus.Completed, run.Status);
            Assert.Equal("Completed", onDisk.GetProperty("Status").GetString());
            Assert.True(File.Exists(Path.Combine(_bench.Files(run.Id).Folder, "summary.csv")), $"{run.Id} has no summary.csv");
            await _bench.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); // let the bench go idle
            await Until(() => _bench.BenchBusyWith() is null, "the bench to go idle");
        }
    }

    [Fact]
    public async Task Two_programs_starting_runs_at_the_same_moment_never_both_use_the_bench()
    {
        // Each round, two programs (separate services on one workspace) start a plan at once: exactly one may run.
        var options = new BenchOptions(TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        var plan = _bench.ListPlans().Single().Id;
        for (var round = 0; round < 10; round++)
        {
            var programs = new[] { new TestBenchService(_workspace, options), new TestBenchService(_workspace, options) };
            using var bothReady = new Barrier(2);
            var outcomes = await Task.WhenAll(programs.Select(p => Task.Run(() =>
            {
                bothReady.SignalAndWait(TestContext.Current.CancellationToken);
                try { return (Program: p, Run: p.StartRun(plan), Error: (string?)null); }
                catch (DomainException ex) { return (Program: p, Run: (TestRun?)null, Error: ex.Message); }
            })));

            var started = Assert.Single(outcomes, o => o.Run is not null);
            Assert.Contains("busy", Assert.Single(outcomes, o => o.Run is null).Error);
            await started.Program.CancelRunAsync(started.Run!.Id);
        }
    }

    [Fact]
    public async Task A_run_whose_chamber_takes_minutes_to_settle_is_not_taken_for_abandoned()
    {
        // A real chamber soaks for minutes; no measurement (so no progress save) happens meanwhile.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Now);
        var soaking = new TestBenchService(_workspace, new BenchOptions(TimeSpan.FromMinutes(2), TimeSpan.Zero), time);
        var watcher = new TestBenchService(_workspace, BenchOptions.Instant, time); // another program
        var run = soaking.StartRun(_bench.ListPlans().Single().Id);

        for (var second = 1; second <= 40; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, TestContext.Current.CancellationToken); // lets the run save its heartbeat
            Assert.Equal(RunStatus.Running, watcher.GetRun(run.Id).Status);
        }
        await soaking.CancelRunAsync(run.Id);
    }

    [Fact]
    public void Plan_names_that_files_cannot_hold_are_refused_readably()
    {
        DomainException Named(string name) => Assert.Throws<DomainException>(() =>
            _bench.CreatePlanFromSpec("LDO-3300_datasheet.csv", name, [25], null, null, samples: 1));

        Assert.Contains("ordinary printable characters", Named("bad\u0001name").Message);
        Assert.Contains("ordinary printable characters", Named("half a pair \ud800").Message);
        Assert.Contains("at most 200 characters", Named(new string('x', 201)).Message);
    }

    [Fact]
    public void A_stray_file_with_a_huge_number_does_not_derail_new_ids()
    {
        File.WriteAllText(Path.Combine(_bench.PlansFolder, "TP-2147483647.xml"), "not a plan");
        File.WriteAllText(Path.Combine(_bench.PlansFolder, "TP-99999999999.xml"), "not a plan");

        Assert.Equal("TP-1002", HotCold().Id);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
