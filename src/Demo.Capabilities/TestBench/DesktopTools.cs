using Demo.Domain;

namespace Demo.Capabilities.TestBench;

/// <summary>
/// What AI may do in the desktop app, and what it's told: the bench capabilities plus one only the running UI can
/// answer. One definition, used by the app and by the tests that stand in for it.
/// </summary>
public static class DesktopTools
{
    /// <summary>What every AI in the desktop app is told: the workflow, and how to use what the user has selected.</summary>
    public const string Instructions = TestBenchCapabilities.Instructions + "\n" + AppContextCapabilities.Instructions;

    /// <summary>What a coding agent is told on top: it works with the open window, and changes wait for the user.</summary>
    public const string AgentInstructions = Instructions + "\n" +
        "- You are working with the TestBench window the user has open. Creating plans or reports and using the bench wait for the user to approve them in that window; if they decline, accept it and don't retry unless they ask.";

    public static CapabilityCatalog Catalog(TestBenchService bench, SelectionContext selection) =>
        CapabilityCatalog.FromObject(new TestBenchCapabilities(bench))
            .Concat(CapabilityCatalog.FromObject(new AppContextCapabilities(selection)));
}
