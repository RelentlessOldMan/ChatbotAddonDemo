using System.Globalization;
using System.Reflection;
using Demo.Capabilities;
using Demo.Capabilities.Workshop;
using Demo.Domain;
using Demo.Mcp;
using ModelContextProtocol.Protocol;

// demo-cli: the "legacy" batch/console tool. Ordinary commands work exactly as before;
// `mcp` turns the same process into an MCP server for coding agents (docs/adr/004-mcp-transports.md).

var argList = args.ToList();
var dataPath = TakeOption(argList, "--data")
    ?? Environment.GetEnvironmentVariable("DEMO_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatbotAddonDemo", "workshop.json");

if (argList.Count == 0 || argList[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var service = new WorkshopService(new JsonFileDataStore(dataPath));

try
{
    switch (argList[0])
    {
        case "orders":
            var status = TakeOption(argList, "--status") is { } s ? Enum.Parse<OrderStatus>(s, ignoreCase: true) : (OrderStatus?)null;
            PrintOrders(service.ListOrders(status, TakeOption(argList, "--customer")));
            return 0;

        case "overdue":
            Console.WriteLine($"Overdue as of {service.Today:yyyy-MM-dd}:");
            PrintOrders(service.ListOverdueOrders());
            return 0;

        case "parts":
            PrintParts(TakeFlag(argList, "--low") ? service.ListLowStockParts() : service.ListParts());
            return 0;

        case "complete" when argList.Count >= 3:
            var done = service.CompleteOrder(argList[1], string.Join(' ', argList.Skip(2)));
            Console.WriteLine($"{done.Id} completed.");
            return 0;

        case "adjust" when argList.Count >= 4:
            var part = service.AdjustStock(argList[1], int.Parse(argList[2], CultureInfo.InvariantCulture), string.Join(' ', argList.Skip(3)));
            Console.WriteLine($"{part.Sku} on hand: {part.OnHand}");
            return 0;

        case "import" when argList.Count == 2:
            return Import(service, argList[1]);

        case "report":
            var outPath = TakeOption(argList, "--out") ?? "orders-report.csv";
            WriteReport(service, outPath);
            Console.WriteLine($"Wrote {outPath}");
            return 0;

        case "mcp":
            var policy = new McpExposurePolicy(AllowWrites: TakeFlag(argList, "--allow-writes"));
            var catalog = CapabilityCatalog.FromObject(new WorkshopCapabilities(service));
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            await StdioMcpHost.RunAsync(catalog, policy, new Implementation { Name = McpClientConfig.CliServerName, Version = version });
            return 0;

        default:
            PrintHelp();
            return 2;
    }
}
catch (DomainException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void PrintHelp() => Console.WriteLine(
    """
    demo-cli — Workshop batch tool

    Usage: demo-cli [--data <file.json>] <command>

      orders [--status Open|InProgress|Completed] [--customer <text>]
      overdue                          List overdue work orders
      parts [--low]                    List parts (or only low stock)
      complete <WO-id> <note...>       Complete a work order
      adjust <sku> <delta> <reason...> Adjust stock
      import <file.csv>                Create orders from CSV: title,customer,yyyy-MM-dd,priority
      report [--out <file.csv>]        Export all orders to CSV

      mcp [--allow-writes]             Run as an MCP server on stdio for coding agents
                                       (read-only unless --allow-writes)
    """);

static void PrintOrders(IEnumerable<WorkOrder> orders)
{
    Console.WriteLine($"{"ID",-8} {"STATUS",-11} {"PRIORITY",-8} {"DUE",-10}  {"CUSTOMER",-17} TITLE");
    foreach (var o in orders)
        Console.WriteLine($"{o.Id,-8} {o.Status,-11} {o.Priority,-8} {o.DueDate:yyyy-MM-dd}  {o.Customer,-17} {o.Title}");
}

static void PrintParts(IEnumerable<Part> parts)
{
    Console.WriteLine($"{"SKU",-8} {"ON HAND",7} {"REORDER",7}  NAME");
    foreach (var p in parts)
        Console.WriteLine($"{p.Sku,-8} {p.OnHand,7} {p.ReorderLevel,7}  {p.Name}{(p.IsLow ? "  [LOW]" : "")}");
}

static int Import(WorkshopService service, string path)
{
    var failures = 0;
    foreach (var (line, number) in File.ReadLines(path).Select((l, i) => (l, i + 1)).Skip(1))
    {
        var f = line.Split(',');
        try
        {
            var order = service.CreateOrder(f[0], f[1], DateOnly.ParseExact(f[2], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                f.Length > 3 ? Enum.Parse<Priority>(f[3], ignoreCase: true) : Priority.Normal);
            Console.WriteLine($"line {number}: created {order.Id}");
        }
        catch (Exception ex) when (ex is DomainException or FormatException or IndexOutOfRangeException or ArgumentException)
        {
            Console.Error.WriteLine($"line {number}: {ex.Message}");
            failures++;
        }
    }
    return failures == 0 ? 0 : 1;
}

static void WriteReport(WorkshopService service, string path)
{
    using var writer = new StreamWriter(path);
    writer.WriteLine("id,title,customer,status,priority,due");
    foreach (var o in service.ListOrders())
        writer.WriteLine($"{o.Id},\"{o.Title}\",\"{o.Customer}\",{o.Status},{o.Priority},{o.DueDate:yyyy-MM-dd}");
}

static string? TakeOption(List<string> args, string name)
{
    var i = args.IndexOf(name);
    if (i < 0 || i + 1 >= args.Count) return null;
    var value = args[i + 1];
    args.RemoveRange(i, 2);
    return value;
}

static bool TakeFlag(List<string> args, string name) => args.Remove(name);
