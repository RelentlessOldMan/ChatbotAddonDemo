# ADR-001: Expose AI through a capability layer, not the UI

**Status:** Accepted

## Context
We want two AI integrations (in-app chat, external agents via MCP) on top of legacy tools whose logic currently lives behind UI event handlers and console commands.

## Options
1. **Drive the UI** (UI automation / screen reading by the model). No refactor, but brittle, slow, untestable, and unusable from the console app or external agents.
2. **Separate tool code per front end** (chat tools here, MCP tools there). Works, but duplicates descriptions, schemas and safety decisions, which drift apart.
3. **One capability catalog** over the domain services, adapted to each front end.

## Decision
Option 3. Each capability is declared once (name, description, typed parameters, `Read`/`Write` safety class) in `Demo.Capabilities` and converted to an `AIFunction`. The chat panel consumes `AIFunction`s directly; the MCP servers wrap the same objects with `McpServerTool.Create`.

## Consequences
- Adding a capability exposes it everywhere at once, and one test suite covers it.
- The refactor cost is real for a legacy codebase: logic must be callable without the UI. That's the main work item when applying this pattern to a real product, and it pays off for testability on its own.
- Capability descriptions are prompts. They need reviewing like UI copy.
