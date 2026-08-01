# Microsoft Agent Framework Analysis for Discord Sky

## Executive Summary

Revised 2026-07-28. The original version of this document was written when Discord Sky talked to
OpenAI through hand-rolled HTTP, and it recommended adopting MAF largely to delete that code. That
recommendation has been overtaken by events: the raw HTTP layer is gone, and the deletion benefit was
already captured by moving to `Microsoft.Extensions.AI` directly. This revision reassesses MAF against
the codebase as it actually is.

The short version: MAF is now used only by the unrestricted-autonomy subsystem. `DiscordSky.Bot.csproj`
pins `Microsoft.Agents.AI.OpenAI` 1.15.0, `Microsoft.Extensions.AI` 10.8.3, and MCP 1.4.1. The normal
reply path stays on `CreativeOrchestrator`'s existing hand-rolled loop.

That division is deliberate. A bot whose ordinary reply ends in a single terminal
`send_discord_message` call gains little from agent conversion. Unrestricted autonomy needs a real
agent that calls native MCP tools and records every mutation before it can execute. MAF's tool-approval
model supplies that suspend point even though approval is automatic.

MAF targets `net8.0`, so there is no TFM blocker. The upgrade preserves the existing reply behavior;
Sky's full pre-autonomy suite passed after the OpenAI 2.10 Responses-client construction update.

---

## Semantic Kernel vs. Microsoft Agent Framework: The Relationship

| | Semantic Kernel | Microsoft Agent Framework |
|---|---|---|
| **Status** | Maintenance-focused | Active development |
| **Positioning** | "v1.x" | "v2.0", the successor |
| **GitHub** | `microsoft/semantic-kernel` | `microsoft/agent-framework` |
| **NuGet (.NET)** | `Microsoft.SemanticKernel.*` | `Microsoft.Agents.AI.*` |
| **PyPI (Python)** | `semantic-kernel` | `agent-framework` |
| **.NET TFM** | `net8.0` / `netstandard2.0` | `net8.0` / `netstandard2.0` / `net472` |
| **Core abstractions** | `Kernel`, `KernelFunction`, `ChatCompletionAgent` | `AIAgent`, `AIFunctionFactory`, `ChatClientAgent` |
| **Message types** | `ChatMessageContent` (SK-specific) | `Microsoft.Extensions.AI` types (industry standard) |
| **Tool registration** | `[KernelFunction]` attribute + Plugin class + Kernel | `AIFunctionFactory.Create(method)`, one line |
| **Agent invocation** | `agent.InvokeAsync()` returns `IAsyncEnumerable<AgentResponseItem>` | `agent.RunAsync()` returns `AgentResponse` |
| **Workflows** | Experimental orchestration patterns | Graph-based workflows with streaming, checkpointing, human-in-the-loop |
| **New features** | Critical fixes only | All new development |
| **Support timeline** | At least 1 year after MAF GA | Long-term |

**Sources**: [Semantic Kernel and Microsoft Agent Framework](https://devblogs.microsoft.com/semantic-kernel/semantic-kernel-and-microsoft-agent-framework/) (Oct 2025), [Migrate to MAF RC](https://devblogs.microsoft.com/semantic-kernel/migrate-your-semantic-kernel-and-autogen-projects-to-microsoft-agent-framework-release-candidate/) (Feb 2026)

---

## Current Discord Sky Architecture

The architecture this document originally described no longer exists. `OpenAiClient`, `OpenAiTooling`,
`OpenAiResponseParser`, and `OpenAiChatModels` have all been removed. The relevant shape today:

| Component | Responsibility |
|-----------|----------------|
| `DiscordBotService` | Discord.Net gateway, routes to command, ambient, and direct-reply handlers |
| `ContextAggregator` | Channel history, vision images, reply-chain walking |
| `CreativeOrchestrator` | Prompt assembly, tool declarations, and a hand-rolled tool loop |
| `LlmChatClientFactory` | Builds a bare `IChatClient` from the OpenAI SDK |
| `SafetyFilter` | Rate limiting and ban-word scrubbing |
| `WorldAutonomyRouter` | Serializes one autonomous MAF run per exact configured guild |
| `StewardMcpSupervisor` | Starts one bound Steward stdio child and discovers its complete native catalog |
| `SqliteWorldAutonomyLedger` | Persists runs and write dispatches before native invocation |

### Key Design Decisions Worth Noting

1. **Already on `Microsoft.Extensions.AI`.** `LlmChatClientFactory` calls `GetResponsesClient().AsIChatClient(model)`
   and wraps it in a `TimeoutChatClient`. Tools are declared with `AIFunctionFactory.CreateDeclaration`
   and JSON Schema literals.
2. **No auto-invocation anywhere.** There is no `UseFunctionInvocation()`, no `ChatClientBuilder`
   pipeline, and no `FunctionInvokingChatClient`. `CreativeOrchestrator` runs its own `while (true)`,
   inspects `FunctionCallContent`, performs side effects itself, and appends `FunctionResultContent`.
3. **Declaration-only tools by design.** `send_discord_message` is a structured output mechanism, not a
   function with a return value, so a declaration that the loop interprets is the correct shape.
4. **Provider-native message structure is preserved.** The loop appends assistant messages exactly as
   returned rather than flattening contents, with a comment warning that flattening destroys reasoning
   items.
5. **Responses API by default.** `UseResponsesApi` is on, and per-request `ChatOptions.ModelId`
   overrides drive per-workload model routing.

---

## What is Microsoft Agent Framework?

Microsoft Agent Framework is a comprehensive open-source framework for building, orchestrating, and deploying AI agents. It ships as a set of NuGet packages (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) and a PyPI package (`agent-framework`).

### Core Features

- **Unified agent type**: A single `AIAgent` / `ChatClientAgent` base type works with any provider (no more `ChatCompletionAgent` vs `OpenAIAssistantAgent` vs `AzureAIAgent` distinctions)
- **Simple agent creation**: Extension methods like `.AsAIAgent()` directly on provider SDK clients
- **Direct tool registration**: `AIFunctionFactory.Create(method)`, with no attributes, plugin classes, or kernel required
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

Both current Semantic Kernel and MAF packages support `net8.0`. TFM compatibility is therefore a
prerequisite, not a reason to prefer MAF. MAF is preferred because it is the active successor and its
approval pipeline fits unrestricted Discord autonomy directly.

```xml
<!-- Just add to DiscordSky.Bot.csproj -->
<PackageReference Include="Microsoft.Agents.AI.OpenAI" Version="1.15.0" />
<PackageReference Include="Microsoft.Extensions.AI" Version="10.8.3" />
```

### 2. Native OpenAI Responses API Support

MAF's OpenAI provider uses the official OpenAI .NET SDK under the hood, including `ResponsesClient`. The quickstart example literally uses `.GetResponsesClient()`:

```csharp
var agent = new OpenAIClient("<apikey>")
   .GetResponsesClient()
   .AsAIAgent(model: "gpt-4.1-mini", name: "Sky", instructions: "You are a mischievous Discord companion.");

AgentResponse response = await agent.RunAsync("Write a roast about pineapple pizza");
Console.WriteLine(response.Text);
```

This means Discord Sky is not forced to switch from the Responses API to Chat Completions, which was a
constraint that made Semantic Kernel's `ChatCompletionAgent` a poor fit. Sky already relies on this:
`LlmChatClientFactory` calls `GetResponsesClient().AsIChatClient(model)` today.

### 3. Tool approval, the primitive that actually matters now

This is the reason to revisit MAF. The planned autonomy feature gives Sky's model direct access to the
complete Discord Steward MCP catalog. Its defining correctness property is that a mutation must be
durably recorded before it can execute, so that a crash mid-write is always reconcilable. Approval is
automatic; it is not a human or policy decision.

`Microsoft.Extensions.AI` provides the boundary:

```csharp
// Reads stay plain and auto-invoke. Writes suspend for approval.
tools.Add(isMutation
    ? new ApprovalRequiredAIFunction(mcpTool)
    : mcpTool);
```

`ApprovalRequiredAIFunction` derives from `DelegatingAIFunction`, so it inherits `Name`, `Description`,
and `JsonSchema` from the wrapped tool. The model sees Steward's exact native contract; only the
invocation behavior changes. `FunctionInvokingChatClient` invokes ordinary tools and loops their
results back, but for an approval-required tool it replaces the `FunctionCallContent` with an approval
request and hands control to the caller.

The shipped 1.15.0/10.8.3 assemblies use `ToolApprovalRequestContent` and
`ToolApprovalResponseContent`; some Microsoft Learn pages show newer `FunctionApproval*` names. Sky's
implementation uses `ToolApprovalAgentOptions.AutoApprovalRules` directly and does not need to create
approval-response content itself, so no compatibility adapter or reflection layer is present.

That maps onto the unrestricted-autonomy design almost one to one:

| Design requirement | Primitive |
|---|---|
| Reads run freely, writes intercept | plain tool vs `ApprovalRequiredAIFunction` |
| Automatic durable approval | `ToolApprovalAgentOptions.AutoApprovalRules` |
| Persist before dispatch | canonicalize and fsync every write, then return `true` |
| Persistence failure | return `false` or throw; do not invoke an unrecorded call |
| Bounded run segment | `MaximumIterationsPerRequest` |
| No parallel-mutation hazard | `AllowConcurrentInvocation` defaults to `false` |
| Correlation metadata | `McpClientTool.WithMeta`, bound by the host, invisible to the model |

`ToolApprovalAgent.AutoApprovalRules` are asynchronous context-aware callbacks, not static tool-name
allowlists. A rule receives the exact function call plus `Agent`, `Session`, `RequestMessages`, and
`RunOptions`. Sky's rule canonicalizes the model-authored call, commits `dispatch_pending`, and returns
`true` only after that commit succeeds. It performs no content, target, field, risk, cadence, consent,
or reversibility checks. Standing "always approve" rules are deliberately not used because they would
skip Sky's durable intent record.

Committed tests now cover the default `ChatClientAgent` approval decorators, a durable pre-dispatch
record before native write invocation, foreign request-ID rejection, `McpClientTool.WithMeta(JsonObject)`,
and hosted tool-search serialization. A real cross-repository stdio test starts the sibling Steward
binary with its `UnrestrictedAutonomy` profile and verifies its complete catalog and capability output.

One caveat is load-bearing. The framework documents `ApprovalRequiredAIFunction` as an advisory marker
that "does not enforce the requirement for user approval." Enforcement exists only because
`FunctionInvokingChatClient` is in the pipeline. Keep `UseProvidedChatClientAsIs = false`, the default,
so `ChatClientAgent` installs FICC, approval-response binding, and non-approval bypass itself. A
startup probe and integration test verify that composition after every MAF upgrade.

### 4. The unrestricted Discord agent

Use `ChatClientAgent`'s default decorated chat-client pipeline. Its FICC processes function calls
serially and permits up to 40 iterations per request. Forty is intentional here: the product permits
multi-write campaigns, and this limit exists only to terminate accidental software loops. A separate
wall-clock timeout bounds a run.

The default non-approval bypass lets native reads execute even when a response also contains a guarded
write. Approval-response binding ensures a response can approve only a request the framework actually
surfaced and cannot substitute different arguments.

The compatibility tests invoke fake approval-required functions and verify the durable dispatch state
before invocation. The live provider smoke remains pending until an OpenAI API key is available.

`ToolApprovalAgent` wraps that agent. Its one Sky auto-approval rule commits the exact write to the
SQLite ledger with `PRAGMA synchronous = FULL` and returns `true`; it does not decide whether Robotnik
is allowed to perform the operation. A delegating native-function wrapper records whether transport
returned an accepted result or threw an uncertain outcome.

An ordinary run completes in one `RunAsync` because every durably recorded write auto-approves. A
failed persistence attempt surfaces an approval request and ends that run; after infrastructure
recovery, start a new run from persisted Discord and operation state rather than resuming stale model
context.

### 5. Large-catalog tool search

Steward's roughly 173 native operations should not be attached as 173 complete schemas to every model
request. OpenAI recommends fewer than 20 initially loaded functions and supports `tool_search`,
namespaces, and deferred functions on GPT-5.4 and later. That preserves unrestricted authority while
loading schemas only when the model asks for them.

This provider path is compile- and serialization-proven. M.E.AI 10.8.3 exposes experimental
`HostedToolSearchTool`; MAF 1.15.0 resolves OpenAI 2.10.0; and a disposable test captured a Responses
request containing `tool_search`, namespace metadata, `defer_loading`, and a deferred
`ApprovalRequiredAIFunction`. Release 0 commits that test and adds one live GPT-5.5 smoke. Never
truncate the manifest or substitute a generic invocation facade.

### 6. Capability disposition

| MAF capability | Decision | Reason |
|---|---|---|
| `ChatClientAgent` and FICC | Use now | Native read/tool-result loop |
| `ApprovalRequiredAIFunction` | Use now | Preserves native tool contract while suspending writes |
| `ToolApprovalAgent.AutoApprovalRules` | Use now | Automatic persist-before-dispatch hook |
| `AgentSession` | Use now | Carries one short unrestricted tool conversation |
| Function middleware | Use now | Result capture and latency telemetry |
| OpenTelemetry agent wrapper | Use now, sensitive data off | Standard spans and token metrics without duplicating private arguments |
| Responses tool search | Use now, experimental | Complete catalog access with lazy schema loading |
| Local evaluation APIs | Defer to ScenarioLab integration | Useful after the core call path is stable |
| Agent skills | Reject for Steward tools | Provider tool search preserves native schemas; skills would turn operations into docs or scripts |
| Workflow checkpointing | Reject | SQLite and Steward reconciliation are the durable operation truth |
| `LoopAgent` | Reject | FICC already owns the tool loop; a second loop obscures termination |
| Background agents | Reject | Reintroduces intent translation without useful parallel work |
| `AgentModeProvider` | Reject | Guild enablement is deployment configuration, not a model mode |
| Standing "always approve" rules | Reject | Would bypass Sky's pre-dispatch intent persistence |

### 7. Observability

MAF's OpenTelemetry wrapper emits agent, chat, and tool spans plus token metrics. Production keeps
sensitive-data capture disabled because prompts, native arguments, and tool results already have a
private durable home. Existing `world_autonomy` JSONL and SQLite records remain the product evidence;
OpenTelemetry is operational correlation, not a replacement ledger.

---

## What MAF Does Not Solve

MAF does not replace Discord-specific context aggregation, exact-guild routing, wakeup scheduling,
per-workload model selection, reasoning budgets, operation persistence, crash reconciliation,
reception analysis, or the existing safety filter. Those remain ordinary Sky services. The framework
contributes a tool loop, a durable suspend/resume protocol, sessions, middleware, and observability.
Treating it as more than that would turn adoption into a rewrite.

### Forced tool choice in the existing reply path

Discord Sky requires the model to always use the `send_discord_message` tool. MAF supports this through `ChatToolMode.RequireSpecific("send_discord_message")` in `Microsoft.Extensions.AI.ChatOptions.ToolMode`, which maps to `tool_choice: { type: "function", name: "send_discord_message" }` at the API level.

There is a critical interaction with the auto-invoke loop. `ChatClientAgent` uses
`FunctionInvokingChatClient` internally, which invokes tool functions and loops back to the model.
With `RequireSpecific`, the model is forced to call the tool on every response, including after
receiving tool results, which creates an infinite loop. This is a
[known issue](https://github.com/microsoft/agent-framework/issues/2879) with three documented
workarounds:

1. **`AIFunctionDeclaration` (recommended)**: Define the tool as a schema-only declaration (not an invocable `AIFunction`). `FunctionInvokingChatClient` will not auto-invoke it and will pass the `FunctionCallContent` back to the caller. This preserves the current flow: one API call, extract args from the response, no loop.
2. **`MaximumIterationsPerRequest = 1`**: Limit the auto-invoke loop to one round-trip, then stop. This invokes the function once but adds an extra API call.
3. **Middleware**: Flip `ToolMode` to `Auto` after the first tool invocation.

The declaration approach suits `send_discord_message`, which is a structured output mechanism rather
than a function returning a result to the model. Unrestricted Discord autonomy is the opposite: its
MCP tools are real functions whose results feed the next turn, and automatic approval provides the
right durability suspend point.

---

## What adoption looks like now

The original migration plan is obsolete. The files it proposed deleting no longer exist, and the
`Microsoft.Extensions.AI` move already delivered the code reduction it promised. What remains is a
narrower, more deliberate adoption.

### Implementation Status

Implemented in the current working tree:

- MAF/MCP package upgrade and the OpenAI 2.10 Responses API compatibility fix;
- exact guild-to-Steward child-process bindings, full native tool discovery, hosted tool search, and
   host-bound MCP metadata;
- durable SQLite runs, pre-dispatch write records, automatic MAF approval, and startup reconciliation
   through native `get_operation` and `reconcile_operation`;
- one concurrent autonomous session per configured guild, triggered by human guild messages;
- focused unit tests, hosted-search request serialization, and a real sibling-Steward stdio integration test.

Still pending before production enablement: a live GPT-5.5 hosted-tool-search smoke with a configured
API key and disposable-guild validation across real Discord R0 through R5 operations.

### Prerequisites

| Capability | Status |
|---|---|
| `net8.0` target | Available, no TFM change |
| Responses API | Already in use through `GetResponsesClient().AsIChatClient(model)` |
| Declaration-only tools | `AIFunctionDeclaration` remains the right shape for `send_discord_message` |
| Tool approval types | Require an upgrade, see below |

The implemented package graph includes:

- `Microsoft.Agents.AI.OpenAI` 1.15.0.
- An explicit `Microsoft.Extensions.AI` 10.8.3 reference.
- `ModelContextProtocol` 1.4.1.

MAF 1.15.0 declares a minimum `Microsoft.Extensions.AI` dependency of 10.6.0. Pinning 10.8.3 removes
transitive ambiguity and targets the current package pair reviewed here.

### Historical sequencing

1. **Upgrade and compatibility-test only.** Bump both packages, commit the minimal approval spike and
   full-catalog tool-search spike as tests, change no production behavior, run the suite, deploy, and
   watch replies, cold opens, images, reactions, and consolidation for a full cycle. This is the
   entire release.
2. **Use the agent layer for unrestricted Discord autonomy only.** Build that session as a
   `ChatClientAgent` with durability-wrapped write tools. Leave `CreativeOrchestrator` on its
   hand-rolled loop, which works, is well tested, and gains nothing from conversion.
3. **Reassess the reply path later, if ever.** Converting it is optional and should be justified by a
   concrete benefit rather than consistency.

### What does not change

`ContextAggregator`, `SafetyFilter`, normal-reply prompt assembly, per-workload model routing,
reasoning budgets, and the existing memory and telemetry stack remain intact. `DiscordBotService` adds
an optional exact-guild autonomy opportunity route but leaves its normal `CreativeOrchestrator` reply
loop unchanged. MAF is not a substitute for any of these components.

The risk worth naming is blast radius. The upgrade touches the shared LLM path used by every feature,
which is why it ships alone.

---

## Risk Assessment

### Version currency

Sky now pins MAF 1.15.0 and the approval types this implementation uses. The remaining version risk is
future package drift in the experimental hosted-search and approval APIs.

**Risk**: the package graph spans MAF, `Microsoft.Extensions.AI`, MCP, and the OpenAI SDK, while the
underlying provider client is shared by replies, cold opens, images, reactions, and consolidation.

**Mitigation**: retain the focused compatibility tests, the full Sky suite, and the real Steward stdio
integration test. Run the pending live GPT-5.5 smoke before enabling any guild binding in production.

### Approval enforcement is opt-in by construction

`ApprovalRequiredAIFunction` does not enforce anything by itself; the pipeline does. A refactor that
changes how the agent is built can silently disable the guarantee without failing a test that only
checks tool registration.

**Mitigation**: assert the enforcing pipeline at binding startup and test the negative case.

### Documentation and package naming drift

Microsoft Learn currently shows `FunctionApproval*` names while the reviewed NuGet assemblies expose
`ToolApproval*`. Copying the current docs into code would not compile against the pinned packages.

**Mitigation**: pin exact versions, compile against the package XML/assemblies, and isolate approval
request/response names behind one adapter. Upgrade that adapter deliberately when the package API
changes.

### Responses tool search is experimental

`HostedToolSearchTool` serializes correctly through the target stack but carries the `MEAI001`
experimental warning and may change between package releases.

**Mitigation**: keep the request-serialization test and one live GPT-5.5 smoke in Release 0. If the
API changes, update the pinned compatibility layer or use client-executed search. Never silently fall
back to a restricted subset.

### Semantic Kernel Being Sunset

SK will receive critical fixes for "at least one year" after MAF GA. This does not affect Sky directly,
but if an abstraction layer is ever adopted, MAF is the one to pick.

### Community and Ecosystem Maturity

MAF has the same core team as Semantic Kernel and Microsoft's full backing, with integrations spanning
Claude Agent SDK, GitHub Copilot SDK, and Azure Functions.

---

## Recommendation

Adopt MAF, but narrowly and in a specific order.

1. **Upgrade the packages as an isolated release.** Nothing else can proceed without the tool approval
   types, and this is the change with real blast radius.
2. **Build unrestricted Discord autonomy on the agent layer.** The approval protocol is the first
   concrete problem in this project that MAF solves better than hand-rolled code, because it provides
   a persist-before-dispatch suspend point without limiting Robotnik's authority.
3. **Leave the reply path alone.** `CreativeOrchestrator` works, is well tested, and its terminal
   `send_discord_message` shape gains nothing from an agent abstraction.

### What Not to Do

- **Do not adopt Semantic Kernel.** It is maintenance-focused and does not provide a better approval
   boundary for this feature. TFM support is not the differentiator.
- **Do not convert everything for consistency.** A second paradigm introduced for one feature is
  acceptable; a rewrite of working paths is not.
- **Do not rely on `ApprovalRequiredAIFunction` alone for safety.** It is an advisory marker. Assert
  that the approval-enforcing pipeline is present, and test it.
- **Do not use standing or built-in "approve everything" rules.** Use one durable auto-approval
   callback that records every write before returning `true`; it does not decide what Robotnik may do.
- **Do not use `WithName` or `WithDescription` on MCP tools.** Renaming a server's tools rebuilds the
   facade the unrestricted design explicitly rejects.

See `docs/discord_steward_unrestricted_autonomy_design_2026-07-28.md` for the design that consumes
these primitives.

---

## References

- [Microsoft Agent Framework Documentation](https://learn.microsoft.com/en-us/agent-framework/)
- [GitHub: microsoft/agent-framework](https://github.com/microsoft/agent-framework)
- [MAF tool approval](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval)
- [MAF middleware](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/)
- [MAF observability](https://learn.microsoft.com/en-us/agent-framework/agents/observability)
- [OpenAI tool search](https://developers.openai.com/api/docs/guides/tools-tool-search)
- [OpenAI function calling](https://developers.openai.com/api/docs/guides/function-calling)
- [NuGet: Microsoft.Agents.AI.OpenAI 1.15.0](https://www.nuget.org/packages/Microsoft.Agents.AI.OpenAI/1.15.0)
- [NuGet: Microsoft.Extensions.AI 10.8.3](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.8.3)
- [Semantic Kernel and Microsoft Agent Framework (blog)](https://devblogs.microsoft.com/semantic-kernel/semantic-kernel-and-microsoft-agent-framework/), relationship explained
- [Migrate SK/AutoGen to MAF RC (blog)](https://devblogs.microsoft.com/semantic-kernel/migrate-your-semantic-kernel-and-autogen-projects-to-microsoft-agent-framework-release-candidate/), migration guide
- [MAF Migration Guide from SK](https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-semantic-kernel)
- [NuGet: Microsoft.Agents.AI](https://www.nuget.org/packages/Microsoft.Agents.AI/), targets net8.0/netstandard2.0/net472
- [NuGet: Microsoft.Agents.AI.OpenAI](https://www.nuget.org/packages/Microsoft.Agents.AI.OpenAI/), OpenAI provider
- [MAF Running Agents (docs)](https://learn.microsoft.com/en-us/agent-framework/agents/running-agents), AgentRunOptions, ChatClientAgentRunOptions, response types
- [MAF Tools Overview (docs)](https://learn.microsoft.com/en-us/agent-framework/agents/tools/), function tools, tool approval, provider support matrix
- [MAF Multimodal (docs)](https://learn.microsoft.com/en-us/agent-framework/agents/multimodal), vision/image input via UriContent
- [ChatToolMode.RequireSpecific (API ref)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chattoolmode), forced tool choice
- [FunctionInvokingChatClient (API ref)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient), MaximumIterationsPerRequest and AIFunctionDeclaration handling
- [ApprovalRequiredAIFunction (API ref)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.approvalrequiredaifunction), delegating wrapper, advisory marker semantics
- [GitHub Issue #2879: Excessive tool calls with tool_choice="required"](https://github.com/microsoft/agent-framework/issues/2879), confirmed behavior and workarounds
- [Build AI Agents with Claude Agent SDK and MAF](https://devblogs.microsoft.com/semantic-kernel/build-ai-agents-with-claude-agent-sdk-and-microsoft-agent-framework/)
- [Build AI Agents with GitHub Copilot SDK and MAF](https://devblogs.microsoft.com/semantic-kernel/build-ai-agents-with-github-copilot-sdk-and-microsoft-agent-framework/)
- [OpenAI Responses API](https://platform.openai.com/docs/api-reference/responses)
