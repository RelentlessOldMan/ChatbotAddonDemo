# Connecting a coding agent (MCP)

*For engineers who use Claude Code, Codex, Cursor or another MCP-capable agent.*

Your agent connects to the **console tool**, `demo-cli.exe`. It starts the tool itself in MCP mode (`demo-cli mcp`) and talks to it over stdin/stdout, the standard way local MCP servers work. (MCP, the Model Context Protocol, is the standard way coding agents plug in outside tools.)

- **No API key goes into our tool, and it never calls a model.** 💲 All the tokens are spent by your agent, talking to its own model on your agent subscription or API key, before and after each tool call. The traffic between your agent and `demo-cli` is free local I/O.
- **What leaves your PC:** our tool results (plan details, spec limits, run findings) become part of your agent's conversation, so they go to your agent's AI provider under your agent's terms.
- Our tools return compact summaries (verdicts and findings, not raw CSV), which keeps your agent's context, and your bill, small. The full data is still in the files if the agent wants it.
- **Nothing has to be open.** The agent starts the tool when it needs it, and the tool exits when the session ends.
- **No network port.** Nothing listens on your machine.
- The desktop app doesn't need to be running. If it is, it shows the agent's new plans and runs live, because both apps use the same workspace folder.

## Choose what the agent may do

Each tool has a safety class (Read / Write / Execute). The flags decide which classes the agent gets:

| Flags | The agent can… |
|---|---|
| *(none)* | Read tools only: list specs, plans and runs; analyze results; query measurements |
| `--allow-writes` | …also use Write tools: create test plans and HTML reports |
| `--allow-runs` | …also use Execute tools: start and stop runs on the bench |

Start with no flags (read-only, as in the snippets below) and add them as you get comfortable. Your agent will also ask your permission before calling tools that aren't read-only.

The paths below are for an installed copy. **From a clone of this repo**, build first (`dotnet build ChatbotAddonDemo.sln`) and use `<repo>\src\Demo.Cli\bin\Debug\net10.0\demo-cli.exe` instead.

## Claude Code
```powershell
claude mcp add --scope user testbench -- "C:\Program Files\TestBench\demo-cli.exe" mcp
```

`--scope user` makes it available in every folder; without it, Claude Code only uses it in the folder you ran the command in.

To allow plans and bench runs, add the flags at the end: `… demo-cli.exe" mcp --allow-writes --allow-runs`. To use a specific workspace folder, add `--workspace D:\Lab\workspace` (it can go before or after `mcp`).

## Codex
```powershell
codex mcp add testbench -- "C:\Program Files\TestBench\demo-cli.exe" mcp
```

That writes this to your Codex configuration, which you can also edit by hand:

```toml
# %USERPROFILE%\.codex\config.toml
[mcp_servers.testbench]
command = 'C:\Program Files\TestBench\demo-cli.exe'
args = ["mcp"]
```

To allow plans and bench runs, use `args = ["mcp", "--allow-writes", "--allow-runs"]`. To use a specific workspace folder, add `"--workspace", 'D:\Lab\workspace'` to `args`.

Instead of `--workspace` you can set the `DEMO_WORKSPACE` environment variable. Without either, the tool uses `%LOCALAPPDATA%\ChatbotAddonDemo\workspace`, the same default as the desktop app.

## What the agent sees

![MCP session](../images/11-mcp-session.png)

- Every tool has a description written for models, a JSON schema, and annotations (hints your agent uses for its permission prompts):

  | Annotation | Set on | Means |
  |---|---|---|
  | `readOnlyHint: true` | Read tools | Only looks |
  | `idempotentHint: true` | Read tools and `generate_report` | Calling it again with the same inputs has no extra effect |
  | `openWorldHint: true` | Execute tools (`start_test_run`, `cancel_test_run`) | Affects things outside the app (here, the bench) |
  | `destructiveHint: true` | `cancel_test_run` only | May destroy work (the rest of the run is never measured) |

- The server sends instructions on connect: the workflow (spec → plan → run → analysis) and rules such as "don't compute statistics yourself; use `get_run_summary`".
- Rule violations and bad arguments come back as tool errors (`isError: true`) with a readable message the agent can act on, e.g. "Temperature 200 °C is outside the bench range -55 to 150 °C." An error about an argument names that argument.
- Results include **absolute file paths** (`results.csv`, `plots\IQ.png`, `report.html`), so an agent with file access can open them for its own analysis.
- Runs take time: call `start_test_run`, then `wait_for_test_run` (up to 120 s per call), then `get_run_summary`. If the session ends mid-run, the tool cancels the run (status *Cancelled*); if it's killed outright, the run shows as *Interrupted* after about 15 s.

## "Couldn't my agent just run demo-cli commands in a shell?"
Yes, and that works too (`demo-cli run TP-1002`, `demo-cli summary R-1002`; `demo-cli help` lists the commands, the workspace options and its default). Commands exit with 0 when done, 1 when the command failed (for example a run that doesn't exist, or `summary` or `query` with conditions such as a temperature the run never reached), and 2 when the command line is wrong. MCP adds typed tools with descriptions, structured results instead of console text, and the same safety classes (Read / Write / Execute) the chat assistant has. It also works in agents that have no shell.
