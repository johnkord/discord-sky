# Multi-Provider LLM Design: xAI Grok + OpenAI

## Executive Summary

This document presents findings and a design for making Discord Sky's LLM backend **swappable between OpenAI and xAI (Grok) at configuration time**, with no code changes required. The approach uses the `Microsoft.Extensions.AI` `IChatClient` abstraction already in use, combined with the OpenAI .NET SDK's support for custom endpoints, to route requests to either provider based on `appsettings.json`.

---

## Research Findings

### 1. xAI API Compatibility with the OpenAI .NET SDK

**Key finding: xAI's API is fully compatible with the OpenAI .NET SDK.** xAI explicitly states their APIs are "fully compatible with the OpenAI SDK." The `/v1/chat/completions` endpoint uses identical request/response schemas.

xAI also supports `/v1/responses` (the newer Responses API), but with important caveats:
- The `instructions` parameter is **not supported** and returns an error if specified
- Some parameters like `frequency_penalty` and `presence_penalty` are marked "NOT SUPPORTED in Responses API"
- It's designed primarily for their native xAI SDK and stateful conversation features

**Implication for Discord Sky**: The project currently uses `IChatClient` via `OpenAIClient.GetChatClient().AsIChatClient()`, which goes through OpenAI's **Chat Completions** path. This is the most compatible path for xAI because:
1. xAI fully supports `/v1/chat/completions`
2. The OpenAI .NET SDK allows overriding the endpoint URI
3. `IChatClient` abstracts the details — the orchestrator doesn't care which provider is behind it

### 2. xAI Feature Support Matrix (from API docs + model screenshot)

| Feature | xAI Support | Notes |
|---------|-------------|-------|
| Chat Completions | ✅ Full | Identical to OpenAI's `/v1/chat/completions` |
| Function/Tool Calling | ✅ Full | `tool_choice: "auto"`, `"required"`, `"none"`, `{"type":"function","function":{"name":"..."}}` all supported |
| Forced Tool Choice | ✅ Full | `{"type": "function", "function": {"name": "send_discord_message"}}` works identically |
| Image/Vision Input | ✅ Partial | Supported by `grok-4-1-fast-reasoning`, `grok-4-1-fast-non-reasoning`, `grok-code-fast-1`, `grok-4-fast-reasoning`, `grok-4-fast-non-reasoning`, `grok-4-0709`, `grok-2-vision-1212`. Not all models support vision. |
| Parallel Tool Calls | ✅ Full | Enabled by default, can be disabled with `parallel_tool_calls: false` |
| Streaming | ✅ Full | Supported on chat completions |
| Reasoning Tokens | ✅ Partial | Grok-4 is always-reasoning (no `reasoning_effort` parameter). Grok-4.1-fast models support `reasoning_effort`. Grok-3-mini and older support it. |
| Structured Outputs | ✅ Full | JSON mode supported |
| Max Output Tokens | ✅ Full | Standard parameter |

### 3. Grok Model Capabilities (from screenshot)

| Model | Vision | Tool Calling | Reasoning | Context | Cost (in/out per 1M tokens) | Best For |
|-------|--------|-------------|-----------|---------|-----|---------|
| `grok-4-1-fast-reasoning` | ✅ | ✅ | Always on | 2M | $0.20 / $0.50 | **Recommended primary** — fast, cheap, reasoning, vision, huge context |
| `grok-4-1-fast-non-reasoning` | ✅ | ✅ | No | 2M | $0.20 / $0.50 | Fast non-reasoning tasks |
| `grok-code-fast-1` | ✅ | ✅ | ✅ | 256K | $0.20 / $1.50 | Code-focused tasks |
| `grok-4-fast-reasoning` | ✅ | ✅ | Always on | 2M | $0.20 / $0.50 | Slightly older fast variant |
| `grok-4-fast-non-reasoning` | ✅ | ✅ | No | 2M | $0.20 / $0.50 | Non-reasoning fast |
| `grok-4-0709` | — | ✅ | Always on | 256K | $3.00 / $15.00 | Premium quality (expensive) |
| `grok-3-mini` | — | ✅ | ✅ | 131K | $0.30 / $0.50 | Budget option |
| `grok-3` | — | ✅ | — | 131K | $3.00 / $15.00 | Older premium |
| `grok-2-vision-1212` | ✅ | ✅ | — | 32K | $2.00 / $10.00 | Legacy vision |

### 4. Critical Grok-4 Gotcha

From xAI's docs:
> **Grok 4 is a reasoning model. There is no non-reasoning mode when using Grok 4.**
> `presencePenalty`, `frequencyPenalty` and `stop` parameters are not supported by reasoning models. Adding them in the request would result in an error.
> **Grok 4 does not have a `reasoning_effort` parameter.** If a `reasoning_effort` is provided, the request will return an error.

This means:
- `grok-4-0709` always reasons and **will error** if `reasoning_effort` is set
- `grok-4-1-fast-reasoning` / `grok-4-fast-reasoning` models DO support `reasoning_effort`
- The bot's current reasoning configuration must be **provider-aware** — reasoning settings should only be sent when the provider/model supports them

**Implementation impact**: We need per-provider reasoning configuration, or smarter conditional logic that doesn't send `reasoning_effort` for models that don't support it.

---

## Alternatives Considered

### Alternative 1: Separate IChatClient per provider, route at request time

Register both an OpenAI `IChatClient` and an xAI `IChatClient`, then choose at invocation time based on which model is requested.

**Pros**: Maximum flexibility — could route different personas to different providers.
**Cons**: Adds complexity to DI, requires the orchestrator to know about providers, breaks the clean single-IChatClient abstraction. Per-request `ModelId` on `ChatOptions` already works for switching models within a single provider.

### Alternative 2: Factory pattern with named clients

Register a `Func<string, IChatClient>` or `IChatClientFactory` that resolves by provider name.

**Pros**: Clean abstraction, supports N providers.
**Cons**: Over-engineered for two providers. Adds an interface and factory class. The orchestrator would need to know which provider to request.

### Alternative 3 (Chosen): Single IChatClient, config-driven provider selection

A single `IChatClient` is registered at startup, configured by `appsettings.json` to point at either OpenAI or xAI. The `OpenAIClient` constructor accepts a custom endpoint URI, making xAI a drop-in replacement.

**Pros**:
- Zero orchestrator changes — the entire swap happens in DI/config
- No new abstractions or interfaces
- `ChatOptions.ModelId` still works for per-persona model overrides (they just need to be models available on the active provider)
- Simple to understand and maintain
- Easily extensible: any OpenAI-compatible provider (Together AI, Groq, Perplexity, etc.) works the same way

**Cons**:
- Can only use one provider at a time (per deployment)
- Can't mix OpenAI and xAI models in the same instance

**Why this is the right choice**: Discord Sky uses a single `IChatClient` injected into `CreativeOrchestrator`. The orchestrator uses `ChatOptions.ModelId` for per-persona routing — this works identically against xAI since the pattern is "set model name in options, let the SDK handle it." Swapping the entire provider at config time is the simplest change with the largest impact.

### Alternative 4: Microsoft Agent Framework migration + provider swap

Migrate to MAF first (as analyzed in `microsoft_agent_framework_analysis.md`), then leverage MAF's multi-provider `ChatClientAgent`.

**Pros**: Gets the MAF benefits, multi-provider is built in.
**Cons**: Much larger migration (3-5 days). The provider swap can be done independently and immediately in <1 day. Nothing prevents doing the MAF migration later.

---

## Chosen Design: Config-Driven Provider Selection

### Configuration Schema

```json
{
  "LLM": {
    "Provider": "openai",
    "OpenAI": {
      "ApiKey": "sk-proj-...",
      "ChatModel": "gpt-5.2",
      "MaxTokens": 1200,
      "IntentModelOverrides": {},
      "ReasoningEffort": "medium"
    },
    "xAI": {
      "ApiKey": "xai-...",
      "ChatModel": "grok-4-1-fast-reasoning",
      "MaxTokens": 1200,
      "IntentModelOverrides": {},
      "ReasoningEffort": "medium"
    }
  }
}
```

The `Provider` field selects which block to use. Both blocks share the same schema (`LlmProviderOptions`). This means:
- Switching providers = change one string value
- Both configs can coexist in the file for easy toggling
- Per-provider model overrides and reasoning settings are independent

### How It Works

1. **Startup**: `Program.cs` reads `LLM:Provider` to determine which provider config block to bind
2. **IChatClient creation**: An `OpenAIClient` is created with either the default OpenAI endpoint or xAI's `https://api.x.ai/v1` endpoint, depending on provider
3. **Options binding**: The active provider's config is bound to `LlmOptions` (renamed from `OpenAIOptions`), which the orchestrator consumes via `IOptionsMonitor<LlmOptions>`
4. **No orchestrator changes**: `CreativeOrchestrator` continues using `IChatClient` + `ChatOptions.ModelId` exactly as before

### Reasoning Effort Handling

Different providers handle reasoning differently:
- **OpenAI**: `reasoning_effort` supported on reasoning models (o-series, etc.)
- **xAI `grok-4-0709`**: Always reasons, **errors** if `reasoning_effort` is set
- **xAI `grok-4-1-fast-reasoning`**: Supports `reasoning_effort`
- **xAI `grok-4-1-fast-non-reasoning`**: No reasoning at all

The design handles this by:
1. Keeping `ReasoningEffort` in the provider config (optional)
2. The orchestrator already checks `!string.IsNullOrWhiteSpace(openAiOpts.ReasoningEffort)` before setting reasoning options
3. For `grok-4-0709`, simply leave `ReasoningEffort` blank/null in config — the orchestrator won't set it
4. For `grok-4-1-fast-reasoning`, set `ReasoningEffort` to `"medium"` or whatever is desired

### Provider-Specific Model Recommendations

| Use Case | OpenAI Model | xAI Model |
|----------|-------------|-----------|
| Primary chat | `gpt-5.2` | `grok-4-1-fast-reasoning` |
| Memory extraction (cheap) | `gpt-5.2` | `grok-4-1-fast-non-reasoning` |
| Premium quality | `gpt-5.2` | `grok-4-0709` |
| Budget | `gpt-4.1-mini` | `grok-3-mini` |
| Vision required | `gpt-5.2` | `grok-4-1-fast-reasoning` |

### What Changes

| File | Change |
|------|--------|
| `Configuration/OpenAIOptions.cs` | Rename to `LlmOptions.cs`, add `Provider` and `Endpoint` fields |
| `Program.cs` | Config-driven `IChatClient` creation with endpoint override |
| `appsettings.json` | New `LLM` section schema |
| `appsettings.Development.json` | New `LLM` section with both providers |
| `CreativeOrchestrator.cs` | Update options type from `OpenAIOptions` → `LlmOptions` |
| `BotOptions.cs` | `MemoryExtractionModel` stays (it's just a model name, works with any provider) |

### What Does NOT Change

- `CreativeOrchestrator` logic, prompts, tool handling
- `ContextAggregator`, `SafetyFilter`, `DiscordBotService`
- `IChatClient` interface usage anywhere
- Memory extraction pipeline
- All test files (they mock `IChatClient`)

---

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| xAI API minor incompatibilities | Medium | The OpenAI .NET SDK is battle-tested against OpenAI-compatible APIs. xAI explicitly claims compatibility. If issues arise, they'd manifest as HTTP errors that the existing retry logic handles. |
| Grok model doesn't follow tool schema as reliably | Medium | The orchestrator already has fallback logic for missing/malformed tool calls. Different models may need prompt tuning. Per-provider `IntentModelOverrides` allows model-specific adjustments. |
| xAI rate limits (480 RPM on most models) | Low | Current throttle (`SemaphoreSlim(3)`) + circuit breaker already handle this. xAI's 480 RPM >> expected Discord bot traffic. |
| Reasoning token budget mismatch | Low | Config-driven per-provider — set appropriate `MaxTokens` and `ReasoningEffort` per provider. |
| `grok-4-0709` errors on `reasoning_effort` | High if misconfigured | Don't set `ReasoningEffort` in xAI config when using `grok-4-0709`. Document this clearly. |

---

## References

- [xAI API Reference — Chat Completions](https://docs.x.ai/developers/rest-api-reference/inference/chat)
- [xAI API Reference — Responses API](https://docs.x.ai/developers/rest-api-reference/inference/chat#create-new-response)
- [xAI Function Calling](https://docs.x.ai/developers/tools/function-calling)
- [xAI Models and Pricing](https://docs.x.ai/developers/models)
- [xAI Quickstart — OpenAI SDK Compatibility](https://docs.x.ai/developers/quickstart#step-4-make-a-request-from-python-or-javascript)
- [OpenAI .NET SDK — Custom Endpoint](https://github.com/openai/openai-dotnet#custom-endpoint)
- [Microsoft.Extensions.AI — IChatClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.ichatclient)
