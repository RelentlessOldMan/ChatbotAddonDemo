using Demo.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Demo.Domain.Tests;

public class WorkshopServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);

    private static WorkshopService Create(out InMemoryDataStore store)
    {
        store = new InMemoryDataStore(SeedData.Create(Today));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return new WorkshopService(store, time);
    }

    [Fact]
    public void Overdue_excludes_completed_and_future_orders()
    {
        var service = Create(out _);
        Assert.Equal(["WO-1001", "WO-1003"], service.ListOverdueOrders().Select(o => o.Id));
    }

    [Fact]
    public void ListOrders_filters_by_status_and_customer_substring()
    {
        var service = Create(out _);
        Assert.Equal(["WO-1003"], service.ListOrders(OrderStatus.Open, "northwind").Select(o => o.Id));
    }

    [Fact]
    public void CreateOrder_assigns_next_id_persists_and_raises_Changed()
    {
        var service = Create(out var store);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        var order = service.CreateOrder("New job", "Acme", Today.AddDays(2), Priority.High);

        Assert.Equal("WO-1007", order.Id);
        Assert.Contains(store.Load().Orders, o => o.Id == "WO-1007");
        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData("", "Acme", 1, "Title is required.")]
    [InlineData("Job", " ", 1, "Customer is required.")]
    [InlineData("Job", "Acme", -1, "Due date cannot be in the past.")]
    public void CreateOrder_validates(string title, string customer, int dueOffset, string message)
    {
        var service = Create(out _);
        var ex = Assert.Throws<DomainException>(() => service.CreateOrder(title, customer, Today.AddDays(dueOffset)));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Completed_orders_cannot_be_reprioritized_or_recompleted()
    {
        var service = Create(out _);
        Assert.Throws<DomainException>(() => service.SetPriority("WO-1005", Priority.Urgent));
        Assert.Throws<DomainException>(() => service.CompleteOrder("WO-1005", "again"));
    }

    [Fact]
    public void CompleteOrder_requires_note_and_ids_are_case_insensitive()
    {
        var service = Create(out _);
        Assert.Throws<DomainException>(() => service.CompleteOrder("WO-1002", " "));
        var done = service.CompleteOrder("wo-1002", "Inspected, all good.");
        Assert.Equal(OrderStatus.Completed, done.Status);
    }

    [Fact]
    public void AdjustStock_rejects_going_negative_and_leaves_data_unchanged()
    {
        var service = Create(out var store);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        var ex = Assert.Throws<DomainException>(() => service.AdjustStock("FLT-031", -5, "used"));

        Assert.Contains("only 1 on hand", ex.Message);
        Assert.Equal(1, store.Load().Parts.Single(p => p.Sku == "FLT-031").OnHand);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void AdjustStock_updates_low_stock_status()
    {
        var service = Create(out _);
        service.AdjustStock("FLT-031", 10, "received PO 4471");
        Assert.DoesNotContain(service.ListLowStockParts(), p => p.Sku == "FLT-031");
    }

    [Fact]
    public void Unknown_ids_produce_domain_errors()
    {
        var service = Create(out _);
        Assert.Equal("Work order 'WO-9' does not exist.", Assert.Throws<DomainException>(() => service.GetOrder("WO-9")).Message);
        Assert.Throws<DomainException>(() => service.AdjustStock("NOPE", 1, "x"));
    }

    [Fact]
    public void JsonFileDataStore_round_trips_and_seeds_when_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"demo-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFileDataStore(path);
            var seeded = store.Load();
            Assert.Equal(6, seeded.Orders.Count);

            new WorkshopService(store).AdjustStock("SNS-110", -4, "used");

            Assert.Equal(10, new JsonFileDataStore(path).Load().Parts.Single(p => p.Sku == "SNS-110").OnHand);
            Assert.Contains("\"InProgress\"", File.ReadAllText(path)); // enums as names: the file stays human-readable
        }
        finally
        {
            File.Delete(path);
        }
    }
}
