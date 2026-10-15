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

Gateway URLs must use `https://` (plain `http://` is accepted only for `localhost`), so a token is never sent in clear text. Any gateway value that is present but unusable (not https, or not a valid address) is reported in Settings, and the app never falls back to personal keys.

When no gateway is set, the app always uses the provider's official address (`api.anthropic.com` / `api.openai.com/v1`), so environment variables such as `ANTHROPIC_BASE_URL` or `OPENAI_BASE_URL` can't redirect users' keys somewhere else.

Precedence when the app chooses credentials: **gateway → environment variable (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY`) → user-entered key**. The Settings page always tells the user which one is in use.

## Alternative: environment variables
For scripted or VDI rollouts, set `ANTHROPIC_API_KEY` or `OPENAI_API_KEY` for the user. Treat these as secrets: anything running as that user can read them.

## Turning features off
- The assistant is **off by default** and per-user.
- To keep the assistant from working at all, set `DisableUserKeys=1` and configure no gateway. The panel then shows "Connect an AI provider" and makes no network calls.
- Coding agents only reach TestBench if a user configures their agent to start `demo-cli mcp`. It is read-only unless `--allow-writes` and/or `--allow-runs` are added. To block agents entirely, don't deploy `demo-cli.exe` to those machines (or block it with AppLocker / WDAC).

## Network
| Feature | Outbound | Inbound |
|---|---|---|
| Assistant (desktop app) | HTTPS to the provider or your gateway | none |
| Coding agents (`demo-cli mcp`) | none from our tool (the agent's own traffic is the agent's business) | none: stdin/stdout only, no port |

**What leaves the PC with coding agents:** `demo-cli` itself never calls a model. But its tool results (plan details, spec limits, run findings) enter the agent's context and go to the agent's AI provider, under the agent user's terms with that provider. Treat that the same as any other data your users give their coding agent.

See also [security notes](../security.md).
