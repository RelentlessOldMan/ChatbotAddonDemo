using System.Text.Json;
using Demo.Capabilities;
using Demo.Capabilities.Workshop;
using Demo.Domain;
using Microsoft.Extensions.AI;

namespace Demo.Capabilities.Tests;

public class CapabilityCatalogTests
{
    private static (CapabilityCatalog Catalog, WorkshopService Service) Create()
    {
        var service = new WorkshopService(new InMemoryDataStore(SeedData.Create(DateOnly.FromDateTime(DateTime.Today))));
        return (CapabilityCatalog.FromObject(new WorkshopCapabilities(service)), service);
    }

    [Fact]
    public void Catalog_exposes_expected_capabilities_with_safety_classes()
    {
        var (catalog, _) = Create();
        var actual = catalog.Capabilities.ToDictionary(c => c.Name, c => c.Safety);

        Assert.Equal(new Dictionary<string, SafetyClass>
        {
            ["list_work_orders"] = SafetyClass.Read,
            ["get_work_order"] = SafetyClass.Read,
            ["list_overdue_orders"] = SafetyClass.Read,
            ["list_parts"] = SafetyClass.Read,
            ["list_low_stock_parts"] = SafetyClass.Read,
            ["create_work_order"] = SafetyClass.Write,
            ["set_work_order_priority"] = SafetyClass.Write,
            ["complete_work_order"] = SafetyClass.Write,
            ["adjust_stock"] = SafetyClass.Write,
        }, actual);
    }

    [Fact]
    public void Every_capability_has_a_description_and_title()
    {
        var (catalog, _) = Create();
        Assert.All(catalog.Capabilities, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
            Assert.NotEqual(c.Name, c.Title);
        });
    }

    [Fact]
    public void Schema_uses_snake_case_names_descriptions_and_enum_names()
    {
        var (catalog, _) = Create();
        var schema = catalog["set_work_order_priority"].Function.JsonSchema;
        var properties = schema.GetProperty("properties");

        Assert.Equal("Work order id, e.g. 'WO-1003'.", properties.GetProperty("id").GetProperty("description").GetString());
        var priorities = properties.GetProperty("priority").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(["Low", "Normal", "High", "Urgent"], priorities);
        Assert.Equal(["id", "priority"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Write_capability_changes_data_through_domain_service()
    {
        var (catalog, service) = Create();
        await catalog["set_work_order_priority"].Function.InvokeAsync(new AIFunctionArguments
        {
            ["id"] = "WO-1003",
            ["priority"] = "Urgent",
        });
        Assert.Equal(Priority.Urgent, service.GetOrder("WO-1003").Priority);
    }

    [Fact]
    public async Task Domain_errors_come_back_as_error_results_not_exceptions()
    {
        var (catalog, _) = Create();
        var result = await catalog["adjust_stock"].Function.InvokeAsync(new AIFunctionArguments
        {
            ["sku"] = "FLT-031",
            ["delta"] = -50,
            ["reason"] = "test",
        });

        var json = JsonSerializer.SerializeToElement(result, CapabilityCatalog.SerializerOptions);
        Assert.Contains("only 1 on hand", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Read_capabilities_never_change_data()
    {
        var (catalog, service) = Create();
        var before = JsonSerializer.Serialize(service.Snapshot);
        foreach (var capability in catalog.Capabilities.Where(c => c.Safety == SafetyClass.Read))
        {
            var args = new AIFunctionArguments();
            if (capability.Name == "get_work_order") args["id"] = "WO-1001";
            await capability.Function.InvokeAsync(args);
        }
        Assert.Equal(before, JsonSerializer.Serialize(service.Snapshot));
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var (catalog, _) = Create();
        Assert.Throws<InvalidOperationException>(() => catalog.Concat(catalog));
    }

    [Fact]
    public void Missing_description_is_a_startup_error()
    {
        Assert.Throws<InvalidOperationException>(() => CapabilityCatalog.FromObject(new Undocumented()));
    }

    private sealed class Undocumented
    {
        [Capability("oops", SafetyClass.Read)]
        public int Oops() => 1;
    }
}
