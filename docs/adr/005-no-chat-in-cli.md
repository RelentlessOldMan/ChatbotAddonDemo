# ADR-005: No chat interface in the console tool

**Status:** Accepted

## Context
Should the console tool get a chat assistant like the desktop app?

## Decision
No. Console users automate, so their AI is an agent *driving* the tool through `demo-cli mcp` ([ADR-004](004-mcp-via-console-tool.md)). A chat prompt inside the console tool would be a worse copy of Claude Code or Codex, and it would need a terminal credential setup for a persona (non-technical users) who doesn't use the console tool anyway.

## Consequences
- The console tool never calls a model and needs no AI credentials. It doesn't even reference `Demo.Ai`.
- If demand appears, a one-shot `demo-cli ask "..."` can reuse `Demo.Ai` (`ChatSession`, credential store) with little new code.
