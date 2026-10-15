# IT admin guide: deploying the AI features

## Recommended: route through your own AI gateway
Point the app at a gateway you operate, for example LiteLLM, Azure API Management, or a provider's enterprise proxy. The gateway holds the real vendor key and enforces budgets, logging, data-loss prevention (DLP) and model allow-lists. Desktops then hold **no long-lived vendor key**.

Set machine policy (Group Policy Preferences or Intune) under `HKLM\Software\Policies\ChatbotAddonDemo`:

| Key | Value | Type | Meaning |
|---|---|---|---|
| `Anthropic\GatewayUrl` | `https://llm.corp.example/anthropic` | REG_SZ | Base URL replacing `api.anthropic.com` |
| `Anthropic\GatewayToken` | *(optional)* | REG_SZ | Sent as the API key; omit if the gateway uses network/Windows identity. Any local user can read HKLM, so prefer identity-based gateways or treat this token as shared by everyone on the machine. |
| `OpenAI\GatewayUrl` | `https://llm.corp.example/openai/v1` | REG_SZ | Base URL replacing `api.openai.com/v1` |
| `OpenAI\GatewayToken` | *(optional)* | REG_SZ | As above |
| `DisableUserKeys` | `1` | REG_DWORD | Hide the "paste your own key" option and ignore personal keys in `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`; only the gateway may be used |

Gateway URLs must use `https://` (plain `http://` is accepted only for this PC: localhost or 127.0.0.1), so a token is never sent in clear text. Any gateway value that is present but unusable (not https, or not a valid address) is reported in Settings, and the app never falls back to personal keys.

When no gateway is set, the app always uses the provider's official address (`api.anthropic.com` / `api.openai.com/v1`), so environment variables such as `ANTHROPIC_BASE_URL` or `OPENAI_BASE_URL` can't redirect users' keys somewhere else.

Precedence when the app chooses credentials: **gateway → environment variable (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY`) → user-entered key**. The Settings page always tells the user which one is in use.

## Alternative: environment variables
For scripted or VDI rollouts, set `ANTHROPIC_API_KEY` or `OPENAI_API_KEY` for the user. Treat these as secrets: anything running as that user can read them.

## Other MCP servers for the assistant
The desktop app's MCP hub can connect the assistant to other MCP servers, such as Perforce, GitHub or Microsoft 365, so it can use their tools too. Users can add their own in Settings ("Other MCP servers for the assistant"). You can add servers for everyone, and limit users to yours.

Add one subkey per server under `HKLM\Software\Policies\ChatbotAddonDemo\McpServers`:

| Key | Value | Type | Meaning |
|---|---|---|---|
| `McpServers\<name>\Command` | `"C:\Program Files\P4\p4mcp.exe" --readonly` or `https://mcp.corp.example/github` | REG_SZ | The command line that starts the server (quote a path with spaces), or its `https://` address |
| `McpServers\<name>\Trusted` | `1` | REG_DWORD | *(optional)* Its tools run without asking. Without it, every use of every tool asks the user first |
| `DisableUserMcpServers` | `1` | REG_DWORD | Users can't add servers of their own; only yours are used |

- `<name>` is the short name users see ("The assistant wants to use github") and the prefix of the tool names the model sees (`github_search_code`): up to 20 letters, digits or dashes. Your servers come first; a user's server with the same name is ignored, and Settings won't save one. Users see only the program or host of your servers, not the whole command line.
- Addresses must use `https://` (plain `http://` only for this PC: localhost or 127.0.0.1). An entry that can't be used is skipped, and the chat says why.
- **Approval:** tools from other servers ask the user before every use unless you set `Trusted=1`. The read-only labels such servers put on their own tools aren't trusted, and servers users add are never trusted.
- Each server is started or contacted when the user sends their first message. One that doesn't answer within 30 seconds, or fails, is reported in the chat, and the chat carries on without it.
- **Coding agents never see these servers.** Agents connected to the app get only TestBench's own tools.
- A command-line server runs as the user, with the user's rights, for as long as the chat panel is open. Only list servers you trust; consider `DisableUserMcpServers=1` where users shouldn't run their own.

## Coding agents
Coding agents (Claude Code, Codex…) can reach TestBench two ways ([guide](connecting-a-coding-agent.md)):

- **The open desktop app** (`demo-desktop.exe mcp`): off until the user ticks "Let coding agents connect to this window" in Settings. Every change the agent asks for (creating files, using the bench) waits for the user's approval in the window. The agent reaches the app through a named pipe restricted to that Windows user, with network access denied. To block it, set `DisableAgentConnections=1` (REG_DWORD) under `HKLM\Software\Policies\ChatbotAddonDemo`; the checkbox is then greyed out.
- **The console tool** (`demo-cli mcp`): only if a user configures their agent to start it. It is read-only unless `--allow-writes` and/or `--allow-runs` are added. To block it, don't deploy `demo-cli.exe` to those machines (or block it with AppLocker / WDAC).

## Turning features off
- The assistant is **off by default** and per-user.
- To keep the assistant from working at all, set `DisableUserKeys=1` and configure no gateway. The panel then shows "Connect an AI provider" and makes no network calls.
- Agent connections to the desktop app are off by default and per-user; `DisableAgentConnections=1` turns them off for everyone.
- `DisableUserMcpServers=1` limits the assistant to the MCP servers you set up (none, if you set up none).

## Network
| Feature | Outbound | Inbound |
|---|---|---|
| Assistant (desktop app) | HTTPS to the provider or your gateway | none |
| Other MCP servers (desktop app) | HTTPS to servers given by address; whatever a command-line server itself does | none |
| Coding agents, desktop app (`demo-desktop.exe mcp`) | none from our app (the agent's own traffic is the agent's business) | none: a named pipe for the current user only, network access denied |
| Coding agents, console tool (`demo-cli mcp`) | none from our tool | none: stdin/stdout only, no port |

**What leaves the PC with coding agents:** our apps never call a model for an agent. But the tool results (plan details, spec limits, run findings) enter the agent's context and go to the agent's AI provider, under the agent user's terms with that provider. Treat that the same as any other data your users give their coding agent.

**What leaves the PC with other MCP servers:** the arguments of each call go to that server. 💲 Its results join the chat, so they're sent to the chat's model with the next request, through your gateway if you have one.

See also [security notes](../security.md).
