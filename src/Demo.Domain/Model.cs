namespace Demo.Domain;

// PLACEHOLDER DOMAIN: a stand-in "legacy" work-order and parts tool.
// Everything outside this project is domain-agnostic; replace this with a
// domain that mirrors the real product.

public enum OrderStatus { Open, InProgress, Completed }

public enum Priority { Low, Normal, High, Urgent }

public sealed record WorkOrder(
    string Id,
    string Title,
    string Customer,
    OrderStatus Status,
    Priority Priority,
    DateOnly DueDate,
    string? CompletionNote = null);

public sealed record Part(string Sku, string Name, int OnHand, int ReorderLevel)
{
    public bool IsLow => OnHand <= ReorderLevel;
}

public sealed record DataSnapshot(IReadOnlyList<WorkOrder> Orders, IReadOnlyList<Part> Parts);

/// <summary>A business-rule violation. Front ends show the message to the user (or model) as-is.</summary>
public sealed class DomainException(string message) : Exception(message);
