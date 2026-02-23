# Microsoft Agent Framework Analysis for Discord Sky

## Executive Summary

This document evaluates whether adopting the **Microsoft Agent Framework (MAF)** would benefit the Discord Sky bot.

A critical clarification: **MAF is not Semantic Kernel**. MAF is the official *successor* to both Semantic Kernel and AutoGen, announced in October 2025 and reaching **Release Candidate (1.0.0-rc1)** on February 19, 2026. In Microsoft's own words: *"Think of Microsoft Agent Framework as Semantic Kernel v2.0 (it's built by the same team!)"* — Semantic Kernel will receive critical bug fixes for at least one year after MAF reaches GA, but all new feature development is happening in MAF.

Crucially, **MAF targets .NET 8.0, .NET Standard 2.0, and .NET Framework 4.7.2** — there is no TFM blocker. Discord Sky can reference MAF packages today on its existing `net8.0` target. Combined with a significantly simpler API, native OpenAI Responses API support, and graph-based workflows, MAF merits serious consideration for the project's next evolution.

---

## Semantic Kernel vs. Microsoft Agent Framework: The Relationship

| | Semantic Kernel | Microsoft Agent Framework |
|---|---|---|
| **Status** | Legacy (maintenance mode) | Active development, RC as of Feb 2026 |
| **Positioning** | "v1.x" | "v2.0" — the successor |
| **GitHub** | `microsoft/semantic-kernel` | `microsoft/agent-framework` |
| **NuGet (.NET)** | `Microsoft.SemanticKernel.*` | `Microsoft.Agents.AI.*` |
| **PyPI (Python)** | `semantic-kernel` | `agent-framework` |
| **.NET TFM** | `net10.0+` (current main) | `net8.0` / `netstandard2.0` / `net472` |
| **Core abstractions** | `Kernel`, `KernelFunction`, `ChatCompletionAgent` | `AIAgent`, `AIFunctionFactory`, `ChatClientAgent` |
| **Message types** | `ChatMessageContent` (SK-specific) | `Microsoft.Extensions.AI` types (industry standard) |
| **Tool registration** | `[KernelFunction]` attribute + Plugin class + Kernel | `AIFunctionFactory.Create(method)` — one line |
| **Agent invocation** | `agent.InvokeAsync()` → `IAsyncEnumerable<AgentResponseItem>` | `agent.RunAsync()` → `AgentResponse` |
| **Workflows** | Experimental orchestration patterns | Graph-based workflows with streaming, checkpointing, human-in-the-loop |
| **New features** | Critical fixes only | All new development |
| **Support timeline** | At least 1 year after MAF GA | Long-term |

**Sources**: [Semantic Kernel and Microsoft Agent Framework](https://devblogs.microsoft.com/semantic-kernel/semantic-kernel-and-microsoft-agent-framework/) (Oct 2025), [Migrate to MAF RC](https://devblogs.microsoft.com/semantic-kernel/migrate-your-semantic-kernel-and-autogen-projects-to-microsoft-agent-framework-release-candidate/) (Feb 2026)

---

## Current Discord Sky Architecture

Discord Sky is a creative persona bot (~2,000 lines of C#) built on five focused components:

| Component | Responsibility |
|-----------|----------------|
| `DiscordBotService` | Listens for messages via Discord.NET, routes to command/ambient/direct-reply handlers |
| `ContextAggregator` | Gathers channel history, collects images for vision, walks reply chains up to configurable depth |
| `CreativeOrchestrator` | Builds system prompts per persona, resolves per-persona model overrides, manages reasoning token budgets, forces structured tool-call output |
| `OpenAiClient` | Direct HTTP client to OpenAI's **Responses API** (`v1/responses`), with retry logic and moderation |
| `SafetyFilter` | Rate limiting, ban-word scrubbing |

### Key Design Decisions Worth Noting

1. **Responses API, not Chat Completions**: The project targets OpenAI's `v1/responses` endpoint with custom `HttpClient` calls and manual JSON serialization.
2. **Forced tool calls**: Every response must come through the `send_discord_message` function call, enforced via `ToolChoice = { type: "function", name: "send_discord_message" }`.
3. **Custom vision pipeline**: `ContextAggregator` collects images from Discord attachments and inline URLs, filters by allowed hosts, and passes them as `input_image` content parts.
4. **Per-persona model routing**: `IntentModelOverrides` allows specific personas to use different models.
5. **Reasoning token budgets**: When reasoning models are active, the orchestrator triples the output token limit and configures `ReasoningEffort`/`ReasoningSummary` at the request level.

---

## What is Microsoft Agent Framework?

Microsoft Agent Framework is a comprehensive open-source framework for building, orchestrating, and deploying AI agents. It ships as a set of NuGet packages (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) and a PyPI package (`agent-framework`).

### Core Features

- **Unified agent type**: A single `AIAgent` / `ChatClientAgent` base type works with any provider (no more `ChatCompletionAgent` vs `OpenAIAssistantAgent` vs `AzureAIAgent` distinctions)
- **Simple agent creation**: Extension methods like `.AsAIAgent()` directly on provider SDK clients
- **Direct tool registration**: `AIFunctionFactory.Create(method)` — no attributes, no plugin classes, no kernel required
- **Graph-based workflows**: Sequential, concurrent, handoff, and group chat patterns with streaming, checkpointing, and human-in-the-loop support
- **Multi-provider support**: OpenAI, Azure OpenAI, GitHub Copilot, Anthropic Claude, AWS Bedrock, Ollama, Microsoft Foundry
- **Interoperability standards**: A2A (Agent-to-Agent protocol), AG-UI, MCP (Model Context Protocol)
- **Middleware system**: Extensible request/response processing pipelines
- **Built-in observability**: OpenTelemetry integration for distributed tracing
- **DevUI**: Interactive developer UI for agent development, testing, and debugging
- **Uses `Microsoft.Extensions.AI`**: The standard .NET AI abstraction layer, not SK-specific types

### Key Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.Agents.AI` | Core agent abstractions |
| `Microsoft.Agents.AI.OpenAI` | OpenAI + Azure OpenAI provider (uses official OpenAI .NET SDK) |
| `Microsoft.Agents.AI.Workflows` | Graph-based workflow engine |

---

## Why MAF Matters for Discord Sky

### 1. No .NET TFM Blocker

The single biggest advantage over Semantic Kernel: **MAF targets `net8.0`**. Discord Sky can add MAF packages today with zero infrastructure changes — no Dockerfile updates, no CI/CD changes, no Discord.NET compatibility risk.

```xml
<!-- Just add to DiscordSky.Bot.csproj -->
<PackageReference Include="Microsoft.Agents.AI.OpenAI" Version="1.0.0-rc1" />
```

### 2. Native OpenAI Responses API Support

MAF's OpenAI provider uses the official OpenAI .NET SDK under the hood—including `ResponsesClient`. The quickstart example literally uses `.GetResponsesClient()`:

```csharp
var agent = new OpenAIClient("<apikey>")
    .GetResponsesClient("gpt-4.1-mini")
    .AsAIAgent(name: "Sky", instructions: "You are a mischievous Discord companion.");

AgentResponse response = await agent.RunAsync("Write a roast about pineapple pizza");
Console.WriteLine(response.Text);
```

This means Discord Sky wouldn't be forced to switch from the Responses API to Chat Completions — a constraint that made Semantic Kernel's `ChatCompletionAgent` a poor fit.

### 3. Dramatically Simpler Tool Registration

The current `OpenAiTooling` class manually constructs JSON schemas. MAF eliminates this entirely:

```csharp
// Current Discord Sky: 50+ lines of manual schema construction
public static OpenAiTool CreateSendDiscordMessageTool()
{
    var schema = new { type = "object", properties = new { mode = new { ... }, text = new { ... } } };
    return new OpenAiTool { Type = "function", Name = "send_discord_message", Parameters = schema };
}

// MAF: One line per tool, schema auto-generated from method signature
var tools = new[]
{
    AIFunctionFactory.Create(SendDiscordMessage, "send_discord_message",
        "Send a Discord message, optionally replying to a specific message.")
};

var agent = responsesClient.AsAIAgent(
    name: "Sky",
    instructions: systemPrompt,
    tools: tools);

// The tool method itself — no attributes required
static string SendDiscordMessage(
    [Description("'reply' or 'broadcast'")] string mode,
    [Description("Message text")] string text,
    [Description("Target message ID")] string? target_message_id = null)
{
    return $"mode={mode}, text={text}, target={target_message_id}";
}
```

This eliminates `OpenAiTooling`, `OpenAiResponseParser`, and most of the manual serialization code in `OpenAiClient`.

### 4. Simpler Invocation Pattern

```csharp
// Current: Build request → serialize → HTTP POST → deserialize → parse tool call
var completion = await _openAiClient.CreateResponseAsync(responseRequest, cancellationToken);
if (!OpenAiResponseParser.TryParseSendDiscordMessageCall(completion, out var toolCall))
    // handle fallback...

// MAF: One call, response includes structured tool results
AgentResponse response = await agent.RunAsync(userContent, session);
string resultText = response.Text;
```

The framework handles the tool-call loop internally: it sends the request, receives the tool call, marshals the arguments to your method, calls it, and returns the final response. All of the parsing logic in `OpenAiResponseParser` becomes unnecessary.

### 5. Multi-Provider Support Without Code Changes

If you ever need Azure OpenAI, Claude, or a local model:

```csharp
// OpenAI direct
var agent = new OpenAIClient(apiKey).GetResponsesClient("gpt-4.1-mini").AsAIAgent(...);

// Azure OpenAI
var agent = new OpenAIClient(bearerPolicy, azureOptions).GetResponsesClient("gpt-4.1").AsAIAgent(...);

// Any IChatClient implementation (Ollama, Claude via Agent Framework Claude SDK, etc.)
var agent = new ChatClientAgent(chatClient, instructions: "...", name: "Sky");
```

The `AIAgent` abstraction is backed by `IChatClient` from `Microsoft.Extensions.AI`, which is the emerging standard interface for .NET AI services — not an SK-specific type.

### 6. Graph-Based Workflows

MAF replaces SK's experimental orchestration patterns with a more mature graph-based workflow engine. Potential use in Discord Sky:

```csharp
// Sequential: Creative agent writes → Safety agent reviews
var writer = chatClient.AsAIAgent(instructions: personaPrompt, name: "creative");
var reviewer = chatClient.AsAIAgent(instructions: "Review for safety", name: "safety");

Workflow workflow = AgentWorkflowBuilder.BuildSequential(writer, reviewer);

List<ChatMessage> messages = [new(ChatRole.User, userContent)];
await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages);
```

This is more compelling than SK's orchestration patterns because:
- It's graph-based (not limited to predefined patterns)
- It supports streaming natively
- It has checkpointing and human-in-the-loop built in
- It's part of the RC release (not experimental)

### 7. Middleware System

MAF has a middleware pipeline for request/response processing — useful for cross-cutting concerns like logging, rate limiting, or content filtering:

```csharp
// Middleware could replace SafetyFilter's scrubbing logic
var agent = chatClient.AsAIAgent(
    instructions: "...",
    middleware: [new BanWordScrubbingMiddleware(chaosSettings)]);
```

This is a natural fit for the safety filter logic that currently lives as a separate service.

---

## What MAF Doesn't Solve

### 1. Custom Context Aggregation

`ContextAggregator` is deeply Discord-specific: it fetches messages via Discord.NET's `IAsyncEnumerable`, walks reply chains, collects images from attachments and inline URLs, filters by allowed hosts, and formats metadata for the model. No framework can replace this — it would survive any migration intact.

### 2. Per-Persona Model Routing

`IntentModelOverrides` lets different personas use different models. With MAF, you'd need to create separate `AIAgent` instances per model or resolve the correct `ResponsesClient` dynamically. This is achievable but requires explicit wiring that the framework doesn't automate.

### 3. Reasoning Token Budget Logic

The custom logic that triples output tokens for reasoning models would need to be implemented via `AgentRunOptions` / `ChatClientAgentRunOptions`:

```csharp
var options = new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = maxOutputTokens });
var response = await agent.RunAsync(userContent, session, options);
```

The calculation logic stays in your code — MAF just provides a cleaner way to pass it to the API.

### 4. Forced Tool Choice (Validated)

Discord Sky requires the model to always use the `send_discord_message` tool. MAF supports this through `ChatToolMode.RequireSpecific("send_discord_message")` in `Microsoft.Extensions.AI.ChatOptions.ToolMode`, which maps to `tool_choice: { type: "function", name: "send_discord_message" }` at the API level.

However, there's a critical interaction with MAF's auto-invoke loop: MAF's `ChatClientAgent` uses `FunctionInvokingChatClient` internally, which automatically invokes tool functions and loops back to the model. With `RequireSpecific`, the model is forced to call the tool on every response — including after receiving tool results — creating an infinite loop. This is a [known issue](https://github.com/microsoft/agent-framework/issues/2879) with three documented workarounds:

1. **`AIFunctionDeclaration` (recommended)**: Define the tool as a schema-only declaration (not an invocable `AIFunction`). `FunctionInvokingChatClient` will NOT auto-invoke it and will pass the `FunctionCallContent` back to the caller. This preserves the current flow: one API call, extract args from the response, no loop.
2. **`MaximumIterationsPerRequest = 1`**: Limit the auto-invoke loop to one round-trip, then stop. This invokes the function once but adds an extra API call.
3. **Middleware**: Flip `ToolMode` to `Auto` after the first tool invocation.

The `AIFunctionDeclaration` approach is ideal for Discord Sky because `send_discord_message` isn't a function that returns a result to the model — it's a structured output mechanism where the bot extracts the arguments and acts on them externally.

---

## What a Migration to MAF Would Look Like

### Validated Prerequisites

All three critical capabilities have been confirmed:

1. **Forced tool choice** — ✅ Supported via `ChatToolMode.RequireSpecific("send_discord_message")`. Use `AIFunctionDeclaration` to avoid the auto-invoke loop ([issue #2879](https://github.com/microsoft/agent-framework/issues/2879)).
2. **Vision inputs** — ✅ Supported via `UriContent` in `ChatMessage` ([multimodal docs](https://learn.microsoft.com/en-us/agent-framework/agents/multimodal)). Images passed as `new UriContent(url, "image/jpeg")` alongside `TextContent`.
3. **Reasoning configuration** — ✅ `ChatOptions.Reasoning` property exists in `Microsoft.Extensions.AI.ChatOptions`, passed through via `ChatClientAgentRunOptions`.

### Migration Steps

1. Add package: `Microsoft.Agents.AI.OpenAI` (prerelease)
2. Create an `OpenAIClient` → `ResponsesClient` in DI setup
3. Define `send_discord_message` as an `AIFunctionDeclaration` (schema only, no implementation) to avoid the auto-invoke loop while preserving forced tool choice via `RequireSpecific`
4. Replace `OpenAiClient.CreateResponseAsync()` → `agent.RunAsync()`, extract `FunctionCallContent` from the response
5. **Delete** `OpenAiResponseParser` — extract tool call args directly from `FunctionCallContent.Arguments`
6. **Delete** most of `OpenAiClient` (MAF handles HTTP, retry via the OpenAI SDK)
7. Keep `ContextAggregator` as-is — translate its output to the agent's input format
8. Keep `SafetyFilter` as-is, or explore MAF middleware as a replacement
9. Handle per-persona model routing by creating agents per model or using a factory pattern
10. Test all three invocation paths: command, ambient, direct reply

### What Gets Deleted

| File | Fate |
|------|------|
| `OpenAiClient.cs` | **Delete** — replaced by MAF's provider |
| `IOpenAiClient.cs` | **Delete** |
| `OpenAiTooling.cs` | **Delete** — replaced by `AIFunctionFactory` |
| `OpenAiResponseParser.cs` | **Delete** — MAF handles parsing |
| `OpenAiChatModels.cs` | **Mostly delete** — MAF + OpenAI SDK types replace request/response models |
| `CreativeOrchestrator.cs` | **Refactor** — simplifies significantly with MAF agent invocation |

### What Survives

| File | Fate |
|------|------|
| `DiscordBotService.cs` | Untouched |
| `ContextAggregator.cs` | Untouched |
| `SafetyFilter.cs` | Untouched (or converted to middleware) |
| `BotOptions.cs` | Untouched |
| `ChaosSettings.cs` | Untouched |
| `OpenAIOptions.cs` | Simplified (MAF handles endpoint/auth) |
| `CreativeModels.cs` | Untouched |
| `PromptRepository.cs` | Untouched |

**Estimated effort**: 3–5 days for the migration, plus 2–3 days for thorough testing. No TFM upgrade, no infrastructure changes.

---

## Risk Assessment

### MAF is at Release Candidate, Not GA

The API surface is declared stable and feature-complete for 1.0, but it hasn't shipped GA yet. From the RC blog post: *"the API surface is stable, and all features that we intend to release with version 1.0 are complete."*

**Risk**: Minor API adjustments may still happen between RC and GA. The team is explicitly asking for feedback before final release.

**Mitigation**: RC is specifically designed for production evaluation. The risk of breaking changes between RC and GA is significantly lower than building on SK's experimental orchestration patterns. The migration from RC to GA should be minimal.

### Semantic Kernel Being Sunset

SK will receive critical fixes for "at least one year" after MAF GA. If you stay on the current raw-API approach, this doesn't affect you. But if you were ever going to adopt an abstraction layer, MAF is the one to pick — not SK, which is now in maintenance mode.

### Community and Ecosystem Maturity

MAF is 10 months old (first commit ~April 2025), with 7.4k GitHub stars, 108 contributors, and 58 releases. It has the same core team as Semantic Kernel (Dmytro Struk, Stephen Toub, etc.) and Microsoft's full backing. It already has integrations with Claude Agent SDK, GitHub Copilot SDK, and Azure Functions.

---

## Recommendation

**MAF is worth evaluating for Discord Sky's next development phase.** Unlike the earlier Semantic Kernel analysis, there are no hard blockers:

1. **No TFM barrier** — MAF targets `net8.0`, matching the project exactly
2. **Native Responses API support** — no forced API switch
3. **Simpler, not more complex** — MAF's API is leaner than both SK and the current raw approach
4. **The right framework to invest in** — SK is maintenance-mode; MAF is the future

### Recommended Approach

1. **Short-term (now)**: All three prerequisites have been validated. Migrate `OpenAiClient` + `OpenAiTooling` + `OpenAiResponseParser` → MAF. Use `AIFunctionDeclaration` for forced tool choice without auto-invoke loop, `UriContent` for vision, and `ChatOptions.Reasoning` for reasoning configuration. This deletes ~500 lines of manual HTTP/serialization/parsing code and replaces them with ~50 lines of MAF setup. Keep `ContextAggregator`, `SafetyFilter`, and `DiscordBotService` untouched.
2. **Integration test**: Verify end-to-end behavior across all three invocation paths (command, ambient, direct reply) with the MAF-based pipeline.
3. **Future**: Once stable on MAF, explore graph-based workflows for a sequential "creative agent → safety review agent" pipeline, and middleware for content filtering.

### What Not to Do

- **Do not adopt Semantic Kernel.** It requires .NET 10, is in maintenance mode, and all new development is in MAF.
- **Do not wait indefinitely.** MAF is at RC with a stable API surface — this is the right time to evaluate.

---

## References

- [Microsoft Agent Framework Documentation](https://learn.microsoft.com/en-us/agent-framework/)
- [GitHub: microsoft/agent-framework](https://github.com/microsoft/agent-framework)
- [Semantic Kernel and Microsoft Agent Framework (blog)](https://devblogs.microsoft.com/semantic-kernel/semantic-kernel-and-microsoft-agent-framework/) — relationship explained
- [Migrate SK/AutoGen to MAF RC (blog)](https://devblogs.microsoft.com/semantic-kernel/migrate-your-semantic-kernel-and-autogen-projects-to-microsoft-agent-framework-release-candidate/) — migration guide
- [MAF Migration Guide from SK](https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-semantic-kernel)
- [NuGet: Microsoft.Agents.AI](https://www.nuget.org/packages/Microsoft.Agents.AI/) — targets net8.0/netstandard2.0/net472
- [NuGet: Microsoft.Agents.AI.OpenAI](https://www.nuget.org/packages/Microsoft.Agents.AI.OpenAI/) — OpenAI provider
- [MAF Running Agents (docs)](https://learn.microsoft.com/en-us/agent-framework/agents/running-agents) — AgentRunOptions, ChatClientAgentRunOptions, response types
- [MAF Tools Overview (docs)](https://learn.microsoft.com/en-us/agent-framework/agents/tools/) — function tools, tool approval, provider support matrix
- [MAF Multimodal (docs)](https://learn.microsoft.com/en-us/agent-framework/agents/multimodal) — vision/image input via UriContent
- [ChatToolMode.RequireSpecific (API ref)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chattoolmode) — forced tool choice
- [FunctionInvokingChatClient (API ref)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient) — MaximumIterationsPerRequest, AIFunctionDeclaration handling
- [GitHub Issue #2879: Excessive tool calls with tool_choice="required"](https://github.com/microsoft/agent-framework/issues/2879) — confirmed behavior + workarounds
- [Build AI Agents with Claude Agent SDK and MAF](https://devblogs.microsoft.com/semantic-kernel/build-ai-agents-with-claude-agent-sdk-and-microsoft-agent-framework/)
- [Build AI Agents with GitHub Copilot SDK and MAF](https://devblogs.microsoft.com/semantic-kernel/build-ai-agents-with-github-copilot-sdk-and-microsoft-agent-framework/)
- [OpenAI Responses API](https://platform.openai.com/docs/api-reference/responses)
