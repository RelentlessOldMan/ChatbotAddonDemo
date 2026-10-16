# ADR-007: An MCP hub in the desktop app: one door for every AI

**Status:** Accepted. Supersedes [ADR-004](004-mcp-via-console-tool.md), which kept MCP out of the desktop app.

## Context
ADR-004 let coding agents in only through the console tool (`demo-cli mcp`) and left the desktop app without MCP. Three things changed that:

- **The two programs are separate apps**, each with its own workspace, not one app in two forms. An agent working through the console tool works on the console tool's files, so it can't help with what the user has open in the desktop app.
- **Agents working next to a person** in the open app turned out to be the interesting case: "look at the run I have open and plan a follow-up", with the person approving changes in the window they're looking at.
- **Customers already run other MCP servers** (Perforce, GitHub, Microsoft 365…) and want the chat assistant to use them too, not only our tools.

## Options
1. **Keep ADR-004.** No MCP in the desktop app. Agents can't see the open app, and the chat can't use other servers.
2. **An HTTP MCP server inside the app** on 127.0.0.1. Needs a port, a bearer token, Host/Origin checks against DNS rebinding, and token rotation.
3. **An MCP hub inside the app, reached locally:**
   - the chat panel calls the app's tools through it in-process;
   - coding agents reach it through a named pipe, via a relay they start (`demo-desktop.exe mcp`);
   - the hub also connects the chat to other MCP servers.

## Decision
**Option 3.**

- **The hub is the one door for every AI in the desktop app** (`Demo.Mcp`, `McpHub`).
  - **The chat panel (C3)** gets its tools from the hub over an in-process MCP connection: the app's own tools, then those of any other servers.
  - **Coding agents (D1)** configure `demo-desktop.exe mcp`. It opens no window; it only passes MCP messages between the agent's stdin/stdout and the open app's named pipe.
    - The pipe is only for the current Windows user and refuses network clients.
    - It only exists while the user has turned on "Let coding agents connect to this window" (off by default; IT can turn it off).
  - **Other MCP servers** come from Settings (the user's) and from machine policy (IT's). IT can also allow only its own.
- **Approvals happen in the app, the same for every AI.**
  - Reads run without asking.
  - A change (Write) or a bench action (Execute) waits for an approval card. For the chat it's in the chat panel; for agents it's in the app's "Coding agents" panel, which appears even with the chat turned off.
  - A declined call tells the agent that the user declined, and not to retry.
- **Agents see only the app's own tools, not other servers' tools.** The agent can connect to those servers itself, under its own rules and with its own credentials. Relaying them through us would only add a place where permissions could get mixed up.
- **Other servers' tools need approval every time**, unless IT marks the server as trusted.
  - The read-only labels a server puts on its own tools aren't trusted: it's someone else's code.
  - A server the user adds is never trusted.
- **The console tool keeps its own MCP door** (`demo-cli mcp`, D2), unchanged, with its permission flags. It has no hub and no other servers: its users drive it with an agent, and the agent can connect to other servers itself.

## Why
- **One place for approvals and permissions in the desktop app.** The chat and agents pass the same gate, so they can't drift apart.
- **No listening port.** The pipe is local, current-user-only and off by default, so there is no token to manage and nothing to secure against web pages (DNS rebinding).
- **The relay is the standard local MCP shape.** The agent starts a command and speaks stdio, so setup is the same one line as for `demo-cli mcp`.
- **The chat calls tools the same way an agent does.** The hub's tests check that the chat sees the same tools, schemas and results through MCP as from the catalog directly.

## Consequences
- **The desktop app depends on the MCP SDK** (through `Demo.Mcp`).
- **Agents get `get_current_view`**, so they can ask what the user is looking at.
- **Only one TestBench window per Windows user can let agents in.** A second window's agent panel says so.
- **Outside servers can be slow or broken.** The hub connects them side by side and gives each one 30 seconds. A server that fails is skipped, and the chat shows a notice naming it, so one broken server never blocks the assistant. The same goes for a single tool whose inputs the AI providers would refuse: every tool goes out with every message, so one such tool would otherwise fail every message.
- **Outside tools send data to the chat's model.** Their results go to the provider in the next request, like any tool result: 💲 tokens, and data leaving the PC. That's why each use asks first by default, and why IT decides which servers are trusted.
- **A server started from a command line runs as the user**, with the user's rights. Only add servers you trust; IT can limit the list.
