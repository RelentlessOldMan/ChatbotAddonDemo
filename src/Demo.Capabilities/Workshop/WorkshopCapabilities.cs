using System.ComponentModel;
using Demo.Domain;

namespace Demo.Capabilities.Workshop;

/// <summary>
/// Capabilities over the PLACEHOLDER domain. Each method is a thin adapter onto
/// <see cref="WorkshopService"/>; descriptions are written for the model, so be
/// specific about formats, units and when to use each one.
/// </summary>
public sealed class WorkshopCapabilities(WorkshopService service)
{
    [Capability("list_work_orders", SafetyClass.Read, Title = "List work orders")]
    [Description("Lists work orders, optionally filtered by status and/or a customer name substring. Returns id, title, customer, status, priority and due date.")]
    public IReadOnlyList<WorkOrder> ListWorkOrders(
        [Description("Only orders with this status. Omit for all statuses.")] OrderStatus? status = null,
        [Description("Case-insensitive substring of the customer name.")] string? customer = null) =>
        service.ListOrders(status, customer);

    [Capability("get_work_order", SafetyClass.Read, Title = "Get a work order")]
    [Description("Gets one work order by id, e.g. 'WO-1003'.")]
    public WorkOrder GetWorkOrder([Description("Work order id, e.g. 'WO-1003'.")] string id) =>
        service.GetOrder(id);

    [Capability("list_overdue_orders", SafetyClass.Read, Title = "List overdue work orders")]
    [Description("Lists work orders that are not completed and whose due date is before today. Also returns today's date.")]
    public object ListOverdueOrders() => new { today = service.Today, overdue = service.ListOverdueOrders() };

    [Capability("list_parts", SafetyClass.Read, Title = "List parts inventory")]
    [Description("Lists all parts with on-hand quantity and reorder level. isLow is true when onHand <= reorderLevel.")]
    public IReadOnlyList<Part> ListParts() => service.ListParts();

    [Capability("list_low_stock_parts", SafetyClass.Read, Title = "List low-stock parts")]
    [Description("Lists parts at or below their reorder level.")]
    public IReadOnlyList<Part> ListLowStockParts() => service.ListLowStockParts();

    [Capability("create_work_order", SafetyClass.Write, Title = "Create a work order")]
    [Description("Creates a new open work order and returns it with its assigned id.")]
    public WorkOrder CreateWorkOrder(
        [Description("Short description of the job.")] string title,
        [Description("Customer name.")] string customer,
        [Description("Due date, yyyy-MM-dd. Must not be in the past.")] DateOnly dueDate,
        [Description("Priority; defaults to Normal.")] Priority priority = Priority.Normal) =>
        service.CreateOrder(title, customer, dueDate, priority);

    [Capability("set_work_order_priority", SafetyClass.Write, Title = "Change work order priority")]
    [Description("Changes the priority of an open or in-progress work order.")]
    public WorkOrder SetWorkOrderPriority(
        [Description("Work order id, e.g. 'WO-1003'.")] string id,
        [Description("New priority.")] Priority priority) =>
        service.SetPriority(id, priority);

    [Capability("complete_work_order", SafetyClass.Write, Title = "Complete a work order")]
    [Description("Marks a work order completed. A short note describing the work done is required.")]
    public WorkOrder CompleteWorkOrder(
        [Description("Work order id, e.g. 'WO-1003'.")] string id,
        [Description("What was done.")] string note) =>
        service.CompleteOrder(id, note);

    [Capability("adjust_stock", SafetyClass.Write, Title = "Adjust part stock")]
    [Description("Adds (positive delta) or removes (negative delta) stock for a part. A reason is required and is recorded.")]
    public Part AdjustStock(
        [Description("Part SKU, e.g. 'FLT-031'.")] string sku,
        [Description("Quantity change; positive to receive, negative to consume.")] int delta,
        [Description("Why, e.g. 'received PO 4471' or 'used on WO-1006'.")] string reason) =>
        service.AdjustStock(sku, delta, reason);
}
