using System.Text.Json;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;
using Microsoft.Extensions.AI;

namespace Demo.Capabilities.Tests;

public sealed class CapabilityCatalogTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "capability-tests", Guid.NewGuid().ToString("N"));
    private readonly TestBenchService _bench;
    private readonly CapabilityCatalog _catalog;

    public CapabilityCatalogTests()
    {
        _bench = new TestBenchService(_workspace, BenchOptions.Instant);
        _catalog = CapabilityCatalog.FromObject(new TestBenchCapabilities(_bench));
    }

    private async Task<JsonElement> Invoke(string name, AIFunctionArguments? args = null) =>
        JsonSerializer.SerializeToElement(await _catalog[name].Function.InvokeAsync(args ?? []), CapabilityCatalog.SerializerOptions);

    [Fact]
    public void Catalog_exposes_expected_capabilities_with_safety_classes()
    {
        Assert.Equal(new Dictionary<string, SafetyClass>
        {
            ["get_bench_info"] = SafetyClass.Read,
            ["list_spec_files"] = SafetyClass.Read,
            ["list_test_plans"] = SafetyClass.Read,
            ["get_test_plan"] = SafetyClass.Read,
            ["list_test_runs"] = SafetyClass.Read,
            ["wait_for_test_run"] = SafetyClass.Read,
            ["get_run_summary"] = SafetyClass.Read,
            ["query_measurements"] = SafetyClass.Read,
            ["create_test_plan"] = SafetyClass.Write,
            ["create_test_plan_from_spec"] = SafetyClass.Write,
            ["generate_report"] = SafetyClass.Write,
            ["start_test_run"] = SafetyClass.Execute,
            ["cancel_test_run"] = SafetyClass.Execute,
        }, _catalog.Capabilities.ToDictionary(c => c.Name, c => c.Safety));
    }

    [Fact]
    public void Every_capability_has_a_description_and_a_human_title()
    {
        Assert.All(_catalog.Capabilities, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
            Assert.NotEqual(c.Name, c.Title);
        });
    }

    [Fact]
    public void Schemas_describe_parameters_and_nested_test_limits()
    {
        var schema = _catalog["create_test_plan"].Function.JsonSchema;
        var properties = schema.GetProperty("properties");

        Assert.Equal(["name", "device", "tests", "samples"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("min and/or max", properties.GetProperty("tests").GetProperty("description").GetString());
        var item = properties.GetProperty("tests").GetProperty("items").GetProperty("properties");
        Assert.Contains("get_bench_info", item.GetProperty("testId").GetProperty("description").GetString());
    }

    [Fact]
    public async Task Plan_from_spec_then_run_then_analyze_works_through_the_capabilities()
    {
        var created = await Invoke("create_test_plan_from_spec", new AIFunctionArguments
        {
            ["specFile"] = "LDO-3300_datasheet.csv",
            ["samples"] = 3,
            ["temperaturesC"] = new[] { -40.0, 25, 85, 125 },
            ["supplyVoltages"] = new[] { 3.6, 5.0 },
        });
        var planId = created.GetProperty("plan").GetProperty("id").GetString();
        Assert.Equal(120, created.GetProperty("plan").GetProperty("measurementCount").GetInt32());
        Assert.True(File.Exists(created.GetProperty("xmlPath").GetString()));

        var started = await Invoke("start_test_run", new AIFunctionArguments { ["planId"] = planId });
        var runId = started.GetProperty("run").GetProperty("id").GetString();
        var done = await Invoke("wait_for_test_run", new AIFunctionArguments { ["runId"] = runId, ["maxSeconds"] = 30 });
        Assert.Equal("Completed", done.GetProperty("status").GetString());

        var summary = await Invoke("get_run_summary", new AIFunctionArguments { ["runId"] = runId });
        Assert.Equal("Fail", summary.GetProperty("summary").GetProperty("verdict").GetString());
        Assert.EndsWith("IQ.png", summary.GetProperty("files").GetProperty("plots")[1].GetString());

        var failures = await Invoke("query_measurements", new AIFunctionArguments { ["runId"] = runId, ["temperatureC"] = 125, ["failedOnly"] = true });
        Assert.All(failures.GetProperty("rows").EnumerateArray(), r => Assert.Equal(3, r.GetProperty("dut").GetInt32()));
    }

    [Fact]
    public async Task Query_results_are_capped_but_report_the_total()
    {
        var result = await Invoke("query_measurements", new AIFunctionArguments { ["runId"] = "R-1001", ["maxRows"] = 5 });

        Assert.Equal(45, result.GetProperty("totalMatching").GetInt32());
        Assert.Equal(5, result.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Domain_errors_come_back_as_error_results_not_exceptions()
    {
        var result = await Invoke("create_test_plan_from_spec", new AIFunctionArguments
        {
            ["specFile"] = "LDO-3300_datasheet.csv",
            ["samples"] = 3,
            ["temperaturesC"] = new[] { 200.0 },
        });

        Assert.Contains("outside the bench range", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Read_capabilities_never_create_or_change_files()
    {
        var before = Snapshot();
        foreach (var capability in _catalog.Capabilities.Where(c => c.Safety == SafetyClass.Read))
        {
            var args = new AIFunctionArguments();
            if (capability.Name is "get_test_plan") args["planId"] = "TP-1001";
            if (capability.Name is "wait_for_test_run" or "get_run_summary" or "query_measurements") args["runId"] = "R-1001";
            await capability.Function.InvokeAsync(args);
        }
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Duplicate_names_are_rejected() =>
        Assert.Throws<InvalidOperationException>(() => _catalog.Concat(_catalog));

    [Fact]
    public void Missing_description_is_a_startup_error() =>
        Assert.Throws<InvalidOperationException>(() => CapabilityCatalog.FromObject(new Undocumented()));

    private string Snapshot() => string.Join("\n",
        Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories).Order().Select(f => $"{f}|{File.GetLastWriteTimeUtc(f).Ticks}"));

    private sealed class Undocumented
    {
        [Capability("oops", SafetyClass.Read)]
        public int Oops() => 1;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
