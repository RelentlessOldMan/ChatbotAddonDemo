# ADR-001: Expose AI through a capability catalog, not the UI

**Status:** Accepted

## Context
We want two AI integrations: an in-app chat assistant (desktop app) and coding agents over MCP (console tool). The legacy tool's logic is reached through UI event handlers and console commands.

## Options
1. **Let the model drive the UI** (screen reading, UI automation). No refactoring, but brittle, slow, hard to test, and unusable by agents or from the console tool.
2. **Write tools separately for each front end** (chat tools here, MCP tools there). Works, but duplicates descriptions, schemas and safety decisions, which then drift apart.
3. **One capability catalog** over the domain logic, adapted to each front end.

## Decision
Option 3. The tools for AI (capability catalog) live in `Demo.Capabilities`. Each one is declared once: name, description written for models, typed parameters (which become the JSON schema), and a safety class:

| Safety class | Meaning | Chat panel | MCP |
|---|---|---|---|
| `Read` | Only looks | Runs automatically (but a lookup asked for in the same step as a change also gets an approval card) | Always exposed; `readOnlyHint`, `idempotentHint` |
| `Write` | Creates or changes files (plans, reports) | Needs approval | Only with `--allow-writes`; `generate_report` also `idempotentHint` |
| `Execute` | Acts on the outside world (here, the bench hardware) | Needs approval | Only with `--allow-runs`; `openWorldHint`, and `destructiveHint` for `cancel_test_run` |

The catalog produces `AIFunction`s. The chat panel uses them directly, and the console tool's MCP server wraps the same objects with `McpServerTool.Create`.

The existing UI and console commands keep calling the domain logic directly; the catalog is the *AI-facing* adapter, not a new layer that people's paths must go through.

## Consequences
- Adding a capability exposes it to the chat panel and to agents at once, and one test suite covers it.
- The refactoring cost is real in a legacy codebase: the logic must be callable without the UI. That's the main work item when applying this pattern to a real product, and it pays off for testability on its own.
- Capability descriptions are prompts. Review them like UI copy.
