using Microsoft.Extensions.AI;

namespace Demo.Ai;

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok);

/// <summary>
/// Estimated list prices. Customers may have negotiated rates, so this is an
/// estimate and the UI says so. Unknown models show token counts only.
/// </summary>
public static class PriceTable
{
    public static readonly IReadOnlyDictionary<string, ModelPrice> Default = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-5-5"] = new(4.00m, 20.00m),
        ["claude-sonnet-5-5"] = new(2.00m, 10.00m),
        ["claude-haiku-4-5"] = new(1.00m, 5.00m),
        // OpenAI prices intentionally omitted until verified; add your rates here.
    };
}

public sealed class SpendCapExceededException : Exception
{
    public SpendCapExceededException(decimal capUsd)
        : base($"This chat has reached its spending limit of ${capUsd:0.00}, so the next request was not sent. Start a new chat or raise the limit in Settings.") { }

    public SpendCapExceededException(long tokenLimit, string model)
        : base($"This chat has reached its limit of {tokenLimit:N0} tokens (no price is known for {model}, so tokens are limited instead of dollars), so the next request was not sent. Start a new chat or raise the limit in Settings.") { }
}

/// <summary>
/// Accumulates token usage and estimated cost for one chat session, and enforces its limit:
/// a dollar limit when the model's price is known, otherwise a token limit.
/// </summary>
public sealed class UsageMeter(string model, decimal? capUsd, IReadOnlyDictionary<string, ModelPrice>? prices = null, long tokenLimitWhenUnpriced = AiSettings.DefaultUnpricedTokenLimit)
{
    /// <summary>Output allowance assumed for a request that hasn't happened yet. Most replies and tool calls are far shorter.</summary>
    public const int ProjectedOutputTokens = 1_000;

    private readonly ModelPrice? _price = (prices ?? PriceTable.Default).GetValueOrDefault(model);
    private long _lastRequestInputTokens;

    public string Model => model;
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long TotalTokens => InputTokens + OutputTokens;
    public int Requests { get; private set; }

    /// <summary>Null when the model's price is unknown.</summary>
    public decimal? EstimatedCostUsd => CostOf(InputTokens, OutputTokens);

    public decimal? CapUsd => capUsd;

    /// <summary>The token limit in force instead of <see cref="CapUsd"/> when the price is unknown; null otherwise.</summary>
    public long? TokenLimit => capUsd is not null && _price is null ? tokenLimitWhenUnpriced : null;

    public event EventHandler? Changed;

    public void Add(UsageDetails? usage)
    {
        Requests++;
        var input = usage?.InputTokenCount ?? 0;
        InputTokens += input;
        OutputTokens += usage?.OutputTokenCount ?? 0;
        if (input > 0) _lastRequestInputTokens = input;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called before every model request. Throws <see cref="SpendCapExceededException"/> when what has been spent
    /// so far plus a projection for this request would go over the limit, so that request is never sent.
    /// The projection is an estimate, not a quote: input ≈ the larger of the previous request's input tokens
    /// (each request resends the conversation, so it rarely shrinks) and a rough characters/4 count of the
    /// messages; output ≈ <see cref="ProjectedOutputTokens"/>. A long reply can still end slightly over the limit.
    /// </summary>
    public void EnsureCanAfford(IEnumerable<ChatMessage> messages)
    {
        if (capUsd is not { } cap) return;
        var projectedInput = Math.Max(_lastRequestInputTokens, EstimateTokens(messages));

        if (CostOf(InputTokens + projectedInput, OutputTokens + ProjectedOutputTokens) is { } projectedCost)
        {
            if (projectedCost > cap) throw new SpendCapExceededException(cap);
        }
        else if (TotalTokens + projectedInput + ProjectedOutputTokens > tokenLimitWhenUnpriced)
        {
            throw new SpendCapExceededException(tokenLimitWhenUnpriced, model);
        }
    }

    public string Summary =>
        TokenLimit is { } limit
            ? $"{TotalTokens:N0} tokens (limit {Compact(limit)} tokens; no price known for {model})"
            : $"{InputTokens:N0} in / {OutputTokens:N0} out tokens" +
              (EstimatedCostUsd is { } c ? $" · ≈ ${c:0.000}" : "") +
              (capUsd is { } cap ? $" (limit ${cap:0.00})" : "");

    private decimal? CostOf(long input, long output) =>
        _price is null ? null : (input * _price.InputPerMTok + output * _price.OutputPerMTok) / 1_000_000m;

    /// <summary>Rough token count (≈ 4 characters per token) of the text and tool traffic in the messages.</summary>
    private static long EstimateTokens(IEnumerable<ChatMessage> messages)
    {
        long chars = 0;
        foreach (var content in messages.SelectMany(m => m.Contents))
        {
            chars += content switch
            {
                TextContent t => t.Text.Length,
                FunctionCallContent call => call.Name.Length + (call.Arguments?.Sum(a => a.Key.Length + (a.Value?.ToString()?.Length ?? 0)) ?? 0),
                FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                _ => 0,
            };
        }
        return chars / 4;
    }

    private static string Compact(long tokens) =>
        tokens >= 1_000 && tokens % 1_000 == 0 ? $"{tokens / 1_000:N0}k" : $"{tokens:N0}";
}
