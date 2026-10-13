# IT admin guide: deploying the AI features

## Recommended: route through your own AI gateway
Point the app at a gateway you operate, for example LiteLLM, Azure API Management, or a provider's enterprise proxy. The gateway holds the real vendor key and enforces budgets, logging, DLP and model allow-lists. Desktops then hold **no long-lived vendor key**.

Set machine policy (Group Policy Preferences or Intune) under `HKLM\Software\Policies\ChatbotAddonDemo`:

| Key | Value | Type | Meaning |
|---|---|---|---|
| `Anthropic\GatewayUrl` | `https://llm.corp.example/anthropic` | REG_SZ | Base URL replacing `api.anthropic.com` |
| `Anthropic\GatewayToken` | *(optional)* | REG_SZ | Sent as the API key; omit if the gateway uses network/Windows identity |
| `OpenAI\GatewayUrl` | `https://llm.corp.example/openai/v1` | REG_SZ | Base URL replacing `api.openai.com/v1` |
| `OpenAI\GatewayToken` | *(optional)* | REG_SZ | As above |
| `DisableUserKeys` | `1` | REG_DWORD | Hide the "paste your own key" option; only the gateway may be used |

Precedence when the app chooses credentials: **gateway → environment variable (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY`) → user-entered key**. The Settings page always tells the user which one is in use.

## Alternative: environment variables
For scripted or VDI rollouts, set `ANTHROPIC_API_KEY` or `OPENAI_API_KEY` for the user. Treat these as secrets: anything running as that user can read them.

## Turning features off
- The assistant and agent access are both **off by default** and per-user.
- To keep the assistant from working at all, set `DisableUserKeys=1` and configure no gateway. The panel then shows "Connect an AI provider" and makes no network calls.

## Network
| Feature | Outbound | Inbound |
|---|---|---|
| Assistant | HTTPS to the provider or your gateway | none |
| Agent access (desktop) | none | TCP on `127.0.0.1` only (default port 47821) |
| Agent access (console) | none | none (stdio) |

See also [security notes](../security.md).
