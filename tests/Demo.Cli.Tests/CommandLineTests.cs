namespace Demo.Cli.Tests;

/// <summary>
/// The console tool's commands, run as a script would. A wrong command line is a short message and exit code 2,
/// never a stack trace and never a guess.
/// </summary>
public sealed class CommandLineTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "cli-args", Guid.NewGuid().ToString("N"));

    private CliResult Cli(params string[] arguments) => CliProcess.Run(_workspace, ["--workspace", _workspace, .. arguments]);

    [Theory]
    [InlineData("--samples must be a whole number", "plan", "from-spec", "LDO-3300_datasheet.csv", "--temps", "25", "--samples", "three")]
    [InlineData("--temps must be a comma-separated list of numbers", "plan", "from-spec", "LDO-3300_datasheet.csv", "--temps", "25,hot")]
    [InlineData("--temp must be a number", "summary", "R-1001", "--temp", "hot")]
    [InlineData("--temp must be a number", "summary", "R-1001", "--temp", "NaN")]
    [InlineData("--supply must be a number", "query", "R-1001", "--supply", "Infinity")]
    [InlineData("--temps must be a comma-separated list of numbers", "plan", "from-spec", "LDO-3300_datasheet.csv", "--temps", "25,1e400")]
    [InlineData("--tests must be a comma-separated list", "plan", "from-spec", "LDO-3300_datasheet.csv", "--tests", " , ")]
    [InlineData("--dut must be a whole number", "query", "R-1001", "--dut", "1.5")]
    [InlineData("--temps needs a value", "plan", "from-spec", "LDO-3300_datasheet.csv", "--temps")]
    [InlineData("--name needs a value", "plan", "from-spec", "LDO-3300_datasheet.csv", "--name", "--temps", "25")]
    [InlineData("unknown option '--allow-run'. Did you mean --allow-runs?", "mcp", "--allow-run")]
    [InlineData("unknown option '--frobnicate'", "plans", "--frobnicate")]
    [InlineData("--failed does not apply here", "runs", "--failed")]
    [InlineData("missing argument. Usage: demo-cli plan show <TP-id>", "plan", "show")]
    [InlineData("unexpected argument 'extra'", "run", "TP-1001", "extra")]
    [InlineData("unknown command 'frobnicate'", "frobnicate")]
    [InlineData("unknown plan command 'delete'", "plan", "delete", "TP-1001")]
    public void Usage_errors_exit_with_2_and_a_message(string message, params string[] arguments)
    {
        var result = Cli(arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("error: ", result.Error);
        Assert.Contains(message, result.Error);
        Assert.DoesNotContain("   at ", result.Error);
        Assert.False(Directory.Exists(_workspace), "a bad command line must not open (and seed) the workspace");
    }

    [Fact]
    public void A_trailing_workspace_option_without_a_folder_is_an_error_not_the_default_workspace()
    {
        // DEMO_WORKSPACE points at _workspace too, so a regression would seed that folder rather than the real one.
        var result = CliProcess.Run(_workspace, "plans", "--workspace");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--workspace needs a value", result.Error);
        Assert.False(Directory.Exists(_workspace));
    }

    [Fact]
    public void Bad_input_and_damaged_files_are_a_message_and_exit_code_1_never_a_crash()
    {
        var badName = Cli("plan", "from-spec", "LDO-3300_datasheet.csv", "--name", "bad\u0001name");
        Assert.Equal(1, badName.ExitCode);
        Assert.Contains("ordinary printable characters", badName.Error);

        File.WriteAllText(Path.Combine(_workspace, "runs", "R-1001", "results.csv"), "dut,temp\nnot,numbers\n"); // hand-edited
        var damaged = Cli("query", "R-1001");
        Assert.Equal(1, damaged.ExitCode);
        Assert.StartsWith("error: ", damaged.Error);

        Assert.DoesNotContain("   at ", badName.Error + damaged.Error);
    }

    [Fact]
    public void Options_may_come_before_the_positional_arguments()
    {
        var result = Cli("plan", "from-spec", "--temps", "25", "--samples", "2", "--tests", "IQ", "LDO-3300_datasheet.csv");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("Created TP-1002 (2 measurements)", result.Output);
    }

    [Fact]
    public void Negative_numbers_are_values_not_options()
    {
        var result = Cli("plan", "from-spec", "LDO-3300_datasheet.csv", "--temps", "-40,25", "--samples", "1", "--tests", "IQ");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("Created TP-1002 (2 measurements)", result.Output);
    }

    [Fact]
    public void Blank_entries_in_a_list_are_ignored()
    {
        var result = Cli("plan", "from-spec", "LDO-3300_datasheet.csv", "--temps", "25,", "--samples", "1", "--tests", "IQ, VOUT,");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("Created TP-1002 (2 measurements)", result.Output);
    }

    [Fact]
    public void Ordinary_commands_still_work_without_any_AI()
    {
        var plans = Ok("plans");
        var created = Ok("plan", "from-spec", "LDO-3300_datasheet.csv", "--temps", "25,125", "--samples", "2", "--tests", "IQ");
        var run = Ok("run", "TP-1002");

        Assert.Contains("TP-1001", plans);
        Assert.Contains("Created TP-1002 (4 measurements)", created);
        Assert.Contains("R-1002: Completed", run);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("query")]
    public void Filters_that_match_nothing_are_an_error_for_summary_and_query_alike(string command)
    {
        var result = Cli(command, "R-1001", "--temp", "150");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("error: No measurements in R-1001 match", result.Error);
        Assert.Contains("temperatures 25 °C", result.Error); // what the run does have
        Assert.Equal("", result.Output);
    }

    [Fact]
    public void A_query_for_failures_in_a_run_without_any_prints_just_the_header()
    {
        var result = Cli("query", "R-1001", "--failed");

        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public void A_missing_file_is_a_command_error_with_exit_code_1()
    {
        var result = Cli("plan", "import", Path.Combine(_workspace, "no-such-plan.xml"));

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("error: ", result.Error);
        Assert.Contains("no-such-plan.xml", result.Error);
        Assert.DoesNotContain("   at ", result.Error);
    }

    [Fact]
    public void Business_rule_errors_exit_with_1()
    {
        var result = Cli("run", "TP-404");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not exist", result.Error);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("plans", "--help")]
    [InlineData] // no command at all
    public void Help_exits_with_0(params string[] arguments)
    {
        var result = Cli(arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage: demo-cli", result.Output);
        Assert.Contains("DEMO_WORKSPACE", result.Output);
        Assert.Contains(@"%LOCALAPPDATA%\ChatbotAddonDemo\workspace", result.Output);
        Assert.False(Directory.Exists(_workspace), "help must not open (and seed) the workspace");
    }

    private string Ok(params string[] arguments)
    {
        var result = Cli(arguments);
        Assert.True(result.ExitCode == 0, result.Error);
        return result.Output;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
