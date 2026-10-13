namespace Demo.Domain;

public static class SeedData
{
    /// <summary>Deterministic demo data, with due dates relative to <paramref name="today"/>.</summary>
    public static DataSnapshot Create(DateOnly today) => new(
        [
            new("WO-1001", "Replace conveyor belt", "Northwind Foods", OrderStatus.InProgress, Priority.High, today.AddDays(-3)),
            new("WO-1002", "Annual boiler inspection", "Contoso Plastics", OrderStatus.Open, Priority.Normal, today.AddDays(5)),
            new("WO-1003", "Recalibrate filling line sensors", "Northwind Foods", OrderStatus.Open, Priority.Normal, today.AddDays(-1)),
            new("WO-1004", "Install backup compressor", "Fabrikam Metals", OrderStatus.Open, Priority.Low, today.AddDays(14)),
            new("WO-1005", "Fix loading dock door", "Contoso Plastics", OrderStatus.Completed, Priority.Normal, today.AddDays(-10), "Replaced spring assembly."),
            new("WO-1006", "Hydraulic press leak", "Fabrikam Metals", OrderStatus.InProgress, Priority.Urgent, today.AddDays(1)),
        ],
        [
            new("BLT-200", "Conveyor belt 2m", 2, 3),
            new("SNS-110", "Proximity sensor", 14, 5),
            new("FLT-031", "Hydraulic filter", 1, 4),
            new("VLV-450", "Pressure relief valve", 9, 2),
            new("SPR-077", "Door torsion spring", 6, 2),
        ]);
}
