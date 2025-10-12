# Discord Sky Bot – OpenAI-Powered Creative Engine Proposal

## Overview
- **Problem**: The bot maintains multiple bespoke content generators (`BitStarterService`, `GremlinStudioService`, `HeckleCycleService`, `MischiefQuestService`) that duplicate creative logic, are hard to extend, and lack shared context.
- **Proposal**: Replace service-specific generators with a unified OpenAI integration that orchestrates chat completions, image generation, and tool calls using shared context (channel history, configuration, lightweight knowledge caches).
- **Outcome**: Increase content quality and adaptability, reduce maintenance overhead, and unlock new interaction modes (multi-modal prompts, dynamic memory).

## Current State
- Each service hardcodes templates and randomness to synthesize text; none leverage live Discord context beyond the direct request payload.
- Chaos tuning knobs (`ChaosSettings`) provide limited variability, with no learning from prior interactions.
- Adding new behaviors requires bespoke classes, testing, and releases.

## Goals
1. Deliver richer, context-aware responses (text + images/audio) powered by OpenAI models.
2. Centralize creative generation behind a single abstraction that different bot commands can reuse.
3. Support multiple context providers: Discord channel history, pinned knowledge summaries, configuration (quiet hours, banned words).
4. Preserve user safety and moderation controls (ban words, quiet hours, rate limiting).
5. Enable quick iteration via prompt versioning, feature flags, and telemetry.

## Non-Goals
- Building a full memory datastore (considered future work).
- Replacing Discord command routing or authentication logic.
- Persisting long-form analytics dashboards (lightweight telemetry only).

## Proposed Architecture
```
+-----------------+      +--------------------+      +---------------------+
| Command Handler | ---> | Creative Orchestrator | -> | OpenAI Client (API) |
+-----------------+      +--------------------+      +---------------------+
                               |                      /         \
                               v                     /           \
                   +---------------------+   Image Gen API   Function Calls
                   | Context Aggregator |          |                |
                   +----------+----------+          |                |
                              |                     |                |
        +---------------------+----------------------+----------------+------+
        | Discord History | Config/Chaos | Safety Filters | Cache |
        +-----------------+----------------------+----------------+------+
```

### Key Components
- **Creative Orchestrator**: Entry point replacing the four services. Accepts high-level intents ("generate bit", "remix attachments"), selects prompt template, attaches context, invokes OpenAI, and shapes responses.
- **Context Aggregator**: Pulls relevant Discord messages (last N messages in channel, user metadata), composes lightweight knowledge summaries (pin highlights, embeds), and injects Chaos settings.
- **OpenAI Client**: Wraps Azure OpenAI or OpenAI REST APIs; supports chat completions, image generation (DALLE), and tool/function calling for structured outputs (e.g., reminder schedule).
- **Safety Filters**: Enforce banned words, quiet hours, and rate limits pre/post generation; escalate to moderation channel if policy violations detected.
- **Response Formatter**: Converts model outputs into Discord-ready embeds/messages and optional follow-up actions (schedule reminders, attach images/audio).
- **Prompt Repository**: Versioned storage (YAML/JSON) of prompt templates, instructions, and examples per intent to enable fast iteration.
- **Feature Flags / Telemetry**: Integration with configuration to control rollouts and capture metrics (latency, token usage, satisfaction).

## Context Sources
| Source | Usage | Notes |
| --- | --- | --- |
| Discord channel history | Provide recent conversation context, references to participants, maintain tone. | Use Discord API to fetch last 20-50 messages; redact PII based on policy. |
| Discord user info | Personalize responses, respect roles. | Access via existing Discord client. |
| Pinned knowledge summaries | Provide curated facts (e.g., project status, lore). | Refresh via scheduled jobs or manual updates; inject as plain text. |
| Configuration (`ChaosSettings`, `BotOptions`) | Determine temperature, max outputs, quiet hours, banned content. | Map to model parameters and post-processing filters. |
| Cache / Past prompts | Avoid repeating recent content, respect cooldowns. | Simple in-memory cache to start; optional Redis for persistence. |

## Interaction Modes
- **Text Generation**: Primary mode for creative scripts, reminders, quests.
- **Image Generation**: Use DALLE or `gpt-image` for Gremlin remix outputs; model returns image URLs stored temporarily (e.g., Azure Blob) before posting.
- **Function Calling**: Model emits structured JSON (e.g., `HeckleResponse` fields) enabling deterministic scheduling and follow-ups.
- **Multi-turn Conversations**: Orchestrator maintains short-lived session state during complex commands (e.g., quest approval flow).

## API Usage & Configuration
- New `OpenAIOptions` section in `appsettings` containing endpoint, apiKey (secret), default models (gpt-4.1-mini, dall-e-2), rate limits, and fallbacks.
- Reuse existing `ChaosSettings` to map to LLM parameters (temperature, top_p, frequency penalty, max tokens) and to adjust context length.
- Implement exponential backoff, retries, and fallback models for resilience.
- Log prompt IDs, token counts, latency, and completion status for monitoring.

## Migration Plan
1. **Scaffold**: Introduce OpenAI client, options binding, and prompt repository without changing commands; add feature flags.
2. **Orchestrator MVP**: Implement creative orchestrator with shared interfaces replicating current service outputs (`BitStarterResponse`, etc.).
3. **Shadow Mode**: For each command, run legacy service + OpenAI orchestrator in parallel, compare outputs via logging/telemetry.
4. **Flip Flag Per Feature**: Replace `BitStarterService` call with orchestrator once quality validated; repeat for Gremlin, Heckle, Mischief.
5. **Consolidate Models**: Deprecate legacy services after stable period; archive templates for regression testing.

## Testing Strategy
- Unit tests for context aggregation, prompt assembly, and response shaping (mock OpenAI client).
- Contract tests for function-call schemas ensuring backwards compatibility with existing DTOs.
- Integration tests hitting OpenAI sandbox/staging environments with recorded fixtures.
- Load tests to validate rate limits and concurrency under busy channels.

## Risks & Mitigations
| Risk | Impact | Mitigation |
| --- | --- | --- |
| API outages / latency | Bot becomes unresponsive | Implement retries, circuit breaker, and local fallback templates for critical paths. |
| Cost overruns | High operational expenses | Track token usage, enforce quotas, dynamically downgrade to smaller models when needed. |
| Safety / policy violations | Offensive content reaches users | Maintain safety filters, leverage OpenAI content moderation API, log and review incidents. |
| Context bloat | Prompt too large, slow responses | Apply heuristics to trim history, summarize with smaller model before main call. |

## Open Questions
- What cadence should we use to refresh pinned knowledge summaries and ensure they stay relevant?

## Appendix
- **Legacy Services**: `BitStarterService`, `GremlinStudioService`, `HeckleCycleService`, `MischiefQuestService` (to be deprecated).
- **Current Status**: Legacy services now delegate to the OpenAI Responses API for generation while preserving existing DTOs, allowing gradual migration of command handlers.
- **Models**: `BitStarterRequest/Response`, `GremlinArtifact`, `HeckleResponse`, `MischiefQuest` remain as DTOs initially to reduce churn.
- **Dependencies**: `OpenAI .NET SDK` or lightweight REST client; optional Azure Storage SDK for asset hosting.
