using System.Globalization;
using System.Text;
using System.Text.Json;
using Demo.Ai;
using Demo.Capabilities;
using Microsoft.Extensions.AI;
using static Demo.Ai.ScriptedChatClient;

namespace Demo.Desktop;

/// <summary>
/// The canned conversation used by <c>--demo</c>: no key, no network, the same every run, so walkthroughs
/// and the automated screenshots are reproducible. The tools still execute for real against the bench,
/// and the scripted replies are built from the real tool results, so the numbers always match the files.
/// </summary>
public static class DemoScript
{
    public const string FirstQuestion =
        "Make a hot/cold plan for the LDO-3300 from its datasheet: -40, 25, 85 and 125 °C at 3.6 V and 5 V, 3 units. Then run it and tell me what you find.";

    public const string SecondQuestion = "Which units failed at 125 °C?";

    private static readonly Dictionary<string, object?> PlanArguments = new()
    {
        ["specFile"] = "LDO-3300_datasheet.csv",
        ["samples"] = 3,
        ["temperaturesC"] = new[] { -40, 25, 85, 125 },
        ["supplyVoltages"] = new[] { 3.6, 5.0 },
        ["name"] = "LDO-3300 hot/cold characterization",
    };

    public static ScriptedChatClient CreateClient() => new(
    [
        // Turn 1: look at the spec sheets (auto), create the plan (approval), start the run (approval),
        // wait for it, analyze it, and report the findings.
        Call("list_spec_files", inputTokens: 2400, outputTokens: 30),
        Call("create_test_plan_from_spec", PlanArguments,
            preamble: "LDO-3300_datasheet.csv has limits for all five measurements the bench can make. I'll build the plan from it: 4 temperatures × 2 supplies × 3 units × 5 tests = 120 measurements.",
            inputTokens: 2900, outputTokens: 120),
        history => WasDeclined(history)
            ? Reply("OK, I didn't create the plan. Tell me what you'd like to change (temperatures, supplies, units or tests).", inputTokens: 3000, outputTokens: 30)(history)
            : Call("start_test_run", new Dictionary<string, object?> { ["planId"] = Last(history, "plan", "id") },
                preamble: $"Saved as {Last(history, "plan", "id")}. Running it uses the bench for about 5 seconds.", inputTokens: 3300, outputTokens: 60)(history),
        history => WasDeclined(history)
            ? Reply($"OK, I didn't start it. The plan is saved as {Last(history, "plan", "id")}; you can run it from the Test plans tab whenever the bench is free.", inputTokens: 3400, outputTokens: 40)(history)
            : Call("wait_for_test_run", new Dictionary<string, object?> { ["runId"] = Last(history, "run", "id"), ["maxSeconds"] = 60 }, inputTokens: 3500, outputTokens: 30)(history),
        history => Call("get_run_summary", new Dictionary<string, object?> { ["runId"] = Last(history, "id") }, inputTokens: 3700, outputTokens: 30)(history),
        history => Reply(DescribeSummary(LastResult(history)), inputTokens: 5200, outputTokens: 180)(history),

        // Turn 2: a question about specific conditions, answered from the measurement rows.
        history => Call("query_measurements", new Dictionary<string, object?> { ["runId"] = LastRunId(history), ["temperatureC"] = 125, ["failedOnly"] = true }, inputTokens: 5600, outputTokens: 40)(history),
        history => Reply(DescribeFailures(LastResult(history)), inputTokens: 6000, outputTokens: 120)(history),
    ])
    {
        ModelId = "demo-script",
        Fallback = Reply("(Demo mode: this scripted assistant only knows the walkthrough questions. Turn off demo mode and add an API key in Settings to chat for real.)"),
    };

    private static string DescribeSummary(JsonElement result)
    {
        var s = result.GetProperty("summary");
        var verdict = s.GetProperty("verdict").GetString() switch { "PassWithWarnings" => "pass with warnings", var v => v!.ToLowerInvariant() };
        var text = new StringBuilder($"{s.GetProperty("runId").GetString()} is done: {s.GetProperty("measurements").GetInt32()} measurements, {s.GetProperty("failed").GetInt32()} failed, so the verdict is {verdict}.\n\nKey findings:");
        foreach (var f in s.GetProperty("findings").EnumerateArray().Take(4))
            text.Append($"\n• {f.GetProperty("text").GetString()}");
        text.Append($"\n\nThe CSV files and plots are in {result.GetProperty("files").GetProperty("folder").GetString()}. Want me to write the HTML report?");
        return text.ToString();
    }

    private static string DescribeFailures(JsonElement result)
    {
        var rows = result.GetProperty("rows").EnumerateArray().ToList();
        if (rows.Count == 0) return "Nothing failed at 125 °C.";
        var byDut = rows.GroupBy(r => r.GetProperty("dut").GetInt32()).ToList();
        var text = new StringBuilder(byDut.Count == 1 ? $"Only DUT {byDut[0].Key} failed at 125 °C:" : $"{byDut.Count} units failed at 125 °C:");
        foreach (var r in rows)
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $"\n• DUT {r.GetProperty("dut").GetInt32()} {r.GetProperty("testId").GetString()} = {r.GetProperty("value").GetDouble():0.0} {r.GetProperty("units").GetString()} at {r.GetProperty("supplyV").GetDouble():0.0} V (limit {r.GetProperty("max").GetDouble():0.##})"));
        if (byDut.Count == 1)
            text.Append($"\nThe other units passed every test at 125 °C, so this looks like one bad part rather than a design problem. The IQ plot shows DUT {byDut[0].Key} pulling away above 85 °C.");
        return text.ToString();
    }

    /// <summary>True when the most recent tool result is the "user declined" message produced by approval denial.</summary>
    private static bool WasDeclined(IReadOnlyList<ChatMessage> history) =>
        history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault()?.Result?.ToString()?.Contains("declined", StringComparison.OrdinalIgnoreCase) == true;

    private static JsonElement LastResult(IReadOnlyList<ChatMessage> history) =>
        JsonSerializer.SerializeToElement(history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result, CapabilityCatalog.SerializerOptions);

    private static string? Last(IReadOnlyList<ChatMessage> history, params string[] path)
    {
        var element = LastResult(history);
        foreach (var name in path) element = element.GetProperty(name);
        return element.GetString();
    }

    private static string? LastRunId(IReadOnlyList<ChatMessage> history) =>
        history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().LastOrDefault(c => c.Arguments?.ContainsKey("runId") == true)?.Arguments!["runId"]?.ToString();
}
