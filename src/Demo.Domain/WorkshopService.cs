namespace Demo.Domain;

/// <summary>
/// The legacy tool's business logic. UI, console commands, chat and MCP all go
/// through here, so validation is enforced no matter who is calling.
/// Thread-safe: MCP requests arrive on thread-pool threads while the UI reads.
/// </summary>
public sealed class WorkshopService
{
    private readonly IDataStore _store;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DataSnapshot _data;

    public WorkshopService(IDataStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        _data = store.Load();
    }

    /// <summary>Raised after any successful change, on the calling thread.</summary>
    public event EventHandler? Changed;

    public DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().Date);

    public DataSnapshot Snapshot { get { lock (_gate) return _data; } }

    public IReadOnlyList<WorkOrder> ListOrders(OrderStatus? status = null, string? customer = null) =>
        Snapshot.Orders
            .Where(o => status is null || o.Status == status)
            .Where(o => customer is null || o.Customer.Contains(customer, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public WorkOrder GetOrder(string id) =>
        Find(Snapshot.Orders, id) ?? throw new DomainException($"Work order '{id}' does not exist.");

    public IReadOnlyList<WorkOrder> ListOverdueOrders()
    {
        var today = Today;
        return Snapshot.Orders.Where(o => o.Status != OrderStatus.Completed && o.DueDate < today).ToList();
    }

    public IReadOnlyList<Part> ListParts() => Snapshot.Parts;

    public IReadOnlyList<Part> ListLowStockParts() => Snapshot.Parts.Where(p => p.IsLow).ToList();

    public WorkOrder CreateOrder(string title, string customer, DateOnly dueDate, Priority priority = Priority.Normal)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Title is required.");
        if (string.IsNullOrWhiteSpace(customer)) throw new DomainException("Customer is required.");
        if (dueDate < Today) throw new DomainException("Due date cannot be in the past.");

        return Mutate(data =>
        {
            var next = data.Orders.Select(o => int.Parse(o.Id.AsSpan(3))).DefaultIfEmpty(1000).Max() + 1;
            var order = new WorkOrder($"WO-{next}", title.Trim(), customer.Trim(), OrderStatus.Open, priority, dueDate);
            return (data with { Orders = [.. data.Orders, order] }, order);
        });
    }

    public WorkOrder SetPriority(string id, Priority priority) =>
        UpdateOrder(id, o =>
        {
            if (o.Status == OrderStatus.Completed) throw new DomainException($"{o.Id} is completed; its priority cannot change.");
            return o with { Priority = priority };
        });

    public WorkOrder CompleteOrder(string id, string note) =>
        UpdateOrder(id, o =>
        {
            if (o.Status == OrderStatus.Completed) throw new DomainException($"{o.Id} is already completed.");
            if (string.IsNullOrWhiteSpace(note)) throw new DomainException("A completion note is required.");
            return o with { Status = OrderStatus.Completed, CompletionNote = note.Trim() };
        });

    public Part AdjustStock(string sku, int delta, string reason)
    {
        if (delta == 0) throw new DomainException("Adjustment must be non-zero.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required for stock adjustments.");

        return Mutate(data =>
        {
            var part = data.Parts.FirstOrDefault(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainException($"Part '{sku}' does not exist.");
            if (part.OnHand + delta < 0)
                throw new DomainException($"Cannot remove {-delta} of {part.Sku}; only {part.OnHand} on hand.");
            var updated = part with { OnHand = part.OnHand + delta };
            return (data with { Parts = data.Parts.Select(p => p == part ? updated : p).ToList() }, updated);
        });
    }

    private static WorkOrder? Find(IEnumerable<WorkOrder> orders, string id) =>
        orders.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    private WorkOrder UpdateOrder(string id, Func<WorkOrder, WorkOrder> change) =>
        Mutate(data =>
        {
            var existing = Find(data.Orders, id) ?? throw new DomainException($"Work order '{id}' does not exist.");
            var updated = change(existing);
            return (data with { Orders = data.Orders.Select(o => o == existing ? updated : o).ToList() }, updated);
        });

    private T Mutate<T>(Func<DataSnapshot, (DataSnapshot Data, T Result)> change)
    {
        T result;
        lock (_gate)
        {
            (var data, result) = change(_data);
            _store.Save(data);
            _data = data;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }
}
