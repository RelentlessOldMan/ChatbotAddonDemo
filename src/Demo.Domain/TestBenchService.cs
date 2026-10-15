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

    /// <summary>How often a run saves its heartbeat while nothing else happens (e.g. the chamber is settling).</summary>
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ActiveRun? _active;

    public TestBenchService(string workspace, BenchOptions? options = null, TimeProvider? time = null)
    {
        Workspace = Path.GetFullPath(workspace);
        Options = options ?? BenchOptions.Demo;
        _time = time ?? TimeProvider.System;
        // A missing plans folder means a new workspace, so it gets the examples. A folder deleted later is just recreated empty.
        var isNew = !Directory.Exists(PlansFolder);
        foreach (var folder in new[] { SpecsFolder, PlansFolder, RunsFolder }) Directory.CreateDirectory(folder);
        if (isNew) Seed();
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
        PlanFiles().Select(TryLoadPlan).OfType<TestPlan>().OrderBy(p => IdNumber(p.Id)).ToList();

    public TestPlan GetPlan(string planId) => LoadPlan(PlanPath(planId));

    public string GetPlanXml(string planId) => File.ReadAllText(PlanPath(planId));

    public string PlanPath(string planId)
    {
        var path = Path.Combine(PlansFolder, $"{ValidId(planId, "TP", "plan", "TP-1002")}.xml");
        return File.Exists(path) ? path : throw new DomainException($"Test plan '{planId}' does not exist.");
    }

    /// <summary>Creates a plan from explicit limits. Empty condition lists mean "hold at the bench default".</summary>
    public TestPlan CreatePlan(string name, string device, IEnumerable<TestLimit> tests,
        IEnumerable<double>? temperaturesC, IEnumerable<double>? supplyVoltages, IEnumerable<double>? loadCurrentsMa, int samples, string source)
    {
        // An unknown test id is kept for Validate to report, so every problem is listed at once.
        var planned = tests.Select(t =>
            Bench.Measurements.FirstOrDefault(m => string.Equals(m.Id, t.TestId, StringComparison.OrdinalIgnoreCase)) is { } m
                ? new PlannedTest(m.Id, m.Name, m.Units, t.Min, t.Max)
                : new PlannedTest(t.TestId ?? "", t.TestId ?? "", "", t.Min, t.Max)).ToList();
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
        RunFolderNames()
            .Select(TryLoadRun)
            .OfType<TestRun>()
            .Where(r => planId is null || string.Equals(r.PlanId, planId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => IdNumber(r.Id))
            .ToList();

    public TestRun GetRun(string runId) =>
        TryLoadRun(RunId(runId)) ?? throw new DomainException($"Test run '{runId}' does not exist.");

    /// <summary>The run currently using the bench, from this process or another one (e.g. the console tool).</summary>
    /// <remarks>A run counts until its files are written, so whoever sees it finished can rely on them.</remarks>
    public TestRun? BenchBusyWith() =>
        _active?.Snapshot() is { Status: RunStatus.Running } mine ? mine : ListRuns().FirstOrDefault(r => r.Status == RunStatus.Running);

    /// <summary>Starts a plan on the bench and returns at once. One run at a time: the bench is a single physical station.</summary>
    public TestRun StartRun(string planId)
    {
        var plan = GetPlan(planId);
        ActiveRun active;
        lock (_gate)
        {
            if (BenchBusyWith() is { } busy)
                throw new DomainException($"The bench is busy with {busy.Id} ({busy.PercentComplete}% done). Wait for it to finish or cancel it.");

            // _gate only covers this process. Other programs (the console tool, an MCP server, another copy of the
            // app) may be starting a run in the same workspace, so the bench itself is claimed with a file only one
            // of them can hold. Windows lets go of it if the process dies, so a crash never leaves the bench locked.
            var benchLock = TryLockBench()
                ?? throw new DomainException("The bench is busy: another program is starting or running a plan. Wait for it to finish or cancel it there.");
            try
            {
                // A crashed run's folder may still hold the next id: whoever first puts run.json there owns it.
                TestRun run;
                do
                {
                    var id = $"R-{NextNumber(RunFolderNames(), "R")}";
                    Directory.CreateDirectory(RunFolder(id));
                    var now = _time.GetLocalNow();
                    run = new TestRun(id, plan.Id, plan.Name, plan.Device, RunStatus.Running, now, null, plan.MeasurementCount, 0, 0, now);
                }
                while (!TryCreateFile(Path.Combine(RunFolder(run.Id), "run.json"), JsonSerializer.Serialize(run, Json)));

                // Task is set before the run is published, so WaitForRunAsync/CancelRunAsync never see it null.
                // ExecuteAsync clears _active under _gate, so it can't do that before this block publishes it.
                active = new ActiveRun(run, benchLock);
                active.Task = Task.Run(() => ExecuteAsync(plan, active));
                _active = active;
            }
            catch
            {
                benchLock.Dispose();
                throw;
            }
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
            // WhenAny never throws, so however the run ended, the caller gets its status rather than an exception.
            using (var stopTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                await Task.WhenAny(active.Task!, Task.Delay(timeout, _time, stopTimer.Token));
                await stopTimer.CancelAsync();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return active.Snapshot(); // the final status, even if run.json could not be written
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
        await active.Task!.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return active.Snapshot();
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
        if (!rows.Any(filter.Matches))
        {
            static string Values<T>(IEnumerable<T> values, string format) =>
                string.Join(", ", values.Distinct().Order().Select(v => string.Format(Inv, format, v)));
            throw new DomainException($"No measurements in {run.Id} match {filter.Describe()}. Available tests are {Values(rows.Select(r => r.TestId), "{0}")}; " +
                $"temperatures {Values(rows.Select(r => r.TemperatureC), "{0:0.#} °C")}; supplies {Values(rows.Select(r => r.SupplyV), "{0:0.0#} V")}; " +
                $"loads {Values(rows.Select(r => r.LoadMa), "{0:0.#} mA")}; DUTs {Values(rows.Select(r => r.Dut), "{0}")}.");
        }
        return RunAnalyzer.Analyze(run, GetPlan(run.PlanId), rows, filter);
    }

    public RunFiles Files(string runId)
    {
        var id = GetRun(runId).Id; // GetRun only returns a run whose run.json agrees with the (validated) id asked for
        var folder = RunFolder(id);
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
                    await SoakAsync(active); // chamber settles at the new temperature
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
                            SaveProgress(active);
                            lastSave = _time.GetUtcNow();
                        }
                    }
                }
            }

            var rows = ResultFiles.ReadCsv(resultsPath);
            // Files first, status second: anyone who sees the run finished (WaitForRunAsync's caller, which may
            // print and exit at once, ending this process) can rely on its files being complete.
            var finished = active.Ending(RunStatus.Completed, _time.GetLocalNow());
            ResultFiles.WriteSummaryCsv(RunAnalyzer.Analyze(finished, plan, rows, MeasurementFilter.All), Path.Combine(RunFolder(id), "summary.csv"));
            ResultFiles.WritePlots(plan, rows, Path.Combine(RunFolder(id), "plots"));
            SaveRun(finished); // if this fails, the run is marked Failed below, with the reason
            active.ReleaseBench();
            active.Publish(finished);
        }
        catch (OperationCanceledException)
        {
            SaveThenPublish(active, active.Ending(RunStatus.Cancelled, _time.GetLocalNow()));
        }
        catch (Exception ex)
        {
            SaveThenPublish(active, active.Ending(RunStatus.Failed, _time.GetLocalNow(), ex.Message));
        }
        finally
        {
            active.ReleaseBench(); // already released, unless something above failed unexpectedly
            lock (_gate) if (_active == active) _active = null;
            RaiseChanged();
        }
    }

    private Task Wait(TimeSpan delay, CancellationToken ct) => delay > TimeSpan.Zero ? Task.Delay(delay, _time, ct) : Task.CompletedTask;

    /// <summary>
    /// The chamber settling at a new temperature, which takes minutes on a real bench. The heartbeat keeps going
    /// meanwhile; otherwise other programs would take the run for abandoned after <see cref="StaleAfter"/>.
    /// </summary>
    private async Task SoakAsync(ActiveRun active)
    {
        for (var left = Options.SoakTime; left > TimeSpan.Zero; left -= HeartbeatEvery)
        {
            await Wait(left < HeartbeatEvery ? left : HeartbeatEvery, active.Cancellation.Token);
            active.Beat(_time.GetLocalNow());
            SaveProgress(active);
        }
    }

    /// <summary>Saves a run's progress for other programs to see. Progress only: if a save fails, the next one catches up.</summary>
    private void SaveProgress(ActiveRun active)
    {
        TrySaveRun(active.Snapshot());
        RaiseChanged();
    }

    /// <summary>Claims the bench for a run, or returns null if another program has it. See <see cref="StartRun"/>.</summary>
    private FileStream? TryLockBench()
    {
        Directory.CreateDirectory(RunsFolder); // in case it was deleted since start-up
        try { return new FileStream(Path.Combine(RunsFolder, ".bench.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; } // held by another program (a sharing violation)
    }

    private TestPlan Save(TestPlan draft)
    {
        TestPlan plan;
        lock (_gate)
        {
            TestPlanXml.Validate(draft);
            Directory.CreateDirectory(PlansFolder); // in case it was deleted since start-up
            do plan = draft with { Id = $"TP-{NextNumber(PlanFiles().Select(Path.GetFileNameWithoutExtension)!, "TP")}" };
            while (!TryCreateFile(Path.Combine(PlansFolder, plan.Id + ".xml"), TestPlanXml.ToXml(plan))); // another program took that id
        }
        RaiseChanged(); // outside the lock, so a handler may call back into this service
        return plan;
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

    /// <summary>Only call with an id that went through <see cref="RunId"/> (or one this process made).</summary>
    private string RunFolder(string runId) => Path.Combine(RunsFolder, runId);

    /// <summary>
    /// Plan and run ids become file names, so anything else (e.g. "..\..\x") is refused before the
    /// file system is touched. The message is meant for whoever typed the id, person or model.
    /// </summary>
    private static string ValidId(string? id, string prefix, string what, string example)
    {
        var text = id?.Trim().ToUpperInvariant();
        return IsValidId(text, prefix) ? text! : throw new DomainException($"'{id}' is not a valid {what} id; {what} ids look like '{example}'.");
    }

    private static string RunId(string? runId) => ValidId(runId, "R", "run", "R-1002");

    /// <summary>PREFIX-digits, e.g. TP-1002 or R-1002 (at most 9 digits, so the number fits an int).</summary>
    private static bool IsValidId(string? id, string prefix) =>
        id is not null && id.StartsWith(prefix + "-", StringComparison.Ordinal) &&
        id[(prefix.Length + 1)..] is { Length: > 0 and <= 9 } digits && digits.All(char.IsAsciiDigit);

    private TestRun? TryLoadRun(string runId)
    {
        if (!IsValidId(runId, "R")) return null;
        var active = _active?.Snapshot();
        if (active is not null && active.Id == runId) return active;
        var path = Path.Combine(RunFolder(runId), "run.json");
        if (!File.Exists(path)) return null;
        try
        {
            var run = JsonSerializer.Deserialize<TestRun>(ReadShared(path), Json);
            // A run.json that names another run or a malformed plan id is not trusted to steer later file access.
            if (run is null || run.Id != runId || !IsValidId(run.PlanId, "TP")) return null;
            return run is { Status: RunStatus.Running } && _time.GetLocalNow() - run.Heartbeat > StaleAfter
                ? run with { Status = RunStatus.Interrupted }
                : run;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Shares everything, so a read never blocks the bench more than Windows insists on (it still won't let
    // File.Move replace an open file; SaveRun retries for that). Retries the moment of the swap itself, for about
    // a quarter of a second, so on a busy machine a run being saved doesn't briefly look like it doesn't exist.
    private static string ReadShared(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8) { Thread.Sleep(Math.Min(10 * attempt, 50)); }
        }
    }

    private void SaveRun(TestRun run)
    {
        var path = Path.Combine(RunFolder(run.Id), "run.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(run, Json));
        // Windows won't replace a file while anyone has it open (FileShare.Delete or not), and other programs poll
        // run.json. Their reads take microseconds, so retry for up to about a second rather than fail the run.
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(temp, path, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 25) { Thread.Sleep(Math.Min(5 * attempt, 50)); }
        }
    }

    private void SaveThenPublish(ActiveRun active, TestRun final)
    {
        TrySaveRun(final);
        active.ReleaseBench();
        active.Publish(final);
    }

    /// <summary>The save after a run has already gone wrong: there is nobody left to report a second failure to.</summary>
    private void TrySaveRun(TestRun run)
    {
        try { SaveRun(run); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Writes a new file all at once, or returns false if the file already exists (e.g. another program just created it).</summary>
    private static bool TryCreateFile(string path, string content)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            return false;
        }
    }

    /// <summary>
    /// The file name is the plan's id everywhere else (PlanPath, new ids), so a copied file that still has the
    /// old id inside would otherwise show up as the old plan, and runs of it would record the wrong plan.
    /// </summary>
    private static TestPlan LoadPlan(string path)
    {
        var plan = TestPlanXml.Parse(File.ReadAllText(path));
        return string.Equals(plan.Id, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
            ? plan
            : throw new DomainException($"{Path.GetFileName(path)} says it is {plan.Id}; plan ids must match the file name.");
    }

    private static TestPlan? TryLoadPlan(string path)
    {
        try { return LoadPlan(path); }
        catch (Exception ex) when (ex is DomainException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static SpecSheet? TryLoadSpec(string path)
    {
        try { return SpecSheet.Load(path); }
        catch (Exception ex) when (ex is DomainException or IOException or UnauthorizedAccessException) { return null; }
    }

    // A folder deleted while the program runs reads as empty rather than as an error.
    private IEnumerable<string> PlanFiles() =>
        Directory.Exists(PlansFolder) ? Directory.EnumerateFiles(PlansFolder, "TP-*.xml") : [];

    private IEnumerable<string> RunFolderNames() =>
        Directory.Exists(RunsFolder) ? Directory.EnumerateDirectories(RunsFolder, "R-*").Select(d => Path.GetFileName(d)) : [];

    private static int IdNumber(string id) => int.TryParse(id.AsSpan(id.IndexOf('-') + 1), Inv, out var n) ? n : 0;

    /// <summary>The number after the highest valid id; names that aren't valid ids (e.g. a stray "TP-x.xml") are ignored.</summary>
    private static int NextNumber(IEnumerable<string> ids, string prefix)
    {
        var highest = ids.Where(id => IsValidId(id, prefix)).Select(IdNumber).DefaultIfEmpty(1000).Max();
        return highest < 999_999_999 ? highest + 1 : throw new DomainException($"There are no {prefix} ids left (the highest is {prefix}-{highest}).");
    }

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
    private sealed class ActiveRun(TestRun initial, FileStream benchLock)
    {
        private readonly Lock _gate = new();
        private TestRun _run = initial;
        private FileStream? _benchLock = benchLock;

        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }

        public TestRun Snapshot() { lock (_gate) return _run; }

        public void Record(bool pass, DateTimeOffset now)
        {
            lock (_gate) _run = _run with { Completed = _run.Completed + 1, Failed = _run.Failed + (pass ? 0 : 1), Heartbeat = now };
        }

        public void Beat(DateTimeOffset now)
        {
            lock (_gate) _run = _run with { Heartbeat = now };
        }

        /// <summary>Lets other programs use the bench. Done just before <see cref="Publish"/>, so whoever sees the run finished can start the next one.</summary>
        public void ReleaseBench() => Interlocked.Exchange(ref _benchLock, null)?.Dispose();

        /// <summary>The run as it will be once finished; nobody sees it until <see cref="Publish"/>.</summary>
        public TestRun Ending(RunStatus status, DateTimeOffset now, string? error = null)
        {
            lock (_gate) return _run with { Status = status, Finished = now, Heartbeat = now, Error = error };
        }

        public void Publish(TestRun final)
        {
            lock (_gate) _run = final;
        }
    }
}
