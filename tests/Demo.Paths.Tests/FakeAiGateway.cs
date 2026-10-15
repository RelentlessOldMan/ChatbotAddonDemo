using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Demo.Paths.Tests;

/// <summary>What the model was sent, reduced to what a scripted model needs, whichever provider's format it came in.</summary>
/// <param name="EmptyMessages">Messages with no text, tool call or tool result: a provider may reject them, and they mean nothing.</param>
public sealed record ModelRequest(IReadOnlyList<string> ToolNames, IReadOnlyList<string> ToolResults, string LastUserText, int EmptyMessages);

/// <summary>What the scripted model replies: words, or a request to run one tool.</summary>
public abstract record ModelReply
{
    public sealed record Text(string Value) : ModelReply;
    public sealed record ToolCall(string Name, JsonObject Arguments) : ModelReply;
}

/// <summary>One HTTP request as the gateway received it.</summary>
public sealed record ReceivedRequest(string Path, IReadOnlyDictionary<string, string> Headers, JsonObject Body, ModelRequest Parsed);

/// <summary>
/// A stand-in for a company AI gateway, on 127.0.0.1. It speaks just enough of the Anthropic Messages API and the
/// OpenAI Responses API for the real vendor SDKs to talk to it, records every request, and answers with a
/// scripted model. So the chat panel's whole path (credentials → SDK → HTTP → tool calls → approvals → core
/// logic) runs for real, with nothing leaving the PC and nothing billed.
/// </summary>
public sealed class FakeAiGateway : IAsyncDisposable
{
    public const int InputTokensPerRequest = 1_000;
    public const int OutputTokensPerRequest = 50;

    private readonly WebApplication _app;
    private readonly Func<ModelRequest, ModelReply> _model;
    private int _ids;

    private FakeAiGateway(WebApplication app, Func<ModelRequest, ModelReply> model)
    {
        _app = app;
        _model = model;
    }

    public List<ReceivedRequest> Received { get; } = [];

    public Uri BaseUrl { get; private set; } = null!;

    public static async Task<FakeAiGateway> StartAsync(Func<ModelRequest, ModelReply> model)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var gateway = new FakeAiGateway(app, model);
        app.MapPost("/{**path}", gateway.HandleAsync);
        await app.StartAsync();
        gateway.BaseUrl = new Uri(app.Urls.Single());
        return gateway;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var body = (JsonObject)(await JsonNode.ParseAsync(context.Request.Body))!;
        if (body["stream"]?.GetValue<bool>() == true)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("The fake gateway doesn't stream.");
            return;
        }

        var anthropic = path.EndsWith("/messages", StringComparison.Ordinal);
        var openAi = path.EndsWith("/responses", StringComparison.Ordinal);
        if (!anthropic && !openAi)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var parsed = anthropic ? ParseAnthropic(body) : ParseOpenAi(body);
        var headers = context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
        ModelReply reply;
        lock (Received)
        {
            Received.Add(new ReceivedRequest(path, headers, body, parsed));
            reply = _model(parsed);
        }

        var id = Interlocked.Increment(ref _ids);
        var model = body["model"]?.GetValue<string>() ?? "fake";
        var response = anthropic ? AnthropicResponse(reply, model, id) : OpenAiResponse(reply, model, id);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(response.ToJsonString());
    }

    // ---- Anthropic Messages API --------------------------------------------------------------

    private static ModelRequest ParseAnthropic(JsonObject body)
    {
        var tools = body["tools"]?.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList() ?? [];
        var results = new List<string>();
        var lastUser = "";
        var empty = 0;
        foreach (var message in body["messages"]!.AsArray())
        {
            if (message!["content"] is JsonValue plain) { lastUser = plain.GetValue<string>(); empty += lastUser.Length == 0 ? 1 : 0; continue; }
            if (message["content"]!.AsArray().All(b => b!["type"]!.GetValue<string>() == "text" && TextOf(b["text"]).Length == 0)) empty++;
            foreach (var block in message["content"]!.AsArray())
            {
                switch (block!["type"]!.GetValue<string>())
                {
                    case "tool_result": results.Add(TextOf(block["content"])); break;
                    case "text" when message["role"]!.GetValue<string>() == "user": lastUser = block["text"]!.GetValue<string>(); break;
                }
            }
        }
        return new ModelRequest(tools, results, lastUser, empty);
    }

    private static JsonObject AnthropicResponse(ModelReply reply, string model, int id) => new()
    {
        ["id"] = $"msg_{id}",
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = model,
        ["content"] = new JsonArray(reply switch
        {
            ModelReply.Text t => new JsonObject { ["type"] = "text", ["text"] = t.Value },
            ModelReply.ToolCall c => new JsonObject { ["type"] = "tool_use", ["id"] = $"toolu_{id}", ["name"] = c.Name, ["input"] = c.Arguments.DeepClone() },
            _ => throw new InvalidOperationException(),
        }),
        ["stop_reason"] = reply is ModelReply.ToolCall ? "tool_use" : "end_turn",
        ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = InputTokensPerRequest, ["output_tokens"] = OutputTokensPerRequest },
    };

    // ---- OpenAI Responses API ----------------------------------------------------------------

    private static ModelRequest ParseOpenAi(JsonObject body)
    {
        var tools = body["tools"]?.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList() ?? [];
        var results = new List<string>();
        var lastUser = "";
        var empty = 0;
        foreach (var item in body["input"]!.AsArray())
        {
            var type = item!["type"]?.GetValue<string>();
            if (type == "function_call_output") results.Add(TextOf(item["output"]));
            else if (type is null or "message")
            {
                var text = TextOf(item["content"]);
                if (text.Length == 0) empty++;
                else if (item["role"]?.GetValue<string>() == "user") lastUser = text;
            }
        }
        return new ModelRequest(tools, results, lastUser, empty);
    }

    private static JsonObject OpenAiResponse(ModelReply reply, string model, int id) => new()
    {
        ["id"] = $"resp_{id}",
        ["object"] = "response",
        ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["status"] = "completed",
        ["model"] = model,
        ["output"] = new JsonArray(reply switch
        {
            ModelReply.Text t => new JsonObject
            {
                ["type"] = "message",
                ["id"] = $"msg_{id}",
                ["role"] = "assistant",
                ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = t.Value, ["annotations"] = new JsonArray() }),
            },
            ModelReply.ToolCall c => new JsonObject
            {
                ["type"] = "function_call",
                ["id"] = $"fc_{id}",
                ["call_id"] = $"call_{id}",
                ["name"] = c.Name,
                ["arguments"] = c.Arguments.ToJsonString(),
                ["status"] = "completed",
            },
            _ => throw new InvalidOperationException(),
        }),
        ["parallel_tool_calls"] = true,
        ["tool_choice"] = "auto",
        ["tools"] = new JsonArray(),
        ["usage"] = new JsonObject
        {
            ["input_tokens"] = InputTokensPerRequest,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
            ["output_tokens"] = OutputTokensPerRequest,
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
            ["total_tokens"] = InputTokensPerRequest + OutputTokensPerRequest,
        },
    };

    /// <summary>A content field that is either a string or an array of text parts.</summary>
    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue v => v.GetValue<string>(),
        JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
        _ => "",
    };

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
