using System.Globalization;
using System.Reflection;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Cli;
using Demo.Domain;
using ModelContextProtocol.Protocol;

// demo-cli: the "legacy" console tool for scripted and batch work. Ordinary commands call the
// domain directly, exactly as before AI existed. `demo-cli mcp` turns the same process into an MCP
// server that a coding agent starts and talks to over stdin/stdout (docs/adr/004-mcp-transport.md).

Console.OutputEncoding = System.Text.Encoding.UTF8; // units like µA and °C
var argList = args.ToList();
var workspace = TakeOption(argList, "--workspace")
    ?? Environment.GetEnvironmentVariable("DEMO_WORKSPACE")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatbotAddonDemo", "workspace");

if (argList.Count == 0 || argList[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var bench = new TestBenchService(workspace, BenchOptions.FromEnvironment());
var inv = CultureInfo.InvariantCulture;

try
{
    switch (argList[0])
    {
        case "specs":
            foreach (var spec in bench.ListSpecs())
                Console.WriteLine($"{spec.FileName,-30} {spec.Device,-10} {string.Join(", ", spec.Parameters.Select(p => p.Parameter))}");
            return 0;

        case "plans":
            Console.WriteLine($"{"PLAN",-8} {"DEVICE",-9} {"MEAS",5}  NAME");
            foreach (var p in bench.ListPlans())
                Console.WriteLine($"{p.Id,-8} {p.Device,-9} {p.MeasurementCount,5}  {p.Name}");
            return 0;

        case "plan" when argList.Count >= 3 && argList[1] == "show":
            Console.Write(bench.GetPlanXml(argList[2]));
            return 0;

        case "plan" when argList.Count >= 3 && argList[1] == "from-spec":
            var made = bench.CreatePlanFromSpec(argList[2], TakeOption(argList, "--name"),
                Numbers(TakeOption(argList, "--temps")), Numbers(TakeOption(argList, "--supplies")), Numbers(TakeOption(argList, "--loads")),
                int.Parse(TakeOption(argList, "--samples") ?? "3", inv), TakeOption(argList, "--tests")?.Split(','));
            Console.WriteLine($"Created {made.Plan.Id} ({made.Plan.MeasurementCount} measurements): {bench.PlanPath(made.Plan.Id)}");
            if (made.SkippedParameters.Count > 0) Console.WriteLine($"Skipped (bench cannot measure): {string.Join(", ", made.SkippedParameters)}");
            return 0;

        case "plan" when argList.Count >= 3 && argList[1] == "import":
            var imported = bench.ImportPlan(argList[2]);
            Console.WriteLine($"Imported as {imported.Id}: {bench.PlanPath(imported.Id)}");
            return 0;

        case "run" when argList.Count >= 2:
            return await Run(bench, argList[1]);

        case "runs":
            Console.WriteLine($"{"RUN",-7} {"PLAN",-8} {"STATUS",-11} {"DONE",9} {"FAILED",6}  STARTED");
            foreach (var r in bench.ListRuns(TakeOption(argList, "--plan")))
                Console.WriteLine($"{r.Id,-7} {r.PlanId,-8} {r.Status,-11} {$"{r.Completed}/{r.Total}",9} {r.Failed,6}  {r.Started:yyyy-MM-dd HH:mm}");
            return 0;

        case "summary" when argList.Count >= 2:
            PrintSummary(bench.Analyze(argList[1], Filter(argList)));
            return 0;

        case "query" when argList.Count >= 2:
            var rows = bench.GetMeasurements(argList[1], Filter(argList) with { FailedOnly = TakeFlag(argList, "--failed") });
            Console.WriteLine(ResultFiles.CsvHeader);
            foreach (var m in rows) Console.WriteLine(ResultFiles.ToCsvLine(m));
            return 0;

        case "report" when argList.Count >= 2:
            Console.WriteLine($"Wrote {bench.GenerateReport(argList[1])}");
            return 0;

        case "mcp":
            var policy = new McpExposurePolicy(AllowWrites: TakeFlag(argList, "--allow-writes"), AllowRuns: TakeFlag(argList, "--allow-runs"));
            var catalog = CapabilityCatalog.FromObject(new TestBenchCapabilities(bench));
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            await StdioMcpHost.RunAsync(catalog, policy, new Implementation { Name = McpClientConfig.ServerName, Version = version }, TestBenchCapabilities.Instructions);
            return 0;

        default:
            PrintHelp();
            return 2;
    }
}
catch (DomainException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static async Task<int> Run(TestBenchService bench, string planId)
{
    var run = bench.StartRun(planId);
    Console.WriteLine($"{run.Id}: running {run.PlanId} ({run.Total} measurements)");
    var lastShown = -1;
    while (run.Status == RunStatus.Running)
    {
        run = await bench.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(1));
        var tenth = run.PercentComplete / 10;
        if (tenth != lastShown && run.Status == RunStatus.Running) { Console.WriteLine($"  {run.PercentComplete,3}%  {run.Failed} failed so far"); lastShown = tenth; }
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

static MeasurementFilter Filter(List<string> args)
{
    var inv = CultureInfo.InvariantCulture;
    double? D(string name) => TakeOption(args, name) is { } v ? double.Parse(v, inv) : null;
    return new MeasurementFilter(TakeOption(args, "--test"), D("--temp"), D("--supply"), null,
        TakeOption(args, "--dut") is { } dut ? int.Parse(dut, inv) : null);
}

static double[]? Numbers(string? csv) =>
    csv?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

static void PrintHelp() => Console.WriteLine(
    """
    demo-cli — TestBench console tool

    Usage: demo-cli [--workspace <folder>] <command>

      specs                              List spec sheets (design files) in the workspace
      plans                              List test plans
      plan show <TP-id>                  Print a plan's XML
      plan from-spec <spec-file> --temps -40,25,125 --supplies 3.6,5 [--loads 100]
                     [--samples 3] [--tests IQ,VOUT] [--name <text>]
                                         Create a plan with limits copied from a spec sheet
      plan import <file.xml>             Validate and add a plan written elsewhere
      run <TP-id>                        Run a plan on the bench and print the findings
      runs [--plan <TP-id>]              List runs
      summary <R-id> [--test X] [--temp C] [--supply V] [--dut N]
                                         Key findings and verdict, optionally for some conditions
      query <R-id> [same filters] [--failed]
                                         Print matching measurements as CSV
      report <R-id>                      Write report.html for a run

      mcp [--allow-writes] [--allow-runs]
                                         Become an MCP server on stdin/stdout for coding agents.
                                         Read-only unless allowed to write files and/or run the bench.
    """);

static string? TakeOption(List<string> args, string name)
{
    var i = args.IndexOf(name);
    if (i < 0 || i + 1 >= args.Count) return null;
    var value = args[i + 1];
    args.RemoveRange(i, 2);
    return value;
}

static bool TakeFlag(List<string> args, string name) => args.Remove(name);
