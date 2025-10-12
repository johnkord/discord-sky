# Targeted Reply Design (Discord Sky)

## Summary
Teach the OpenAI Responses API to decide whether to respond to a specific recent Discord message or post a general channel update, then let the bot use Discord's message reference feature to reply directly to the chosen message. The backend supplies structured history (including message IDs), asks the model to emit a `send_discord_message` tool call with all required parameters (text, reply target, attachments, etc.), and enforces the decision before sending the Discord reply.

## Goals
- Let the model choose whether to reply to a specific message or send a standalone update.
- Make the choice controllable and observable from the backend.
- Minimize accidental replies to unsafe or deleted content.
- Keep the orchestration consistent with existing Creative personas.

## Non-Goals
- Building full conversation threading or multi-turn memory.
- Replacing the safety filter or moderation pipeline.
- Introducing user-facing configuration UI.

## Current State
- `CreativeOrchestrator` gathers the last few Discord messages (without IDs) via `ContextAggregator`.
- The Responses API is called once per request with free-form text instructions. Returned text is sent to the channel with `SendMessageAsync`.
- No mechanism exists to reply to a specific message or to carry message identifiers through the orchestration flow.

## Proposed Solution

### High-Level Flow
1. **Gather history with metadata**: extend `ChannelMessage` to include `ulong MessageId` and optionally `bool IsBot`. Update `ContextAggregator` so the last *N* messages include IDs and author metadata.
2. **Shape the model request**:
   - Use the Responses API `input` array with a system instruction describing the decision task and enumerating recent messages as JSON rows (id, author, content, age).
   - Register a single-response tool named (for example) `send_discord_message` whose JSON schema contains fields like `mode`, `target_message_id`, `text`, `embeds`, `stickers`, `reference_type`, etc. (only `text` is required initially).
   - Instruct the model that it **must** choose between replying to one of the supplied message IDs or broadcasting (represented by `target_message_id = null`) and then invoke `send_discord_message` with the parameters that describe the final Discord payload.
3. **Post-process the response**:
   - Accept only tool-call outputs; if the model returns free-form text, treat that as a parsing failure and fall back to broadcast behavior.
   - Parse the tool arguments with `OpenAiResponseParser` (add helper) and validate: if `mode == "reply"`, ensure the ID is in the provided history and the message is still present.
   - Run the existing safety scrub on `text`.
4. **Send to Discord**:
   - Map the tool parameters directly to Discord.NET options (e.g., use `messageReference: new MessageReference(targetId)` when replying, map optional embed fields when later supported).
   - Otherwise, call `SendMessageAsync(text)` as today.
5. **Telemetry**: log the tool payload (mode, ID, optional extras) and surface mismatches or validation failures.

### Prompt & Schema Details
- **System instruction**: describe the persona expectations, the decision task, and constraints (choose one of the supplied message IDs or broadcast, avoid older than `N` minutes, respect quiet hours). Explicitly state that the response must be a single `send_discord_message` tool invocation.
- **History encoding**: send a short bullet list or JSON array via the `input` `messages`, e.g. `{"id": "123", "author": "User", "content": "...", "age_minutes": 3}`. Keep under token budget (<1k tokens).
- **Tool schema**: define the JSON schema passed when registering the tool, capturing text, reply target, allowed component arrays, and any future extensible fields.
- **Safety fallback**: if the model fails to call the tool or the arguments are invalid, default to broadcast mode to avoid replying to the wrong message.

### Discord API Usage
- Require the `MessageId` for each history entry; add retrieval in `ContextAggregator` (e.g., `IMessage.Id`).
- When replying, reuse Discord.NET's `MessageReference` to create an in-thread reply based on the `target_message_id` supplied by the tool call.
- Handle the case where the target message has been deleted (catch `HttpException` and downgrade to broadcast with an apology message).

## Edge Cases
- **Empty history**: force `broadcast` mode.
- **All history authored by bots**: optionally prefer broadcast unless the model explicitly selects a bot message.
- **Moderation blocks**: if the chosen text trips safety, fall back to a canned response noting the issue.
- **Timeouts from OpenAI**: degrade to current broadcast behavior.

## Implementation Notes
- Update records in `CreativeModels.cs` and associated tests.
- Introduce a reusable tool-definition helper (e.g., `OpenAiTooling.SendDiscordMessageDefinition`) so orchestrator and tests share the JSON schema registration.
- Extend `OpenAiResponseParser` with helpers to deserialize the tool arguments into strongly typed records, validating required fields and rejecting unexpected data.
- Thread the parsed decision through `CreativeResult` (e.g., add `ulong? ReplyToMessageId`, `string Mode`, future optional fields for embeds).
- Adjust `DiscordBotService` to honor `CreativeResult.ReplyToMessageId` when sending replies.
- Add structured logging (`ReplyDecision` event) for observability and future tuning.

## Alternatives Considered
- **Backend-driven selection**: Have the bot heuristically choose a target (e.g., most recent mention). *Rejected* because it removes persona context and makes the reply feel generic.
- **Two-step model call**: First ask the model which message to answer, then request the actual text. *Rejected* for cost and latency; a single structured response suffices.
- **Discord commands for explicit replies**: Require the user to mention the message or use reply UI to trigger the bot. *Rejected* because it shifts the decision burden to users and breaks the "surprise persona" experience.

## Rollout & Observability
- Feature flag the reply behavior (config key in `OpenAIOptions`) for gradual enablement.
- Add metrics for decision distribution (`reply` vs `broadcast`) and failure counts.
- Write unit tests for parser validation and orchestrator branching logic.
- Verify in a staging guild before enabling broadly.

## Risks & Mitigations
- **Model hallucinates IDs**: enforce schema validation and reject unknown IDs.
- **Token inflation**: cap history length and prune content over ~250 characters.
- **Discord rate limits**: reuse existing rate-limit handling; replies count the same as normal messages.
- **User privacy**: ensure only recent, visible messages are sent to OpenAI (respect channel allowlist and ban words).
