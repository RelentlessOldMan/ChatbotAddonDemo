using Demo.Ai;
using Demo.Capabilities;
using Demo.Capabilities.Workshop;
using Demo.Domain;
using Microsoft.Extensions.AI;
using static Demo.Ai.ScriptedChatClient;

namespace Demo.Ai.Tests;

public class ChatSessionTests
{
    private readonly WorkshopService _service = new(new InMemoryDataStore(SeedData.Create(DateOnly.FromDateTime(DateTime.Today))));
    private readonly List<ApprovalRequest> _approvalsAsked = [];

    private ChatSession Session(ScriptedChatClient model, decimal? cap = null, string priced = "claude-opus-5-5") =>
        new(model, CapabilityCatalog.FromObject(new WorkshopCapabilities(_service)), new UsageMeter(priced, cap));

    private ApprovalHandler Answer(bool approve) => (request, _) =>
    {
        _approvalsAsked.Add(request);
        return Task.FromResult(approve);
    };

    [Fact]
    public async Task Read_tools_run_without_approval_and_results_reach_the_model()
    {
        var model = new ScriptedChatClient([Call("list_overdue_orders"), Reply("WO-1001 and WO-1003 are overdue.")]);

        var turn = await Session(model).SendAsync("What's overdue?", Answer(true));

        Assert.Empty(_approvalsAsked);
        Assert.Equal("WO-1001 and WO-1003 are overdue.", turn.Text);
        var toolResult = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("WO-1003", System.Text.Json.JsonSerializer.Serialize(toolResult.Result));
    }

    [Fact]
    public async Task Write_tools_ask_for_approval_and_run_when_approved()
    {
        var model = new ScriptedChatClient(
        [
            Call("set_work_order_priority", new Dictionary<string, object?> { ["id"] = "WO-1003", ["priority"] = "High" }),
            Reply("Done — WO-1003 is now High priority."),
        ]);

        await Session(model).SendAsync("Bump WO-1003 to high", Answer(true));

        var asked = Assert.Single(_approvalsAsked);
        Assert.Equal("set_work_order_priority", asked.Capability.Name);
        Assert.Equal("Change work order priority", asked.Capability.Title);
        Assert.Contains("\"WO-1003\"", asked.ArgumentsJson);
        Assert.Equal(Priority.High, _service.GetOrder("WO-1003").Priority);
    }

    [Fact]
    public async Task Denied_writes_do_not_run_and_the_model_is_told()
    {
        var model = new ScriptedChatClient(
        [
            Call("adjust_stock", new Dictionary<string, object?> { ["sku"] = "FLT-031", ["delta"] = 10, ["reason"] = "PO" }),
            Reply("OK, I won't change the stock."),
        ]);

        await Session(model).SendAsync("Receive 10 filters", Answer(false));

        Assert.Single(_approvalsAsked);
        Assert.Equal(1, _service.ListParts().Single(p => p.Sku == "FLT-031").OnHand);
        var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("declined", result.Result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Usage_is_metered_across_tool_round_trips()
    {
        var model = new ScriptedChatClient([Call("list_parts", inputTokens: 1000, outputTokens: 50), Reply("ok", inputTokens: 2000, outputTokens: 100)]);
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
            Call("complete_work_order", new Dictionary<string, object?> { ["id"] = "WO-1005", ["note"] = "x" }),
            Reply("It was already completed."),
        ]);

        await Session(model).SendAsync("complete WO-1005", Answer(true));

        var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("already completed", System.Text.Json.JsonSerializer.Serialize(result.Result));
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
        Assert.Equal(["a", "one", "b"], second.Skip(1).Select(m => m.Text));
    }
}
