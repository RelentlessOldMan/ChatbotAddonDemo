using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demo.Domain;

/// <summary>Result of generating a plan from a spec sheet: the plan, plus spec rows the bench can't measure.</summary>
public sealed record PlanFromSpec(TestPlan Plan, IReadOnlyList<string> SkippedParameters);

/// <summary>A test to include in a new plan: which measurement, and its limits.</summary>
public sealed record TestLimit(string TestId, double? Min, double? Max);

/// <summary>
/// The legacy tool's business logic. The UI, the console commands, the in-app assistant and
/// coding agents (via MCP) all go through here, so the bench rules apply no matter who is asking.
///
/// State lives in a workspace folder of plain files, which is how the real tools work:
///   specs/  datasheets (CSV/JSON) that plans can be built from
///   plans/  TP-nnnn.xml test plans (+ TestPlan.xsd)
///   runs/   R-nnnn/ run.json, results.csv, summary.csv, plots/*.png, report.html
/// Thread-safe: the UI, the assistant and the running bench use it concurrently.
/// </summary>
public sealed class TestBenchService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>A Running run whose heartbeat is older than this was abandoned (its process exited).</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ActiveRun? _active;

    public TestBenchService(string workspace, BenchOptions? options = null, TimeProvider? time = null)
    {
        Workspace = Path.GetFullPath(workspace);
        Options = options ?? BenchOptions.Demo;
        _time = time ?? TimeProvider.System;
        if (!Directory.Exists(PlansFolder)) Seed();
    }

    public string Workspace { get; }
    public string SpecsFolder => Path.Combine(Workspace, "specs");
    public string PlansFolder => Path.Combine(Workspace, "plans");
    public string RunsFolder => Path.Combine(Workspace, "runs");
    public BenchOptions Options { get; }

    /// <summary>Raised (on a background thread when a run is executing) after plans or runs change, and as a run progresses.</summary>
    public event EventHandler? Changed;

    // ---- Spec sheets (the customer's design files) ------------------------------------------------

    public IReadOnlyList<SpecSheet> ListSpecs() =>
        Directory.Exists(SpecsFolder)
            ? Directory.EnumerateFiles(SpecsFolder).Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).Select(TryLoadSpec).OfType<SpecSheet>().ToList()
            : [];

    /// <summary>Reads a spec from the workspace's specs folder. Bare file names only, so callers can't read arbitrary files.</summary>
    public SpecSheet ReadSpec(string fileName)
    {
        if (fileName.IndexOfAny(['/', '\\', ':']) >= 0 || fileName.Contains(".."))
            throw new DomainException("Give just the spec file name (e.g. 'LDO-3300_datasheet.csv'); spec sheets are read from the workspace's specs folder only.");
        var path = Path.Combine(SpecsFolder, fileName);
        if (!File.Exists(path))
            throw new DomainException($"There is no spec sheet named '{fileName}'. Available: {string.Join(", ", ListSpecs().Select(s => s.FileName))}.");
        return SpecSheet.Load(path);
    }

    // ---- Test plans ----------------------------------------------------------------------------

    public IReadOnlyList<TestPlan> ListPlans() =>
        Directory.EnumerateFiles(PlansFolder, "TP-*.xml").Select(TryLoadPlan).OfType<TestPlan>().OrderBy(p => IdNumber(p.Id)).ToList();

    public TestPlan GetPlan(string planId) => TestPlanXml.Parse(File.ReadAllText(PlanPath(planId)));

    public string GetPlanXml(string planId) => File.ReadAllText(PlanPath(planId));

    public string PlanPath(string planId)
    {
        var path = Path.Combine(PlansFolder, $"{planId.Trim().ToUpperInvariant()}.xml");
        return File.Exists(path) ? path : throw new DomainException($"Test plan '{planId}' does not exist.");
    }

    /// <summary>Creates a plan from explicit limits. Empty condition lists mean "hold at the bench default".</summary>
    public TestPlan CreatePlan(string name, string device, IEnumerable<TestLimit> tests,
        IEnumerable<double>? temperaturesC, IEnumerable<double>? supplyVoltages, IEnumerable<double>? loadCurrentsMa, int samples, string source)
    {
        var planned = tests.Select(t =>
        {
            var m = Bench.Measurement(t.TestId);
            return new PlannedTest(m.Id, m.Name, m.Units, t.Min, t.Max);
        }).ToList();
        return Save(new TestPlan("", name.Trim(), device.Trim(), _time.GetLocalNow(), source,
            Conditions(temperaturesC, supplyVoltages, loadCurrentsMa), samples, planned));
    }

    /// <summary>Creates a plan whose tests and limits come from a spec sheet, so nobody retypes limits.</summary>
    public PlanFromSpec CreatePlanFromSpec(string specFile, string? name,
        IEnumerable<double>? temperaturesC, IEnumerable<double>? supplyVoltages, IEnumerable<double>? loadCurrentsMa, int samples,
        IEnumerable<string>? onlyTests = null)
    {
        var spec = ReadSpec(specFile);
        var wanted = onlyTests?.Select(t => t.Trim().ToUpperInvariant()).ToHashSet();
        var rows = spec.Parameters.Where(p => wanted is null || wanted.Contains(p.Parameter.ToUpperInvariant())).ToList();
        if (wanted is not null && wanted.Except(rows.Select(r => r.Parameter.ToUpperInvariant())).FirstOrDefault() is { } missing)
            throw new DomainException($"'{specFile}' has no parameter '{missing}'.");

        var skipped = rows.Where(r => !Bench.Measurements.Any(m => string.Equals(m.Id, r.Parameter, StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Parameter).ToList();
        var tests = rows.Where(r => !skipped.Contains(r.Parameter)).Select(r =>
        {
            var m = Bench.Measurement(r.Parameter);
            if (r.Units != m.Units) throw new DomainException($"'{specFile}' gives {r.Parameter} in {r.Units}; the bench measures it in {m.Units}.");
            return new PlannedTest(m.Id, m.Name, m.Units, r.Min, r.Max);
        }).ToList();

        var plan = Save(new TestPlan("", string.IsNullOrWhiteSpace(name) ? $"{spec.Device} from {spec.FileName}" : name.Trim(), spec.Device,
            _time.GetLocalNow(), $"spec:{spec.FileName}", Conditions(temperaturesC, supplyVoltages, loadCurrentsMa), samples, tests));
        return new PlanFromSpec(plan, skipped);
    }

    /// <summary>Imports a plan file written by hand or by another tool. It gets a new id.</summary>
    public TestPlan ImportPlan(string xmlPath) =>
        Save(TestPlanXml.Parse(File.ReadAllText(xmlPath)) with { Source = $"import:{Path.GetFileName(xmlPath)}" });

    // ---- Runs ----------------------------------------------------------------------------------

    public IReadOnlyList<TestRun> ListRuns(string? planId = null) =>
        Directory.EnumerateDirectories(RunsFolder, "R-*")
            .Select(d => TryLoadRun(Path.GetFileName(d)))
            .OfType<TestRun>()
            .Where(r => planId is null || string.Equals(r.PlanId, planId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => IdNumber(r.Id))
            .ToList();

    public TestRun GetRun(string runId) =>
        TryLoadRun(runId.Trim().ToUpperInvariant()) ?? throw new DomainException($"Test run '{runId}' does not exist.");

    /// <summary>The run currently using the bench, from this process or another one (e.g. the console tool).</summary>
    public TestRun? BenchBusyWith() =>
        _active?.Snapshot() ?? ListRuns().FirstOrDefault(r => r.Status == RunStatus.Running);

    /// <summary>Starts a plan on the bench and returns at once. One run at a time: the bench is a single physical station.</summary>
    public TestRun StartRun(string planId)
    {
        var plan = GetPlan(planId);
        ActiveRun active;
        lock (_gate)
        {
            if (BenchBusyWith() is { } busy)
                throw new DomainException($"The bench is busy with {busy.Id} ({busy.PercentComplete}% done). Wait for it to finish or cancel it.");

            var id = $"R-{NextNumber(Directory.EnumerateDirectories(RunsFolder, "R-*").Select(Path.GetFileName)!)}";
            Directory.CreateDirectory(RunFolder(id));
            var now = _time.GetLocalNow();
            var run = new TestRun(id, plan.Id, plan.Name, plan.Device, RunStatus.Running, now, null, plan.MeasurementCount, 0, 0, now);
            SaveRun(run);
            active = _active = new ActiveRun(run);
            active.Task = Task.Run(() => ExecuteAsync(plan, active));
        }
        RaiseChanged();
        return active.Snapshot();
    }

    /// <summary>Waits until the run finishes or <paramref name="timeout"/> passes, then returns its state.</summary>
    public async Task<TestRun> WaitForRunAsync(string runId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var active = _active;
        if (active is not null && string.Equals(active.Snapshot().Id, runId, StringComparison.OrdinalIgnoreCase))
        {
            try { await active.Task!.WaitAsync(timeout, _time, cancellationToken); }
            catch (TimeoutException) { }
            return active.Task!.IsCompleted ? GetRun(runId) : active.Snapshot();
        }

        // Running in another process: poll its run.json.
        var deadline = _time.GetUtcNow() + timeout;
        var run = GetRun(runId);
        while (run.Status == RunStatus.Running && _time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), _time, cancellationToken);
            run = GetRun(runId);
        }
        return run;
    }

    public async Task<TestRun> CancelRunAsync(string runId)
    {
        var active = _active;
        if (active is null || !string.Equals(active.Snapshot().Id, runId, StringComparison.OrdinalIgnoreCase))
        {
            var run = GetRun(runId);
            throw new DomainException(run.Status == RunStatus.Running
                ? $"{run.Id} is running in another program; cancel it there."
                : $"{run.Id} is not running (it is {run.Status}).");
        }
        await active.Cancellation.CancelAsync();
        try { await active.Task!; } catch (OperationCanceledException) { }
        return GetRun(runId);
    }

    // ---- Results and analysis -------------------------------------------------------------------

    public IReadOnlyList<Measurement> GetMeasurements(string runId, MeasurementFilter filter) =>
        ResultFiles.ReadCsv(Files(runId).ResultsCsv).Where(filter.Matches).ToList();

    /// <summary>Statistics and key findings for a finished run, optionally narrowed to some conditions.</summary>
    public RunSummary Analyze(string runId, MeasurementFilter filter)
    {
        var run = GetRun(runId);
        if (run.Status == RunStatus.Running)
            throw new DomainException($"{run.Id} is still running ({run.PercentComplete}% done). Wait for it to finish first.");
        var rows = ResultFiles.ReadCsv(Files(runId).ResultsCsv);
        if (rows.Count == 0) throw new DomainException($"{run.Id} has no measurements ({run.Status}).");
        return RunAnalyzer.Analyze(run, GetPlan(run.PlanId), rows, filter);
    }

    public RunFiles Files(string runId)
    {
        var folder = RunFolder(GetRun(runId).Id);
        var plots = Path.Combine(folder, "plots");
        var report = Path.Combine(folder, "report.html");
        return new RunFiles(folder, Path.Combine(folder, "results.csv"), Path.Combine(folder, "summary.csv"), plots,
            Directory.Exists(plots) ? Directory.GetFiles(plots, "*.png").Order().ToList() : [],
            File.Exists(report) ? report : null);
    }

    /// <summary>Writes report.html (findings, statistics, plots) for a completed run and returns its path.</summary>
    public string GenerateReport(string runId)
    {
        var run = GetRun(runId);
        if (run.Status != RunStatus.Completed) throw new DomainException($"{run.Id} is {run.Status}; reports are made from completed runs.");
        var summary = Analyze(runId, MeasurementFilter.All);
        var path = Path.Combine(RunFolder(run.Id), "report.html");
        ResultFiles.WriteReport(run, GetPlan(run.PlanId), summary, Files(runId), path);
        RaiseChanged();
        return path;
    }

    // ---- Internals ------------------------------------------------------------------------------

    private async Task ExecuteAsync(TestPlan plan, ActiveRun active)
    {
        var id = active.Snapshot().Id;
        var bench = new BenchSimulator(id);
        var resultsPath = Path.Combine(RunFolder(id), "results.csv");
        var lastSave = _time.GetUtcNow();
        try
        {
            await using (var writer = new StreamWriter(new FileStream(resultsPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), ResultFiles.CsvEncoding))
            {
                await writer.WriteLineAsync(ResultFiles.CsvHeader);
                foreach (var t in plan.ValuesOf(Bench.Temperature))
                {
                    await Wait(Options.SoakTime, active.Cancellation.Token); // chamber settles at the new temperature
                    foreach (var v in plan.ValuesOf(Bench.SupplyVoltage))
                    foreach (var load in plan.ValuesOf(Bench.LoadCurrent))
                    for (var dut = 1; dut <= plan.Samples; dut++)
                    foreach (var test in plan.Tests)
                    {
                        active.Cancellation.Token.ThrowIfCancellationRequested();
                        await Wait(Options.MeasurementTime, active.Cancellation.Token);
                        var value = bench.Measure(test.Id, dut, t, v, load);
                        var m = new Measurement(dut, t, v, load, test.Id, value, test.Units, test.Min, test.Max, test.Passes(value));
                        await writer.WriteLineAsync(ResultFiles.ToCsvLine(m));
                        active.Record(m.Pass, _time.GetLocalNow());

                        if (_time.GetUtcNow() - lastSave > TimeSpan.FromMilliseconds(250))
                        {
                            await writer.FlushAsync();
                            SaveRun(active.Snapshot());
                            lastSave = _time.GetUtcNow();
                            RaiseChanged();
                        }
                    }
                }
            }

            var rows = ResultFiles.ReadCsv(resultsPath);
            var finished = active.Finish(RunStatus.Completed, _time.GetLocalNow());
            ResultFiles.WriteSummaryCsv(RunAnalyzer.Analyze(finished, plan, rows, MeasurementFilter.All), Path.Combine(RunFolder(id), "summary.csv"));
            ResultFiles.WritePlots(plan, rows, Path.Combine(RunFolder(id), "plots"));
            SaveRun(finished);
        }
        catch (OperationCanceledException)
        {
            SaveRun(active.Finish(RunStatus.Cancelled, _time.GetLocalNow()));
        }
        catch (Exception ex)
        {
            SaveRun(active.Finish(RunStatus.Failed, _time.GetLocalNow(), ex.Message));
        }
        finally
        {
            lock (_gate) if (_active == active) _active = null;
            RaiseChanged();
        }
    }

    private Task Wait(TimeSpan delay, CancellationToken ct) => delay > TimeSpan.Zero ? Task.Delay(delay, _time, ct) : Task.CompletedTask;

    private TestPlan Save(TestPlan draft)
    {
        lock (_gate)
        {
            var plan = draft with { Id = $"TP-{NextNumber(Directory.EnumerateFiles(PlansFolder, "TP-*.xml").Select(Path.GetFileNameWithoutExtension)!)}" };
            TestPlanXml.Validate(plan);
            File.WriteAllText(Path.Combine(PlansFolder, plan.Id + ".xml"), TestPlanXml.ToXml(plan));
            RaiseChanged();
            return plan;
        }
    }

    private static IReadOnlyList<ConditionSweep> Conditions(IEnumerable<double>? temperatures, IEnumerable<double>? supplies, IEnumerable<double>? loads)
    {
        ConditionSweep Sweep(string id, IEnumerable<double>? values)
        {
            var definition = Bench.Condition(id);
            var list = values?.ToList() ?? [];
            return new ConditionSweep(definition.Id, definition.Units, list.Count > 0 ? list : [definition.Default]);
        }
        return [Sweep(Bench.Temperature, temperatures), Sweep(Bench.SupplyVoltage, supplies), Sweep(Bench.LoadCurrent, loads)];
    }

    private string RunFolder(string runId) => Path.Combine(RunsFolder, runId);

    private TestRun? TryLoadRun(string runId)
    {
        var active = _active?.Snapshot();
        if (active is not null && active.Id == runId) return active;
        var path = Path.Combine(RunFolder(runId), "run.json");
        if (!File.Exists(path)) return null;
        try
        {
            var run = JsonSerializer.Deserialize<TestRun>(File.ReadAllText(path), Json);
            return run is { Status: RunStatus.Running } && _time.GetLocalNow() - run.Heartbeat > StaleAfter
                ? run with { Status = RunStatus.Interrupted }
                : run;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private void SaveRun(TestRun run)
    {
        var path = Path.Combine(RunFolder(run.Id), "run.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(run, Json));
        File.Move(temp, path, overwrite: true);
    }

    private TestPlan? TryLoadPlan(string path)
    {
        try { return TestPlanXml.Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is DomainException or IOException) { return null; }
    }

    private static SpecSheet? TryLoadSpec(string path)
    {
        try { return SpecSheet.Load(path); }
        catch (Exception ex) when (ex is DomainException or IOException) { return null; }
    }

    private static int IdNumber(string id) => int.TryParse(id.AsSpan(id.IndexOf('-') + 1), Inv, out var n) ? n : 0;

    private static int NextNumber(IEnumerable<string> ids) => ids.Select(IdNumber).DefaultIfEmpty(1000).Max() + 1;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>First use: example datasheets, a room-temperature plan and one completed run.</summary>
    private void Seed()
    {
        Directory.CreateDirectory(SpecsFolder);
        Directory.CreateDirectory(PlansFolder);
        Directory.CreateDirectory(RunsFolder);
        File.WriteAllText(Path.Combine(PlansFolder, "TestPlan.xsd"), TestPlanXml.SchemaText);
        File.WriteAllText(Path.Combine(SpecsFolder, "LDO-3300_datasheet.csv"), SeedData.DatasheetCsv);
        File.WriteAllText(Path.Combine(SpecsFolder, "LDO-3300_rev-B_targets.json"), SeedData.RevBTargetsJson);

        var plan = CreatePlanFromSpec("LDO-3300_datasheet.csv", "LDO-3300 room-temperature check",
            [25], [3.6, 5.0, 5.5], [100], samples: 3).Plan;

        // The seed run uses the same code path as any other run, just with no delays.
        var seeder = new TestBenchService(Workspace, BenchOptions.Instant, _time);
        seeder.StartRun(plan.Id);
        seeder._active?.Task?.GetAwaiter().GetResult();
    }

    /// <summary>The run in progress in this process. Mutable counters behind a lock; readers get snapshots.</summary>
    private sealed class ActiveRun(TestRun initial)
    {
        private readonly Lock _gate = new();
        private TestRun _run = initial;

        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }

        public TestRun Snapshot() { lock (_gate) return _run; }

        public void Record(bool pass, DateTimeOffset now)
        {
            lock (_gate) _run = _run with { Completed = _run.Completed + 1, Failed = _run.Failed + (pass ? 0 : 1), Heartbeat = now };
        }

        public TestRun Finish(RunStatus status, DateTimeOffset now, string? error = null)
        {
            lock (_gate) return _run = _run with { Status = status, Finished = now, Heartbeat = now, Error = error };
        }
    }
}
