using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Cli;
using Demo.Domain;

// demo-cli: the "legacy" console tool for scripted and batch work. Ordinary commands call the
// domain directly, exactly as before AI existed. `demo-cli mcp` turns the same process into an MCP
// server that a coding agent starts and talks to over stdin/stdout (docs/adr/004-mcp-via-console-tool.md).
// Exit codes: 0 success, 1 the command failed (a business rule or a file problem), 2 bad command line.

Console.OutputEncoding = System.Text.Encoding.UTF8; // units like µA and °C

CommandLine line;
try
{
    line = CommandLine.Parse(args);
}
catch (UsageException ex)
{
    return UsageError(ex.Message);
}

if (line.WantsHelp)
{
    PrintHelp();
    return 0;
}

var workspace = line.Text("--workspace")
    ?? Environment.GetEnvironmentVariable("DEMO_WORKSPACE")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatbotAddonDemo", "workspace");

// Opened only after the command line has been checked: opening a new workspace seeds it with examples.
TestBenchService? opened = null;
TestBenchService Bench() => opened ??= new TestBenchService(workspace, BenchOptions.FromEnvironment());

var p = line.Positionals;
try
{
    switch (p[0])
    {
        case "specs":
            line.Expect("specs", 1);
            foreach (var spec in Bench().ListSpecs())
                Console.WriteLine($"{spec.FileName,-30} {spec.Device,-10} {string.Join(", ", spec.Parameters.Select(x => x.Parameter))}");
            return 0;

        case "plans":
            line.Expect("plans", 1);
            Console.WriteLine($"{"PLAN",-8} {"DEVICE",-9} {"MEAS",5}  NAME");
            foreach (var plan in Bench().ListPlans())
                Console.WriteLine($"{plan.Id,-8} {plan.Device,-9} {plan.MeasurementCount,5}  {plan.Name}");
            return 0;

        case "plan" when p.Count >= 2 && p[1] == "show":
            line.Expect("plan show <TP-id>", 3);
            Console.Write(Bench().GetPlanXml(p[2]));
            return 0;

        case "plan" when p.Count >= 2 && p[1] == "from-spec":
            line.Expect("plan from-spec <spec-file> [--temps ...] [--supplies ...] [--loads ...] [--samples N] [--tests X,Y] [--name <text>]", 3,
                "--name", "--temps", "--supplies", "--loads", "--samples", "--tests");
            var made = Bench().CreatePlanFromSpec(p[2], line.Text("--name"),
                line.Numbers("--temps"), line.Numbers("--supplies"), line.Numbers("--loads"),
                line.WholeNumber("--samples") ?? 3, line.List("--tests"));
            Console.WriteLine($"Created {made.Plan.Id} ({made.Plan.MeasurementCount} measurements): {Bench().PlanPath(made.Plan.Id)}");
            if (made.SkippedParameters.Count > 0) Console.WriteLine($"Skipped (bench cannot measure): {string.Join(", ", made.SkippedParameters)}");
            return 0;

        case "plan" when p.Count >= 2 && p[1] == "import":
            line.Expect("plan import <file.xml>", 3);
            var imported = Bench().ImportPlan(p[2]);
            Console.WriteLine($"Imported as {imported.Id}: {Bench().PlanPath(imported.Id)}");
            return 0;

        case "plan":
            return UsageError(p.Count < 2
                ? "plan needs a subcommand: show, from-spec or import."
                : $"unknown plan command '{p[1]}'; use show, from-spec or import.");

        case "run":
            line.Expect("run <TP-id>", 2);
            return await Run(Bench(), p[1]);

        case "runs":
            line.Expect("runs [--plan <TP-id>]", 1, "--plan");
            Console.WriteLine($"{"RUN",-7} {"PLAN",-8} {"STATUS",-11} {"DONE",9} {"FAILED",6}  STARTED");
            foreach (var r in Bench().ListRuns(line.Text("--plan")))
                Console.WriteLine($"{r.Id,-7} {r.PlanId,-8} {r.Status,-11} {$"{r.Completed}/{r.Total}",9} {r.Failed,6}  {r.Started:yyyy-MM-dd HH:mm}");
            return 0;

        case "summary":
            line.Expect("summary <R-id> [--test X] [--temp C] [--supply V] [--dut N]", 2, "--test", "--temp", "--supply", "--dut");
            PrintSummary(Bench().Analyze(p[1], Filter(line)));
            return 0;

        case "query":
            line.Expect("query <R-id> [--test X] [--temp C] [--supply V] [--dut N] [--failed]", 2, "--test", "--temp", "--supply", "--dut", "--failed");
            var rows = Bench().GetMeasurements(p[1], Filter(line) with { FailedOnly = line.Flag("--failed") });
            // Conditions that match nothing are an error, as for summary (and with the same message: what the run does have).
            // A run with no failures is an answer, though: `--failed` alone may print just the header.
            if (rows.Count == 0) Bench().Analyze(p[1], Filter(line));
            Console.WriteLine(ResultFiles.CsvHeader);
            foreach (var m in rows) Console.WriteLine(ResultFiles.ToCsvLine(m));
            return 0;

        case "report":
            line.Expect("report <R-id>", 2);
            Console.WriteLine($"Wrote {Bench().GenerateReport(p[1])}");
            return 0;

        case "mcp":
            line.Expect("mcp [--allow-writes] [--allow-runs]", 1, "--allow-writes", "--allow-runs");
            var policy = new McpExposurePolicy(AllowWrites: line.Flag("--allow-writes"), AllowRuns: line.Flag("--allow-runs"));
            var bench = Bench();
            var catalog = CapabilityCatalog.FromObject(new TestBenchCapabilities(bench));
            // When the agent disconnects (or Ctrl+C), a run it started is cancelled rather than abandoned.
            await StdioMcpHost.RunAsync(catalog, policy, TestBenchCapabilities.Instructions,
                onStopping: () => RunShutdown.CancelActiveRunAsync(bench, RunShutdown.Grace));
            return 0;

        default:
            return UsageError($"unknown command '{p[0]}'. Run 'demo-cli help' for the commands.");
    }
}
catch (UsageException ex)
{
    return UsageError(ex.Message);
}
catch (Exception ex) when (ex is DomainException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int UsageError(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 2;
}

static async Task<int> Run(TestBenchService bench, string planId)
{
    var run = bench.StartRun(planId);
    Console.WriteLine($"{run.Id}: running {run.PlanId} ({run.Total} measurements)");

    // Ctrl+C stops the bench cleanly (the run is saved as Cancelled) instead of abandoning it mid-run.
    using var stop = new CancellationTokenSource();
    ConsoleCancelEventHandler onCtrlC = (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.CancelKeyPress += onCtrlC;
    try
    {
        var lastShown = -1;
        while (run.Status == RunStatus.Running)
        {
            run = await bench.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(1), stop.Token);
            var tenth = run.PercentComplete / 10;
            if (tenth != lastShown && run.Status == RunStatus.Running) { Console.WriteLine($"  {run.PercentComplete,3}%  {run.Failed} failed so far"); lastShown = tenth; }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        Console.WriteLine($"{run.Id}: cancelling...");
        await RunShutdown.CancelActiveRunAsync(bench, RunShutdown.Grace);
        run = bench.GetRun(run.Id);
    }
    finally
    {
        Console.CancelKeyPress -= onCtrlC;
    }

    Console.WriteLine($"{run.Id}: {run.Status}. Files: {bench.Files(run.Id).Folder}");
    if (run.Status != RunStatus.Completed) return 1;
    PrintSummary(bench.Analyze(run.Id, MeasurementFilter.All));
    return 0;
}

static void PrintSummary(RunSummary s)
{
    Console.WriteLine($"{s.RunId} ({s.Device}, {s.Scope}): {s.Measurements} measurements, {s.Failed} failed → {s.Verdict}");
    foreach (var f in s.Findings) Console.WriteLine($"  [{f.Severity}] {f.Text}");
}

static MeasurementFilter Filter(CommandLine line) =>
    new(line.Text("--test"), line.Number("--temp"), line.Number("--supply"), null, line.WholeNumber("--dut"));

static void PrintHelp() => Console.WriteLine(
    """
    demo-cli — TestBench console tool

    Usage: demo-cli [--workspace <folder>] <command>

    The workspace is --workspace, else the DEMO_WORKSPACE environment variable, else
    %LOCALAPPDATA%\ChatbotAddonDemo\workspace (created with examples on first use).

      specs                              List spec sheets (design files) in the workspace
      plans                              List test plans
      plan show <TP-id>                  Print a plan's XML
      plan from-spec <spec-file> [--temps -40,25,125] [--supplies 3.6,5] [--loads 100]
                     [--samples 3] [--tests IQ,VOUT] [--name <text>]
                                         Create a plan with limits copied from a spec sheet
                                         (conditions left out: 25 °C, 5 V, 100 mA)
      plan import <file.xml>             Validate and add a plan written elsewhere
      run <TP-id>                        Run a plan on the bench and print the findings (Ctrl+C cancels the run)
      runs [--plan <TP-id>]              List runs
      summary <R-id> [--test X] [--temp C] [--supply V] [--dut N]
                                         Key findings and verdict, optionally for some conditions
      query <R-id> [same filters] [--failed]
                                         Print matching measurements as CSV
      report <R-id>                      Write report.html for a run

      mcp [--allow-writes] [--allow-runs]
                                         Become an MCP server on stdin/stdout for coding agents.
                                         Read-only unless allowed to write files and/or run the bench.

    Exit codes: 0 done, 1 the command failed, 2 the command line is wrong.
    """);
