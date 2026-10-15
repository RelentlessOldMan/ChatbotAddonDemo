using System.Globalization;
using System.Text;
using System.Text.Json;
using Demo.Capabilities;

namespace Demo.Desktop.ViewModels;

/// <summary>The wording of an approval card, the same whether the chat panel's assistant or a coding agent is asking.</summary>
public static class ApprovalText
{
    private const int MaxArgumentLength = 200;

    /// <param name="who">"The assistant", or the coding agent's name.</param>
    public static string Heading(string who, Capability capability) => capability switch
    {
        // An agent names itself, so its name is forced onto one line like the arguments below.
        { Server: { } server } => $"{OneLine(who)} wants to use {server}",
        { Safety: SafetyClass.Execute } => $"{OneLine(who)} wants to use the test bench",
        { Safety: SafetyClass.Write } => $"{OneLine(who)} wants to create a file",
        // A read normally runs without asking, but is asked about when it comes in the same reply as a change.
        _ => $"{OneLine(who)} wants to look something up",
    };

    /// <summary>
    /// One line per argument; strings bare, everything else (numbers, lists) as compact JSON. The values come from
    /// the model, so each is forced onto one line (a value with line breaks can't pass for extra arguments) and cut short.
    /// </summary>
    public static string Arguments(IReadOnlyDictionary<string, object?> arguments) =>
        string.Join("\n", arguments.Select(a => $"{OneLine(a.Key)}: {OneLine(a.Value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
            string s => s,
            var v => JsonSerializer.Serialize(v, CapabilityCatalog.SerializerOptions),
        })}"));

    private static string OneLine(string value)
    {
        var text = new StringBuilder(Math.Min(value.Length, MaxArgumentLength + 10));
        foreach (var c in value)
        {
            if (text.Length >= MaxArgumentLength) return text.Append('…').ToString();
            text.Append(c switch
            {
                '\r' => @"\r",
                '\n' => @"\n",
                '\t' => @"\t",
                _ when char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => $@"\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return text.ToString();
    }
}
