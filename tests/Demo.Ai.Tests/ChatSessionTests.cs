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

    private ChatSession Session(ScriptedChatClient model, decimal? cap = null, string priced = "claude-opus-5-5", int maxRequests = 20) =>
        new(model, CapabilityCatalog.FromObject(new TestBenchCapabilities(_bench)), new UsageMeter(priced, cap),
            ChatSessionOptions.For(TestBenchCapabilities.Instructions) with { MaxModelRequestsPerMessage = maxRequests });

    private static List<AIContent> Contents(IEnumerable<ChatMessage> messages) => messages.SelectMany(m => m.Contents).ToList();

    /// <summary>What both providers require: every tool call answered by its result, and no approval left unanswered.</summary>
    private static void AssertSendable(IEnumerable<ChatMessage> messages)
    {
        var contents = Contents(messages);
        var results = contents.OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
        var answers = contents.OfType<ToolApprovalResponseContent>().Select(r => r.RequestId).ToHashSet();
        Assert.All(contents.OfType<FunctionCallContent>(), call => Assert.Contains(call.CallId, results));
        Assert.All(contents.OfType<ToolApprovalRequestContent>(), request => Assert.Contains(request.RequestId, answers));
    }

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
    public async Task Spend_cap_is_checked_before_every_tool_round_trip()
    {
        // The first request costs $0.80 + output; projecting the next one at a similar size would pass $1.
        var model = new ScriptedChatClient([Call("get_bench_info", inputTokens: 200_000, outputTokens: 50), Reply("never sent")]);
        var session = Session(model, cap: 1.00m);

        await Assert.ThrowsAsync<SpendCapExceededException>(() => session.SendAsync("parts?", Answer(true)));

        Assert.Equal(1, model.RemainingSteps);
        Assert.Single(model.Requests);
        Assert.Equal(200_000, session.Meter.InputTokens); // the completed request still counts
    }

    [Fact]
    public async Task Usage_of_completed_requests_is_kept_when_a_later_request_fails()
    {
        var model = new ScriptedChatClient([Call("get_bench_info", inputTokens: 1000, outputTokens: 50), _ => throw new HttpRequestException("outage")]);
        var session = Session(model);

        await Assert.ThrowsAsync<HttpRequestException>(() => session.SendAsync("parts?", Answer(true)));

        Assert.Equal(1000, session.Meter.InputTokens);
        Assert.Equal(50, session.Meter.OutputTokens);
    }

    [Fact]
    public async Task First_request_is_refused_when_even_its_estimate_exceeds_the_cap()
    {
        var model = new ScriptedChatClient([Reply("never sent")]);
        var session = Session(model, cap: 0.01m);

        // ≈ 10,000 input tokens at $4/MTok + the output allowance ≈ $0.06 > $0.01.
        await Assert.ThrowsAsync<SpendCapExceededException>(() => session.SendAsync(new string('x', 40_000), Answer(true)));

        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Unknown_model_prices_report_tokens_only()
    {
        var session = Session(new ScriptedChatClient([Reply("hi", inputTokens: 80_000, outputTokens: 4_000)]), cap: 0.01m, priced: "some-new-model");
        await session.SendAsync("hi", Answer(true));
        Assert.Null(session.Meter.EstimatedCostUsd);
        Assert.Equal(AiSettings.DefaultUnpricedTokenLimit, session.Meter.TokenLimit);
        Assert.Equal("84,000 tokens (limit 500k tokens; no price known for some-new-model)", session.Meter.Summary);
    }

    [Fact]
    public async Task Unknown_model_prices_enforce_a_token_limit_instead()
    {
        var model = new ScriptedChatClient([Reply("big", inputTokens: 499_000, outputTokens: 500), Reply("never sent")]);
        var session = Session(model, cap: 1.00m, priced: "some-new-model");

        await session.SendAsync("first", Answer(true));
        var refused = await Assert.ThrowsAsync<SpendCapExceededException>(() => session.SendAsync("second", Answer(true)));

        Assert.Contains("500,000 tokens", refused.Message);
        Assert.Equal(1, model.RemainingSteps);
    }

    [Fact]
    public void No_limit_means_no_token_limit_either()
    {
        var meter = new UsageMeter("some-new-model", capUsd: null);
        meter.Add(new UsageDetails { InputTokenCount = 10_000_000 });
        meter.EnsureCanAfford([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Null(meter.TokenLimit);
        Assert.Equal("10,000,000 in / 0 out tokens", meter.Summary);
    }

    [Fact]
    public async Task Cancelling_during_approval_leaves_the_chat_usable()
    {
        var model = new ScriptedChatClient(
        [
            Call("start_test_run", new Dictionary<string, object?> { ["planId"] = "TP-1001" }),
            Reply("Hello again."),
        ]);
        var session = Session(model);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync("Run TP-1001", async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return true;
        }, cts.Token));
        Assert.Single(session.History); // just the system prompt: no tool ran, so the unfinished turn was removed

        var turn = await session.SendAsync("hello", Answer(true));

        Assert.Equal("Hello again.", turn.Text);
        Assert.Equal(["hello"], model.Requests[1].Skip(1).Select(m => m.Text));
        Assert.Single(_bench.ListRuns()); // nothing was started
    }

    [Fact]
    public async Task Cancelling_during_approval_keeps_lookups_that_already_ran_but_no_dangling_request()
    {
        var model = new ScriptedChatClient(
        [
            Call("get_bench_info"),
            Call("start_test_run", new Dictionary<string, object?> { ["planId"] = "TP-1001" }),
            Reply("Hello again."),
        ]);
        var session = Session(model);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync("Run TP-1001", async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return true;
        }, cts.Token));

        Assert.Equal("get_bench_info", Assert.Single(Contents(session.History).OfType<FunctionCallContent>()).Name);
        Assert.Empty(Contents(session.History).OfType<ToolApprovalRequestContent>());
        AssertSendable(session.History);

        Assert.Equal("Hello again.", (await session.SendAsync("hello", Answer(true))).Text);
        AssertSendable(model.Requests[^1]);
        Assert.Single(_bench.ListRuns()); // nothing was started
    }

    [Fact]
    public async Task Approved_change_stays_in_the_conversation_when_the_next_request_fails()
    {
        var model = new ScriptedChatClient(
        [
            Call("create_test_plan_from_spec", HotColdFromSpec),
            _ => throw new HttpRequestException("outage"),
            Reply("Yes, TP-1002 is saved."),
        ]);
        var session = Session(model);
        var announced = new List<ChatMessage>();
        session.MessageAdded += announced.Add;

        await Assert.ThrowsAsync<HttpRequestException>(() => session.SendAsync("Make a hot/cold plan from the datasheet", Answer(true)));

        Assert.Equal(["TP-1001", "TP-1002"], _bench.ListPlans().Select(p => p.Id));
        var call = Assert.Single(Contents(session.History).OfType<FunctionCallContent>());
        Assert.Equal("create_test_plan_from_spec", call.Name);
        var result = Assert.Single(Contents(session.History).OfType<FunctionResultContent>());
        Assert.Equal(call.CallId, result.CallId);
        Assert.Contains(announced, m => m.Contents.Contains(result)); // so the UI can show the change as done
        AssertSendable(session.History);

        var turn = await session.SendAsync("Did it save?", Answer(true));

        Assert.Equal("Yes, TP-1002 is saved.", turn.Text);
        Assert.Contains(Contents(model.Requests[^1]).OfType<FunctionResultContent>(), r => r.CallId == call.CallId); // the model still knows
        AssertSendable(model.Requests[^1]);
        Assert.Equal(2, _bench.ListPlans().Count); // and it wasn't run a second time
        Assert.Single(_approvalsAsked);
    }

    [Fact]
    public async Task Approved_change_stays_in_the_conversation_when_the_spending_limit_stops_the_next_request()
    {
        // The first request costs $0.80 + output; projecting the next one at a similar size would pass $1.
        var model = new ScriptedChatClient([Call("create_test_plan_from_spec", HotColdFromSpec, inputTokens: 200_000), Reply("never sent")]);
        var session = Session(model, cap: 1.00m);

        await Assert.ThrowsAsync<SpendCapExceededException>(() => session.SendAsync("Make a hot/cold plan from the datasheet", Answer(true)));

        Assert.Single(model.Requests);
        Assert.Equal(2, _bench.ListPlans().Count);
        Assert.Single(Contents(session.History).OfType<FunctionResultContent>());
        AssertSendable(session.History);
    }

    [Fact]
    public async Task Model_requests_per_message_are_capped_across_approvals()
    {
        // A model that asks for another plan after every one: each approval restarts the per-call tool limit.
        var model = new ScriptedChatClient([]) { Fallback = Call("create_test_plan_from_spec", HotColdFromSpec) };
        var session = Session(model, maxRequests: 3);

        var refused = await Assert.ThrowsAsync<TurnRequestLimitException>(() => session.SendAsync("Make plans", Answer(true)));

        Assert.Contains("after 3 requests", refused.Message);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(3, _approvalsAsked.Count);
        Assert.Equal(4, _bench.ListPlans().Count); // the seeded plan + the three approved ones, each kept in the conversation
        Assert.Equal(3, Contents(session.History).OfType<FunctionResultContent>().Count());
        AssertSendable(session.History);

        // The count starts again with the next message.
        await Assert.ThrowsAsync<TurnRequestLimitException>(() => session.SendAsync("No more plans", Answer(false)));
        Assert.Equal(6, model.Requests.Count);
        Assert.Equal(4, _bench.ListPlans().Count);
    }

    [Fact]
    public async Task Invented_tool_names_are_refused_without_asking()
    {
        // A write next to a made-up tool: the function-invocation layer asks approval for both.
        var model = new ScriptedChatClient(
        [
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call_real", "start_test_run", new Dictionary<string, object?> { ["planId"] = "TP-1001" }),
                new FunctionCallContent("call_fake", "format_bench_disk"),
            ])) { FinishReason = ChatFinishReason.ToolCalls },
            Reply("Done."),
        ]);

        var turn = await Session(model).SendAsync("Run TP-1001", Answer(true));

        Assert.Equal("Done.", turn.Text);
        Assert.Equal("start_test_run", Assert.Single(_approvalsAsked).Capability.Name);
        Assert.Equal(2, _bench.ListRuns().Count);
        var results = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Contains("no tool named 'format_bench_disk'", results.Single(r => r.CallId == "call_fake").Result?.ToString());
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
