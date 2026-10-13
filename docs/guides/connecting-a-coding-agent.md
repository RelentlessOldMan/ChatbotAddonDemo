# Connecting a coding agent (MCP)

*For users of Claude Code, Codex, Cursor and other MCP-capable agents.*

Both tools expose the same set of operations ("capabilities") over the [Model Context Protocol](https://modelcontextprotocol.io). Your agent uses its own model subscription, so **no API key is configured in our apps**.

| | Desktop app (`Demo.Desktop`) | Console tool (`demo-cli`) |
|---|---|---|
| Transport | Streamable HTTP on `127.0.0.1` | stdio (the agent launches it) |
| When to use | Work alongside a person using the app; see their current selection | Headless and scripted work, no UI needed |
| Auth | Bearer token | None (runs as you) |
| Writes | Off unless "Allow agents to make changes" is ticked | Off unless `--allow-writes` |
| Extra tools | `get_current_view` (what the user is looking at) | none |

## Desktop app
1. **Settings → Agent access (MCP)**, tick **Let coding agents connect to this app**, then **Save**. The status bar turns green.
2. Click **Copy for Claude Code** or **Copy for Codex** and run or paste the result.

Claude Code:
```powershell
claude mcp add --transport http demo-desktop http://127.0.0.1:47821/mcp --header "Authorization: Bearer <token>"
```

Codex (keeps the token out of `config.toml`):
```powershell
setx DEMO_DESKTOP_MCP_TOKEN <token>
```
```toml
# %USERPROFILE%\.codex\config.toml
[mcp_servers.demo_desktop]
url = "http://127.0.0.1:47821/mcp"
bearer_token_env_var = "DEMO_DESKTOP_MCP_TOKEN"
```

The app must be running for the agent to connect.

![Agent access](../images/03-settings-agent-access.png)

## Console tool
Claude Code:
```powershell
claude mcp add demo-cli -- "C:\Program Files\Workshop\demo-cli.exe" mcp
```
Codex:
```toml
[mcp_servers.demo_cli]
command = 'C:\Program Files\Workshop\demo-cli.exe'
args = ["mcp"]
```
Add `--allow-writes` (Claude Code) or `"--allow-writes"` to `args` (Codex) to expose the tools that change data. Use `--data <file>` to point at a specific data file.

![stdio session](../images/09-mcp-stdio-session.png)

## What the agent sees
Every tool has a description written for models, a JSON schema, and annotations. `readOnlyHint: true` means it reads only. `destructiveHint: true` means it changes data, and well-behaved agents will ask you before calling it. Business-rule violations come back as readable results (e.g. `{"error": "WO-1005 is already completed."}`) so the agent can react.
