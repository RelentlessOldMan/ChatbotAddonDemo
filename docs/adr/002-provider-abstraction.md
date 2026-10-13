# ADR-002: Use Microsoft.Extensions.AI `IChatClient`; ship Anthropic and OpenAI

**Status:** Accepted

## Context
Customers have existing relationships with different AI vendors. We need at least Claude and OpenAI, and don't want vendor code spread across the app.

## Options
1. Call each vendor SDK directly from the chat feature. Full feature access, duplicated loop logic.
2. Hand-rolled `IChatModel` interface. Ours to maintain; reinvents tool-calling plumbing.
3. **`Microsoft.Extensions.AI.IChatClient`**: the .NET standard abstraction. The official Anthropic (`AsIChatClient`) and OpenAI (`Microsoft.Extensions.AI.OpenAI`) SDKs both provide adapters, and M.E.AI supplies `FunctionInvokingChatClient` (the tool loop) and `ApprovalRequiredAIFunction` (human approval).

## Decision
Option 3. `ChatClientFactory` builds an `IChatClient` from the provider, the model name and the resolved credential (a key, or a gateway URL and token). The rest of the app sees only `IChatClient`.

## Consequences
- Adding a provider is one `switch` arm.
- The common denominator hides some vendor features. Where we need them (e.g. Anthropic prompt caching or effort), we set them through `ChatOptions.RawRepresentationFactory` inside the provider-specific factory code.
- Default models: `claude-opus-5-5` (Anthropic) and `gpt-6-astra` (OpenAI). Both can be changed in Settings because model names change faster than our release cycle.
- OpenAI goes through the **Responses API** (`GetResponsesClient().AsIChatClient(model)`), not Chat Completions: current models return HTTP 400 for function tools with reasoning on `/v1/chat/completions`. Response storage is turned off (`store: false`) so customer data isn't kept on OpenAI's side, which means the whole conversation is sent with each request, the same as with Anthropic.
