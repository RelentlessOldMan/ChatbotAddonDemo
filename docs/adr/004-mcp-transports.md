# ADR-004: stdio MCP for the console app, loopback Streamable HTTP for the WPF app

**Status:** Accepted

## Context
External agents (Claude Code, Codex, Cursor…) connect over MCP. Our two apps have different lifecycles.

## Decision
- **Console app:** `demo-cli mcp` serves MCP over **stdio**. The agent spawns the process. No network surface and no auth needed, because the trust boundary is the user who configured the agent.
- **WPF app:** hosts **Streamable HTTP** on `127.0.0.1:<port>/mcp` inside the running app, so the agent works with the live session the user is looking at. It is off by default, protected by a per-install bearer token, validates Host/Origin headers, and has writes disabled unless the user enables them.

## Alternatives considered
- **WPF via a stdio shim** (a small exe that the agent spawns, which relays to the app over a named pipe). This avoids a TCP port, but it adds a component and a second IPC protocol. Worth revisiting if customers' security teams object to any listening port.
- **Console app over HTTP.** That would mean a long-running daemon, which doesn't match how the console tool is used.

## Consequences
- Tool annotations (`readOnlyHint`, `destructiveHint`) come from the capability's safety class, so agents' permission prompts work correctly.
- The same exposure policy object (`McpExposurePolicy`) controls both hosts.
