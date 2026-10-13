# ADR-005: No chat interface in the console app

**Status:** Accepted

## Context
Should the console tool get a chatbot like the WPF app?

## Decision
No. Console users automate; their AI integration is an agent *driving* the tool via MCP. A chat REPL would be a worse copy of Claude Code or Codex, and it would need a terminal credential UX for a persona (non-technical users) who doesn't use the console app.

## Consequences
- The console app needs no AI credentials at all.
- If demand appears, a one-shot `demo-cli ask "..."` can reuse `Demo.Ai` (`ChatSession`, credential store) with little new code.
