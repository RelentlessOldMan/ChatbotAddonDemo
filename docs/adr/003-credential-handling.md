# ADR-003: Customer-supplied credentials, DPAPI at rest, gateway option for enterprises

**Status:** Accepted

## Context
The app runs on customer desktops and the customer pays for tokens, so we must not ship our own key. Customers range from individuals to enterprises with central IT.

## Decision
Credential sources, in precedence order:
1. **Machine policy / gateway:** `HKLM\Software\Policies\ChatbotAddonDemo` (or env vars) sets a base URL and optional token. Requests go to the customer's own LLM gateway, which holds the vendor key and enforces budgets and logging.
2. **Environment variables:** `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`.
3. **User-entered key:** stored encrypted with `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` under `%LOCALAPPDATA%\ChatbotAddonDemo\`.

Keys are never written to plaintext settings or logs, and they are masked in the UI after entry. A "Test connection" check runs before the key is saved.

## Alternatives rejected
- **Vendor key embedded in our binary:** it can be extracted, and we would pay for every customer's usage.
- **Plain JSON/INI config:** keys get copied around, backed up and committed.
- **Windows Credential Manager:** a fine alternative to DPAPI files with the same protection. We chose DPAPI files because they are simpler to demo and test. Swapping is local to `ICredentialStore`.
- **OAuth "sign in with vendor":** not generally available to third-party desktop apps today. Revisit later.

## Consequences
- DPAPI CurrentUser protects against other users and offline disk theft, not against malware running as that same user. That's the same bar as browsers' saved passwords, and we document it.
- The gateway path is the strongest option, and we recommend it to enterprise customers.
