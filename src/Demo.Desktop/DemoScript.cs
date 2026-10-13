using Demo.Ai;
using Microsoft.Extensions.AI;
using static Demo.Ai.ScriptedChatClient;

namespace Demo.Desktop;

/// <summary>
/// The canned conversation used by <c>--demo</c>: no key, no network, the same every run, so
/// walkthroughs and the automated screenshots are reproducible. The tools still execute for real.
/// </summary>
public static class DemoScript
{
    public const string FirstQuestion = "Which work orders are overdue? Bump the Northwind sensor job to high priority.";
    public const string SecondQuestion = "Which parts should I reorder?";

    public static ScriptedChatClient CreateClient() => new(
    [
        // Turn 1: read (auto-runs), then a write (needs approval), then summarize.
        Call("list_overdue_orders", inputTokens: 1450, outputTokens: 35),
        Call("set_work_order_priority", new Dictionary<string, object?> { ["id"] = "WO-1003", ["priority"] = "High" },
            preamble: "Two orders are overdue: WO-1001 (Replace conveyor belt, 3 days late) and WO-1003 (Recalibrate filling line sensors, 1 day late). I'll raise WO-1003 to High.",
            inputTokens: 1720, outputTokens: 85),
        history => WasDeclined(history)
            ? Reply("OK, I've left WO-1003 at Normal priority. Both WO-1001 and WO-1003 remain overdue.", inputTokens: 1830, outputTokens: 30)(history)
            : Reply("Done. WO-1003 is now High priority. Note that WO-1001 is already High and is 3 days overdue; it may be waiting on BLT-200 conveyor belts, which are below their reorder level.",
                inputTokens: 1830, outputTokens: 60)(history),

        // Turn 2: a pure read.
        Call("list_low_stock_parts", inputTokens: 1910, outputTokens: 30),
        Reply("Two parts are at or below their reorder level:\n• FLT-031 Hydraulic filter: 1 on hand (reorder at 4)\n• BLT-200 Conveyor belt 2m: 2 on hand (reorder at 3)\nWO-1006 (hydraulic press leak, Urgent) will likely need filters, so I'd order FLT-031 first.",
            inputTokens: 2050, outputTokens: 95),
    ])
    {
        ModelId = "demo-script",
        Fallback = Reply("(Demo mode: this scripted assistant only knows the walkthrough questions. Turn off demo mode and add an API key in Settings to chat for real.)"),
    };

    /// <summary>True when the most recent tool result is the "user declined" message produced by approval denial.</summary>
    private static bool WasDeclined(IReadOnlyList<ChatMessage> history) =>
        history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault()?.Result?.ToString()?.Contains("declined", StringComparison.OrdinalIgnoreCase) == true;
}
