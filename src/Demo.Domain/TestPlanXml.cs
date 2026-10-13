using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Demo.Domain;

/// <summary>Reads, writes and validates plan files. Every plan, whoever created it, passes through <see cref="Validate"/>.</summary>
public static class TestPlanXml
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Lazy<XmlSchemaSet> Schema = new(LoadSchema);

    public static string SchemaText
    {
        get
        {
            using var stream = typeof(TestPlanXml).Assembly.GetManifestResourceStream("Demo.Domain.TestPlan.xsd")!;
            return new StreamReader(stream).ReadToEnd();
        }
    }

    public static string ToXml(TestPlan plan)
    {
        var doc = new XDocument(
            new XElement("TestPlan",
                new XAttribute("id", plan.Id),
                new XAttribute("name", plan.Name),
                new XAttribute("device", plan.Device),
                new XAttribute("created", plan.Created.ToString("yyyy-MM-ddTHH:mm:sszzz", Inv)),
                new XAttribute("source", plan.Source),
                new XElement("Conditions",
                    plan.Conditions.Select(c => new XElement("Sweep",
                        new XAttribute("parameter", c.Parameter),
                        new XAttribute("units", c.Units),
                        new XAttribute("values", string.Join(' ', c.Values.Select(v => v.ToString(Inv))))))),
                new XElement("Samples", new XAttribute("count", plan.Samples)),
                new XElement("Tests",
                    plan.Tests.Select(t => new XElement("Test",
                        new XAttribute("id", t.Id),
                        new XAttribute("name", t.Name),
                        new XAttribute("units", t.Units),
                        t.Min is { } min ? new XAttribute("min", min.ToString(Inv)) : null,
                        t.Max is { } max ? new XAttribute("max", max.ToString(Inv)) : null)))));
        return doc.Declaration + doc.ToString() + Environment.NewLine;
    }

    /// <summary>Parses and fully validates plan XML (schema, then bench rules).</summary>
    public static TestPlan Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new DomainException($"The plan is not valid XML: {ex.Message}");
        }

        var errors = new List<string>();
        doc.Validate(Schema.Value, (_, e) => errors.Add(e.Message));
        if (errors.Count > 0) throw new DomainException($"The plan does not match TestPlan.xsd: {string.Join(" ", errors.Take(3))}");

        var root = doc.Root!;
        var plan = new TestPlan(
            (string)root.Attribute("id")!,
            (string)root.Attribute("name")!,
            (string)root.Attribute("device")!,
            DateTimeOffset.Parse((string)root.Attribute("created")!, Inv),
            (string?)root.Attribute("source") ?? "imported",
            root.Element("Conditions")!.Elements("Sweep").Select(s => new ConditionSweep(
                (string)s.Attribute("parameter")!,
                (string)s.Attribute("units")!,
                ((string)s.Attribute("values")!).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, Inv)).ToList())).ToList(),
            (int)root.Element("Samples")!.Attribute("count")!,
            root.Element("Tests")!.Elements("Test").Select(t => new PlannedTest(
                (string)t.Attribute("id")!,
                (string)t.Attribute("name")!,
                (string)t.Attribute("units")!,
                (double?)t.Attribute("min"),
                (double?)t.Attribute("max"))).ToList());
        Validate(plan);
        return plan;
    }

    /// <summary>The bench's rules for a runnable plan. Throws <see cref="DomainException"/> with every problem found.</summary>
    public static void Validate(TestPlan plan)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Name)) problems.Add("The plan needs a name.");
        if (!Bench.Fixtures.Contains(plan.Device, StringComparer.OrdinalIgnoreCase))
            problems.Add($"The bench has no fixture for device '{plan.Device}'. Fixtures: {string.Join(", ", Bench.Fixtures)}.");
        if (plan.Samples is < 1 or > Bench.MaxSamples) problems.Add($"Samples must be 1 to {Bench.MaxSamples} units.");

        foreach (var group in plan.Conditions.GroupBy(c => c.Parameter, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Condition {group.Key} is listed more than once.");
        foreach (var definition in Bench.Conditions)
        {
            var sweep = plan.Conditions.FirstOrDefault(c => string.Equals(c.Parameter, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (sweep is null) { problems.Add($"Condition {definition.Id} is missing; give at least one value."); continue; }
            if (sweep.Units != definition.Units) problems.Add($"{definition.Id} must be in {definition.Units}, not '{sweep.Units}'.");
            if (sweep.Values.Count == 0) problems.Add($"{definition.Id} needs at least one value.");
            if (sweep.Values.Distinct().Count() != sweep.Values.Count) problems.Add($"{definition.Id} has duplicate values.");
            foreach (var v in sweep.Values.Where(v => v < definition.Min || v > definition.Max))
                problems.Add($"{definition.Id} {v} {definition.Units} is outside the bench range {definition.Min} to {definition.Max} {definition.Units}.");
        }

        if (plan.Tests.Count == 0) problems.Add("The plan needs at least one test.");
        foreach (var group in plan.Tests.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Test {group.Key} is listed more than once.");
        foreach (var test in plan.Tests)
        {
            var known = Bench.Measurements.FirstOrDefault(m => string.Equals(m.Id, test.Id, StringComparison.OrdinalIgnoreCase));
            if (known is null) { problems.Add($"The bench cannot measure '{test.Id}'. Available: {string.Join(", ", Bench.Measurements.Select(m => m.Id))}."); continue; }
            if (test.Units != known.Units) problems.Add($"{test.Id} is measured in {known.Units}, not '{test.Units}'.");
            if (test.Min is null && test.Max is null) problems.Add($"{test.Id} needs a min or max limit.");
            if (test.Min is { } lo && test.Max is { } hi && lo >= hi) problems.Add($"{test.Id}: min ({lo}) must be below max ({hi}).");
        }

        if (problems.Count == 0 && plan.MeasurementCount > Bench.MaxMeasurementsPerRun)
            problems.Add($"The plan has {plan.MeasurementCount} measurements; the bench allows {Bench.MaxMeasurementsPerRun} per run. Use fewer conditions or units.");

        if (problems.Count > 0) throw new DomainException(string.Join(" ", problems));
    }

    private static XmlSchemaSet LoadSchema()
    {
        var set = new XmlSchemaSet();
        using var reader = XmlReader.Create(new StringReader(SchemaText));
        set.Add(null, reader);
        set.Compile();
        return set;
    }
}
