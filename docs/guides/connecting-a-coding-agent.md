# Connecting a coding agent (MCP)

*For engineers who use Claude Code, Codex, Cursor or another MCP-capable agent.*

Your agent connects to the **console tool**, `demo-cli.exe`. It starts the tool itself in MCP mode (`demo-cli mcp`) and talks to it over stdin/stdout, the standard way local MCP servers work.

- **No API key goes into our tool, and it never calls a model.** 💲 All the tokens are spent by your agent, talking to its own model on your agent subscription, before and after each tool call. The traffic between your agent and `demo-cli` is free local I/O.
- Our tools return compact summaries (verdicts and findings, not raw CSV), which keeps your agent's context, and your bill, small. The full data is still in the files if the agent wants it.
- **Nothing has to be open.** The agent starts the tool when it needs it, and the tool exits when the session ends.
- **No network port.** Nothing listens on your machine.
- The desktop app doesn't need to be running. If it is, it shows the agent's new plans and runs live, because both apps use the same workspace folder.

## Choose what the agent may do

| Flags | The agent can… |
|---|---|
| *(none)* | Read only: list specs, plans and runs; analyze results; query measurements |
| `--allow-writes` | …also create test plans and HTML reports |
| `--allow-runs` | …also start and stop runs on the bench |

Start with no flags and add them as you get comfortable. Your agent will also ask your permission before calling tools that aren't read-only.

## Claude Code
```powershell
claude mcp add testbench -- "C:\Program Files\TestBench\demo-cli.exe" mcp --allow-writes --allow-runs
```

## Codex
```toml
# %USERPROFILE%\.codex\config.toml
[mcp_servers.testbench]
command = 'C:\Program Files\TestBench\demo-cli.exe'
args = ["mcp", "--allow-writes", "--allow-runs"]
```

To use a specific workspace folder, add `"--workspace", "D:\\Lab\\workspace"` before `"mcp"` (or set `DEMO_WORKSPACE`).

## What the agent sees

![MCP session](../images/11-mcp-session.png)

- Every tool has a description written for models, a JSON schema, and annotations: `readOnlyHint: true` means it only reads, and `openWorldHint: true` means it drives the bench.
- The server sends instructions on connect: the workflow (spec → plan → run → analysis) and rules such as "don't compute statistics yourself; use `get_run_summary`".
- Rule violations come back as readable results the agent can act on, e.g. `{"error": "Temperature 200 °C is outside the bench range -55 to 150 °C."}`.
- Results include **absolute file paths** (`results.csv`, `plots\IQ.png`, `report.html`), so an agent with file access can open them for its own analysis.
- Runs take time: call `start_test_run`, then `wait_for_test_run` (up to 120 s per call), then `get_run_summary`. If the session ends mid-run, the run is marked *Interrupted*.

## "Couldn't my agent just run demo-cli commands in a shell?"
Yes, and that works too (`demo-cli run TP-1002`, `demo-cli summary R-1002`). MCP adds typed tools with descriptions, structured results instead of console text, and the same read / write / run permission tiers the chat assistant has. It also works in agents that have no shell.
