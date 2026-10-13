using System.Globalization;
using System.Net;
using System.Text;
using ScottPlot;

namespace Demo.Domain;

/// <summary>The files a run leaves behind. Paths are absolute so a coding agent can open them directly.</summary>
public sealed record RunFiles(string Folder, string ResultsCsv, string SummaryCsv, string PlotsFolder, IReadOnlyList<string> Plots, string? Report);

/// <summary>Reads and writes the result files: results.csv, summary.csv, plots/*.png and report.html.</summary>
public static class ResultFiles
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>UTF-8 with a BOM, so Excel shows µA and °C correctly.</summary>
    public static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    public const string CsvHeader = "dut,temperature_c,supply_v,load_ma,test,value,units,min,max,result";

    public static string ToCsvLine(Measurement m) => string.Join(',',
        m.Dut.ToString(Inv), m.TemperatureC.ToString(Inv), m.SupplyV.ToString(Inv), m.LoadMa.ToString(Inv), m.TestId,
        m.Value.ToString("G6", Inv), m.Units, m.Min?.ToString(Inv) ?? "", m.Max?.ToString(Inv) ?? "", m.Pass ? "PASS" : "FAIL");

    public static IReadOnlyList<Measurement> ReadCsv(string path)
    {
        if (!File.Exists(path)) return [];
        static double? N(string s) => s.Length == 0 ? null : double.Parse(s, Inv);

        // The bench may still be appending: share the file, and ignore a half-written last line.
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var rows = new List<Measurement>();
        reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(',');
            if (f.Length != 10 || (f[9] != "PASS" && f[9] != "FAIL")) continue;
            rows.Add(new Measurement(int.Parse(f[0], Inv), double.Parse(f[1], Inv), double.Parse(f[2], Inv), double.Parse(f[3], Inv),
                f[4], double.Parse(f[5], Inv), f[6], N(f[7]), N(f[8]), f[9] == "PASS"));
        }
        return rows;
    }

    public static void WriteSummaryCsv(RunSummary summary, string path)
    {
        var sb = new StringBuilder("test,name,units,limits,count,failed,mean,std_dev,lowest,highest,cpk,drift_per_c,worst_case\n");
        foreach (var t in summary.Tests)
            sb.AppendLine(string.Join(',', t.TestId, Text(t.Name), t.Units, Text(t.Limits), t.Count, t.Failed,
                t.Mean.ToString(Inv), t.StdDev.ToString(Inv), t.Lowest.ToString("G6", Inv), t.Highest.ToString("G6", Inv),
                t.Cpk?.ToString(Inv) ?? "", t.DriftPerDegreeC?.ToString(Inv) ?? "", Text(t.WorstCase)));
        File.WriteAllText(path, sb.ToString(), CsvEncoding);
    }

    /// <summary>One PNG per test: value against the main swept condition, one series per DUT, limits as dashed lines.</summary>
    public static IReadOnlyList<string> WritePlots(TestPlan plan, IReadOnlyList<Measurement> rows, string folder)
    {
        Directory.CreateDirectory(folder);
        var (axisName, axisUnits, x) = XAxis(plan);
        var paths = new List<string>();
        foreach (var test in plan.Tests)
        {
            var testRows = rows.Where(r => r.TestId == test.Id).ToList();
            if (testRows.Count == 0) continue;

            var plot = new Plot();
            foreach (var dut in testRows.GroupBy(r => r.Dut).OrderBy(g => g.Key))
            {
                var points = dut.Select(r => (X: x(r), r.Value)).OrderBy(p => p.X).ToList();
                var scatter = plot.Add.ScatterPoints(points.Select(p => p.X).ToArray(), points.Select(p => p.Value).ToArray());
                scatter.LegendText = $"DUT {dut.Key}";
                scatter.MarkerSize = 7;
                var means = points.GroupBy(p => p.X).Select(g => (X: g.Key, Y: g.Average(p => p.Value))).ToList();
                if (means.Count > 1)
                {
                    var line = plot.Add.ScatterLine(means.Select(m => m.X).ToArray(), means.Select(m => m.Y).ToArray());
                    line.Color = scatter.Color.WithAlpha(0.5);
                    line.LineWidth = 1.5f;
                }
            }
            foreach (var (limit, label) in new[] { (test.Max, "max"), (test.Min, "min") })
            {
                if (limit is not { } value) continue;
                var h = plot.Add.HorizontalLine(value);
                h.Color = Colors.Red;
                h.LinePattern = LinePattern.Dashed;
                h.LineWidth = 1.5f;
                h.LegendText = $"{label} {value.ToString(Inv)} {test.Units}";
            }
            plot.Title($"{test.Name} ({test.Id}) · {plan.Device}");
            plot.XLabel($"{axisName} ({axisUnits})");
            plot.YLabel($"{test.Id} ({test.Units})");
            plot.ShowLegend(Alignment.UpperLeft);

            var path = Path.Combine(folder, $"{test.Id}.png");
            plot.SavePng(path, 800, 450);
            paths.Add(path);
        }
        return paths;
    }

    public static void WriteReport(TestRun run, TestPlan plan, RunSummary summary, RunFiles files, string path)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var verdictColor = summary.Verdict switch { Verdict.Fail => "#b91c1c", Verdict.PassWithWarnings => "#b45309", _ => "#15803d" };
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>{{E(run.Id)}} · {{E(plan.Name)}}</title>
            <style>
              body { font: 14px/1.5 "Segoe UI", system-ui, sans-serif; color: #1f2937; max-width: 920px; margin: 32px auto; padding: 0 16px; }
              h1 { font-size: 22px; margin-bottom: 4px; } h2 { font-size: 17px; margin-top: 28px; border-bottom: 1px solid #e5e7eb; padding-bottom: 4px; }
              .meta { color: #6b7280; } .verdict { display: inline-block; padding: 2px 10px; border-radius: 999px; color: white; background: {{verdictColor}}; font-weight: 600; }
              table { border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; } th, td { text-align: left; padding: 4px 8px; border-bottom: 1px solid #f0f0f0; }
              li.Fail { color: #b91c1c; } li.Warning { color: #b45309; } img { max-width: 100%; border: 1px solid #e5e7eb; margin: 8px 0; }
            </style></head><body>
            <h1>{{E(plan.Name)}}</h1>
            <div class="meta">Run {{E(run.Id)}} of plan {{E(plan.Id)}} · device {{E(plan.Device)}} · {{run.Started.ToString("yyyy-MM-dd HH:mm", Inv)}} · {{summary.Measurements}} measurements, {{summary.Failed}} failed</div>
            <p><span class="verdict">{{summary.Verdict switch { Verdict.PassWithWarnings => "PASS WITH WARNINGS", var v => v.ToString().ToUpperInvariant() }}}</span></p>
            <h2>Key findings</h2><ul>
            """);
        foreach (var f in summary.Findings) sb.Append($"<li class=\"{f.Severity}\"><b>{f.Severity}:</b> {E(f.Text)}</li>\n");
        sb.Append("</ul><h2>Statistics</h2><table><tr><th>Test</th><th>Limits</th><th>N</th><th>Failed</th><th>Mean</th><th>σ</th><th>Min</th><th>Max</th><th>Cpk</th><th>Drift /°C</th></tr>\n");
        foreach (var t in summary.Tests)
            sb.Append($"<tr><td>{E(t.Name)} ({t.TestId})</td><td>{E(t.Limits)}</td><td>{t.Count}</td><td>{t.Failed}</td><td>{t.Mean.ToString(Inv)}</td><td>{t.StdDev.ToString(Inv)}</td><td>{t.Lowest.ToString("G5", Inv)}</td><td>{t.Highest.ToString("G5", Inv)}</td><td>{t.Cpk?.ToString(Inv) ?? "–"}</td><td>{t.DriftPerDegreeC?.ToString(Inv) ?? "–"}</td></tr>\n");
        sb.Append("</table><h2>Plots</h2>\n");
        foreach (var plot in files.Plots) sb.Append($"<img src=\"plots/{E(Path.GetFileName(plot))}\" alt=\"{E(Path.GetFileNameWithoutExtension(plot))} plot\">\n");
        sb.Append($"<h2>Files</h2><p><a href=\"results.csv\">results.csv</a> (every measurement) · <a href=\"summary.csv\">summary.csv</a> (statistics per test)</p></body></html>\n");
        File.WriteAllText(path, sb.ToString());
    }

    private static (string Name, string Units, Func<Measurement, double> X) XAxis(TestPlan plan)
    {
        if (plan.ValuesOf(Bench.Temperature).Count > 1) return ("Temperature", "°C", m => m.TemperatureC);
        if (plan.ValuesOf(Bench.SupplyVoltage).Count > 1) return ("Supply voltage", "V", m => m.SupplyV);
        if (plan.ValuesOf(Bench.LoadCurrent).Count > 1) return ("Load current", "mA", m => m.LoadMa);
        return ("DUT", "#", m => m.Dut);
    }

    /// <summary>A quoted text cell. One that starts like a formula gets a leading ' so Excel shows it instead of evaluating it.</summary>
    private static string Text(string s) =>
        "\"" + (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + s : s).Replace("\"", "\"\"") + "\"";
}
