using Demo.Ai;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;
using Microsoft.Extensions.AI;
using static Demo.Ai.ScriptedChatClient;

namespace Demo.Ai.Tests;

public sealed class ChatSessionTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "chat-tests", Guid.NewGuid().ToString("N"));
    private readonly TestBenchService _bench;
    private readonly List<ApprovalRequest> _approvalsAsked = [];

    public ChatSessionTests() => _bench = new TestBenchService(_workspace, BenchOptions.Instant);

    private ChatSession Session(ScriptedChatClient model, decimal? cap = null, string priced = "claude-opus-5-5") =>
        new(model, CapabilityCatalog.FromObject(new TestBenchCapabilities(_bench)), new UsageMeter(priced, cap),
            ChatSessionOptions.For(TestBenchCapabilities.Instructions));

    private static readonly Dictionary<string, object?> HotColdFromSpec = new()
    {
        ["specFile"] = "LDO-3300_datasheet.csv",
        ["samples"] = 3,
        ["temperaturesC"] = new[] { -40, 25, 85, 125 },
    };

    private ApprovalHandler Answer(bool approve) => (request, _) =>
    {
        _approvalsAsked.Add(request);
        return Task.FromResult(approve);
    };

    [Fact]
    public async Task Read_tools_run_without_approval_and_results_reach_the_model()
    {
        var model = new ScriptedChatClient([Call("list_test_plans"), Reply("There is one plan, TP-1001.")]);

        var turn = await Session(model).SendAsync("What plans are there?", Answer(true));

        Assert.Empty(_approvalsAsked);
        Assert.Equal("There is one plan, TP-1001.", turn.Text);
        var toolResult = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("LDO-3300 room-temperature check", System.Text.Json.JsonSerializer.Serialize(toolResult.Result));
    }

    [Fact]
    public async Task Write_tools_ask_for_approval_and_run_when_approved()
    {
        var model = new ScriptedChatClient([Call("create_test_plan_from_spec", HotColdFromSpec), Reply("Saved as TP-1002.")]);

        await Session(model).SendAsync("Make a hot/cold plan from the datasheet", Answer(true));

        var asked = Assert.Single(_approvalsAsked);
        Assert.Equal("create_test_plan_from_spec", asked.Capability.Name);
        Assert.Equal(SafetyClass.Write, asked.Capability.Safety);
        Assert.Equal("Create a test plan from a spec sheet", asked.Capability.Title);
        Assert.Contains("LDO-3300_datasheet.csv", asked.ArgumentsJson);
        Assert.Equal(["TP-1001", "TP-1002"], _bench.ListPlans().Select(p => p.Id));
    }

    [Fact]
    public async Task Bench_actions_ask_for_approval_too()
    {
        var model = new ScriptedChatClient([Call("start_test_run", new Dictionary<string, object?> { ["planId"] = "TP-1001" }), Reply("Started.")]);

        await Session(model).SendAsync("Run TP-1001", Answer(true));

        Assert.Equal(SafetyClass.Execute, Assert.Single(_approvalsAsked).Capability.Safety);
        Assert.Equal(2, _bench.ListRuns().Count);
    }

    [Fact]
    public async Task Denied_writes_do_not_run_and_the_model_is_told()
    {
        var model = new ScriptedChatClient(
        [
            Call("start_test_run", new Dictionary<string, object?> { ["planId"] = "TP-1001" }),
            Reply("OK, I won't start it."),
        ]);

        await Session(model).SendAsync("Run TP-1001", Answer(false));

        Assert.Single(_approvalsAsked);
        Assert.Single(_bench.ListRuns()); // only the seeded run
        var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("declined", result.Result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Usage_is_metered_across_tool_round_trips()
    {
        var model = new ScriptedChatClient([Call("get_bench_info", inputTokens: 1000, outputTokens: 50), Reply("ok", inputTokens: 2000, outputTokens: 100)]);
        var session = Session(model);

        await session.SendAsync("parts?", Answer(true));

        Assert.Equal(3000, session.Meter.InputTokens);
        Assert.Equal(150, session.Meter.OutputTokens);
        Assert.Equal((3000 * 4m + 150 * 20m) / 1_000_000m, session.Meter.EstimatedCostUsd);
    }

    [Fact]
    public async Task Spend_cap_blocks_further_messages()
    {
        var model = new ScriptedChatClient([Reply("expensive", inputTokens: 1_000_000, outputTokens: 0), Reply("never sent")]);
        var session = Session(model, cap: 1.00m);

        await session.SendAsync("first", Answer(true));

        await Assert.ThrowsAsync<SpendCapExceededException>(() => session.SendAsync("second", Answer(true)));
        Assert.Equal(1, model.RemainingSteps);
    }

    [Fact]
    public async Task Unknown_model_prices_report_tokens_only()
    {
        var session = Session(new ScriptedChatClient([Reply("hi")]), cap: 0.01m, priced: "some-new-model");
        await session.SendAsync("hi", Answer(true));
        Assert.Null(session.Meter.EstimatedCostUsd);
        Assert.DoesNotContain("$0.0", session.Meter.Summary.Split('(')[0]);
    }

    [Fact]
    public async Task Domain_errors_reach_the_model_as_readable_results()
    {
        var model = new ScriptedChatClient(
        [
            Call("generate_report", new Dictionary<string, object?> { ["runId"] = "R-9999" }),
            Reply("That run doesn't exist."),
        ]);

        await Session(model).SendAsync("report R-9999", Answer(true));

        var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("does not exist", System.Text.Json.JsonSerializer.Serialize(result.Result));
    }

    [Fact]
    public async Task System_prompt_and_tools_are_sent_and_history_accumulates()
    {
        var model = new ScriptedChatClient([Reply("one"), Reply("two")]);
        var session = Session(model);

        await session.SendAsync("a", Answer(true));
        await session.SendAsync("b", Answer(true));

        var second = model.Requests[1];
        Assert.Equal(ChatRole.System, second[0].Role);
        Assert.Contains("do not use Markdown", second[0].Text);      // generic rules from Demo.Ai
        Assert.Contains("Call get_bench_info before", second[0].Text); // domain instructions from the host app
        Assert.Equal(["a", "one", "b"], second.Skip(1).Select(m => m.Text));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
