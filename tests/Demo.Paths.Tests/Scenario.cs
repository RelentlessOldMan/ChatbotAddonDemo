using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Demo.Paths.Tests;

/// <summary>
/// The one job every path is tested with, the same as the walkthrough: make a hot/cold plan for the LDO-3300 from
/// its datasheet, run it, and report the findings. In a fresh workspace the plan is TP-1002 and the run R-1002.
/// </summary>
public static partial class Scenario
{
    public const string Question =
        "Make a hot/cold plan for the LDO-3300 from its datasheet: -40, 25, 85 and 125 °C at 3.6 V and 5 V, 3 units. Then run it and tell me what you find.";

    public const string SpecFile = "LDO-3300_datasheet.csv";
    public static readonly double[] Temperatures = [-40, 25, 85, 125];
    public static readonly double[] Supplies = [3.6, 5];
    public const int Units = 3;
    public const int Measurements = 4 * 2 * 3 * 5;

    public static JsonObject PlanArguments() => new()
    {
        ["specFile"] = SpecFile,
        ["samples"] = Units,
        ["temperaturesC"] = new JsonArray([.. Temperatures.Select(t => JsonValue.Create(t))]),
        ["supplyVoltages"] = new JsonArray([.. Supplies.Select(v => JsonValue.Create(v))]),
    };

    /// <summary>
    /// A scripted model that does the job the way a real one would: one tool per reply, ids taken from the
    /// previous result. It decides from the conversation it is sent, so it works however the request was framed.
    /// </summary>
    public static ModelReply ScriptedModel(ModelRequest request)
    {
        var results = request.ToolResults;
        var last = results.Count > 0 ? results[^1] : "";
        if (Declined(last)) return new ModelReply.Text("OK, I didn't do that. Tell me what you'd like to change.");

        return results.Count switch
        {
            0 => new ModelReply.ToolCall("list_spec_files", []),
            1 => new ModelReply.ToolCall("create_test_plan_from_spec", PlanArguments()),
            2 => new ModelReply.ToolCall("start_test_run", new JsonObject { ["planId"] = PlanId().Match(last).Value }),
            3 => new ModelReply.ToolCall("wait_for_test_run", new JsonObject { ["runId"] = RunId().Match(last).Value, ["maxSeconds"] = 60 }),
            4 => new ModelReply.ToolCall("get_run_summary", new JsonObject { ["runId"] = RunId().Match(last).Value }),
            _ => new ModelReply.Text($"{RunId().Match(last).Value} is done. Verdict: {Verdict().Match(last).Groups[1].Value}."),
        };
    }

    /// <summary>What the chat code tells the model when the user clicks Deny.</summary>
    public static bool Declined(string toolResult) => toolResult.Contains("declined", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"TP-\d+")] public static partial Regex PlanId();
    [GeneratedRegex(@"R-\d+")] public static partial Regex RunId();
    [GeneratedRegex(@"""verdict""\s*:\s*""(\w+)""")] public static partial Regex Verdict();
}
