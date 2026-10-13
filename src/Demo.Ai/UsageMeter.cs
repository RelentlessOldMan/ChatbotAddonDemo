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

public sealed class SpendCapExceededException(decimal capUsd)
    : Exception($"This chat has reached its spending limit of ${capUsd:0.00}. Start a new chat or raise the limit in Settings.");

/// <summary>Accumulates token usage and estimated cost for one chat session.</summary>
public sealed class UsageMeter(string model, decimal? capUsd, IReadOnlyDictionary<string, ModelPrice>? prices = null)
{
    private readonly ModelPrice? _price = (prices ?? PriceTable.Default).GetValueOrDefault(model);

    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public int Requests { get; private set; }

    /// <summary>Null when the model's price is unknown.</summary>
    public decimal? EstimatedCostUsd =>
        _price is null ? null : (InputTokens * _price.InputPerMTok + OutputTokens * _price.OutputPerMTok) / 1_000_000m;

    public decimal? CapUsd => capUsd;

    public event EventHandler? Changed;

    public void Add(UsageDetails? usage)
    {
        Requests++;
        InputTokens += usage?.InputTokenCount ?? 0;
        OutputTokens += usage?.OutputTokenCount ?? 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void EnsureUnderCap()
    {
        if (capUsd is { } cap && EstimatedCostUsd is { } cost && cost >= cap)
            throw new SpendCapExceededException(cap);
    }

    public string Summary =>
        $"{InputTokens:N0} in / {OutputTokens:N0} out tokens" +
        (EstimatedCostUsd is { } c ? $" · ≈ ${c:0.000}" : "") +
        (capUsd is { } cap ? $" (limit ${cap:0.00})" : "");
}
