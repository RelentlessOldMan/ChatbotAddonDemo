# ADR-004: Coding agents connect through the console tool (stdio MCP); no MCP server in the desktop app

**Status:** Accepted (revised: an earlier draft also hosted MCP over HTTP inside the WPF app)

## Context
Engineers who use coding agents (Claude Code, Codex, Cursor) want those agents to drive our tool. MCP is the standard way to do that. We have two programs that could host an MCP server:

- **demo-cli.exe**, a short-lived console process.
- **Demo.Desktop.exe**, a long-running WPF app that the engineer may or may not have open.

## Options
1. **Console tool only, over stdio.** The agent starts `demo-cli mcp` as a child process and talks over stdin/stdout. No port, no token: the process runs as the user who configured the agent, with their permissions, and only for as long as the agent session.
2. **Desktop app only, over HTTP on 127.0.0.1.** The agent attaches to the running app and can see what the user is looking at. Needs the app to be open, plus a listening port to secure: loopback bind, bearer token, Host/Origin checks against DNS rebinding, an on/off switch, an activity indicator, and token rotation.
3. **Both.**

## Decision
**Option 1.** `demo-cli mcp` is the single entry point for agents. Permissions are flags set by whoever configures the agent: read-only by default, `--allow-writes` for plans and reports, `--allow-runs` for the bench.

Option 2 is documented as an **optional extension, not built** ([design §8](../design.md#8-optional-not-built-mcp-inside-the-desktop-app)), for products where an agent and a person need to work in the same live session.

## Why
- It covers the need. Plans and results are files in a shared workspace, so whatever the agent does through the console tool shows up in the open desktop app anyway (it watches the folder).
- It has the smallest security surface: no network listener at all.
- It's the standard local-MCP shape, so setup is one line in either agent.
- One MCP host means one place to test and document.

## Consequences
- The desktop app has no MCP code and no MCP settings. Its Settings page points agent users to `demo-cli mcp`.
- An agent can't ask "what is the user looking at?" (`get_current_view` exists only in the chat panel). If that becomes important, build option 2 or a stdio shim that relays to the running app over a named pipe.
- If the agent session ends during a bench run, the console tool cancels the run before it exits (status *Cancelled*), so the bench isn't left busy. If the process is killed outright, the run shows as *Interrupted* once its heartbeat is 15 s old. Agents should call `wait_for_test_run` before ending.
