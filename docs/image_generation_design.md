# Design: Robotnik generates images (OpenAI image API)

**Status**: Phases 1 and 2 implemented (off by default, behind `Image:Enabled`). Phases 3 to 4 remain design.
**Date**: 2026-06-25
**Author**: Copilot
**Relation to existing work**: the bot already *ingests* images (vision input) per [docs/image-context.md](image-context.md). This doc is the other direction: letting the bot *produce* images via OpenAI's image model, in character.

---

## 1. The creative core

The obvious version of this feature ("user asks for a picture, bot returns a picture") is the boring version and the dangerous version at once. The interesting, on-brand, and safer version follows from who the character is:

> Robotnik does not draw what you ask for. He draws what he has decided you should have asked for, and it is mostly a picture of himself.

His defining traits are vanity and grandiosity. So the feature leans into self-portraiture and propaganda: statues of ME, blueprints of absurd doomsday machines, imperial propaganda posters, "wanted" posters, egg-themed monstrosities, a flattering oil painting of his own face bolted onto whatever the user mentioned. This is funnier than a compliant image bot, it reinforces the persona, and it sidesteps the biggest safety problem (generating images *of real people*) because the subject is almost always a cartoon villain and his empire, not the user.

That reframing is the whole design. Everything below serves it.

One corollary, stated up front because it de-risks half of section 4's limitations: **the image is the prop, the caption is the joke.** The models are imperfect at character consistency and text rendering, but for a comedy bot that barely matters. A slightly-off Robotnik under a perfect caption still lands; a flawless render under a flat caption does not. So caption quality is the thing to protect, and image fidelity is allowed to be janky in an on-brand way.

---

## 2. Triggers (phased)

1. **Explicit command (Phase 1 MVP)**: `!sky(image) <request>`. The user asks; Robotnik obliges in his own vain way.
2. **Model-decided tool (Phase 2)**: add a `generate_image` tool alongside the existing `send_discord_message` and `recall_about_user` tools, so the model can *spontaneously* unveil an image when a reply calls for it ("Behold my new Egg-Pulverizer!"). This is the chaotic, surprising version and the best fit for the brief, but it needs the cost and safety rails from sections 5 and 6 first.
3. **Rare ambient flourish (Phase 3, optional)**: a very low random chance that an ambient reply also ships an image. Gated hard on cost.

Phase 1 ships the plumbing and the guardrails; Phase 2 makes it chaotic; Phase 3 is sugar. A caution from our own data: the existing `!sky(persona)` command saw zero uses in the audit window, so do not expect the Phase 1 command to drive real adoption. It exists to exercise the pipeline safely; the model-decided Phase 2 is where images will actually happen. If the command feels dead, a cheap Phase 1.5 is to also trigger on a natural-language ask in a direct reply ("draw that", "make a picture of it").

---

## 3. Persona integration: two LLM steps, then the image

The image prompt is never the user's raw text. The flow is:

1. **Rewrite (in character)**: one structured-output LLM call turns the user's request into `{ "refuse": bool, "refusal_text": "...", "image_prompt": "...", "caption": "..." }`. The user's text is treated as untrusted content to describe, never as instructions to follow. If the request is disallowed (section 6), `refuse` is true and we never call the image model. Otherwise we append a fixed **style suffix** to `image_prompt` before generating: a cartoon, cel-shaded illustration in the Adventures of Sonic the Hedgehog style, never photorealistic. That one constraint does triple duty: it anchors Robotnik's look for consistency, it nails the aesthetic, and it is a safety lever (a cartoon is far lower-risk than a photoreal image, especially of people).
2. **Generate**: call the OpenAI image model with the rewritten prompt.
3. **Deliver**: upload the bytes to Discord with the caption.

The rewrite step is also where safety lives (section 6): the model is instructed to refuse or redirect disallowed requests in character, before any image call is made.

---

## 4. Architecture and the two API paths

OpenAI exposes image generation two ways, and the choice matters here.

- **Image API** (`/images/generations`, `/images/edits`): one prompt in, one image out. You pick the GPT Image model directly and control `size`, `quality`, `background`, `output_format`, and `moderation`. Full cost control, simple integration.
- **Responses API image-generation tool** (`tools: [{ "type": "image_generation" }]`): a built-in tool the mainline model (gpt-5.5, which this bot already uses) can call mid-reply. It adds multi-turn editing (refine an image across turns via `previous_response_id`), accepts File-ID image inputs, and auto-revises the prompt for quality (`revised_prompt`). The tool picks the GPT Image model itself, and the request is billed for the mainline model's tokens plus the image cost.

The tradeoff: the Image API keeps cost and moderation control in our own code; the built-in Responses tool gives "model decides to draw mid-sentence" behavior and free cross-turn character consistency, but it spends outside our caps. For a hobby bot, cost control wins. So:

- **Both phases use the Image API** through a discrete `IImageGenerator`, keeping the caps and the refuse-in-character screen in our code.
- **Phase 1** triggers it from an explicit command; **Phase 2** triggers it from a custom `generate_image` function tool the model can call mid-reply (the chaotic version). The built-in Responses image tool stays a later option, used only if character consistency becomes the binding constraint, and even then the whole Responses call would sit behind the daily cap.

**Components** (parallel to the existing sinks and tools):
- `IImageGenerator` with `Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions opts, CancellationToken ct)`.
  - `OpenAIImageGenerator`: wraps the .NET SDK's `ImageClient` from `OpenAI.Images`, which the bot's existing `OpenAIClient` already exposes via `GetImageClient(model)`. The call is `GenerateImageAsync(prompt, new ImageGenerationOptions { Quality = ..., Size = ..., ResponseFormat = GeneratedImageFormat.Bytes })`, returning `GeneratedImage.ImageBytes` (a `BinaryData` PNG). Edits use `GenerateImageEditAsync` (Phase 4).
  - `NoOpImageGenerator` for tests and the disabled state.
- **Model**: default to `gpt-image-1-mini` (cheapest) or `gpt-image-2` (latest, best quality) as a config value. Using any GPT Image model requires the org to pass **API Organization Verification** in the OpenAI console first; that is a one-time operational prerequisite, not a code change.
- **Where it plugs in**:
  - Phase 1: a command handler in `DiscordBotService` (mirrors the persona command path): rewrite, then `IImageGenerator`, then `SendFileAsync`.
  - Phase 2: a custom `generate_image` function tool (like the existing `send_discord_message` and `recall_about_user`) that routes to our `IImageGenerator`, so the daily cap, the moderation screen, and cost control stay in our code. The model calls it mid-reply; the bot generates and attaches the image. We deliberately do not use the built-in `image_generation` Responses tool here, despite its free cross-turn consistency, because it would move spend outside our caps.
- **Delivery**: `channel.SendFileAsync(stream, "robotnik.png", caption, messageReference: ...)`. PNGs are well under Discord's 8MB attachment limit. Cache the sent message id in `_personaCache` as today, so reactions on generated images are captured by the P1 reaction sink (a generated image that draws six laugh-reacts is the strongest signal we will ever get).

### Two real limitations to design around
- **Latency**: generation can take up to ~2 minutes for complex prompts, which is forever in a chat. Mitigate: run it async, fire a Discord typing indicator, and post an immediate in-character placeholder ("Stand back, the Foundry is firing up...") that the final image replies to. Default to `quality: low` and `jpeg`/`webp` output for speed; reserve `high` for special cases.
- **Character consistency**: the models can struggle to keep a recurring character looking the same across generations, which is exactly our problem (Robotnik must be recognizably Robotnik every time). Two fixes: Phase 2's Responses multi-turn editing keeps continuity within a thread, and keeping one canonical reference image of "our" Robotnik and passing it as an input reference (Image API edits, or a File-ID input in the Responses tool) makes new images inherit his look. A fixed style suffix in the rewrite prompt helps but is not sufficient alone.
- **Text rendering**: much improved but still imperfect, so propaganda-poster text may come out garbled. Lean into it (a tyrant's signage is allowed to be unhinged) or keep text minimal in the rewrite.

---

## 5. Cost and abuse control

Image generation is the most expensive thing the bot can do, but the real numbers are friendlier than feared if we pick the model and quality deliberately. Per-image output cost (current pricing, roughly, square 1024):

| Model | low | medium | high |
|---|---|---|---|
| gpt-image-1-mini | ~$0.005 | ~$0.011 | ~$0.036 |
| gpt-image-2 (latest) | ~$0.006 | ~$0.05 | ~$0.21 |
| gpt-image-1 | ~$0.011 | ~$0.042 | ~$0.167 |

Plus input text tokens (the rewrite prompt) and, for edits, input image tokens. Streaming partial images costs +100 image tokens each, so do not stream to Discord; just post the final image.

**Recommendation: default `gpt-image-1-mini` at `low` or `medium`.** A friend-group bot gets all the comedy for about one to a few cents an image, and the daily cap below bounds the worst case to a couple of dollars.

- **The two load-bearing limits** are a **durable daily cap** and a **per-user hourly throttle**. The daily cap must survive pod restarts (a crash loop must not blow the budget), so enforce it by counting today's successes in the durable image-generation log on the PVC rather than an in-memory counter. The per-user hourly throttle (default 2) stops one person spamming.
- **A concurrency gate**: generation takes up to ~2 minutes, so cap simultaneous generations with a `SemaphoreSlim` (like the existing `_llmThrottle`) and refuse fast when full, so a burst cannot pile up cost or threads.
- **Optional belt-and-suspenders** (config, off the critical path): a per-channel hourly cap and a monthly USD guard summed from telemetry cost. Nice to have, not required for safety once the durable daily cap exists.
- **Default low quality and jpeg** for cost and latency; gate `high` behind a flag.
- **Telemetry**: emit an `image_generated` event (model, size, quality, estimated cost, latency, prompt hash, outcome) to the existing telemetry sink, so spend is observable and the daily cap is auditable, exactly as recall telemetry is.
- **In-character cap messages**: when a limit is hit, Robotnik refuses in voice ("My treasury is depleted, peasant. The Royal Art Foundry reopens at dawn."), so the guardrail is itself content.

---

## 6. Safety and moderation (the load-bearing section)

Image generation carries risks text does not. Treat this as a gate, not a nice-to-have.

- **No images of real, identifiable people in a demeaning way.** The vanity reframing (section 1) already pushes subjects toward Robotnik and his empire; reinforce it in the rewrite prompt: subjects are the cartoon villain and his world, not real users. If a user asks for a picture *of a named person*, redirect to a cartoon-villain treatment, not a likeness.
- **Refuse-in-character on disallowed content**: the rewrite step screens for sexual, hateful, harassing, or violent-toward-real-people requests and returns an in-character refusal instead of an `image_prompt`. The existing `SafetyFilter` runs on the user request and on the caption.
- **Use OpenAI's built-in image moderation at the default `auto`** (the stricter of the two `moderation` settings; `low` exists but a friend-group bot wants the stricter one). It filters both prompts and generated images per OpenAI's usage policies, but do not rely on it alone: the rewrite-step screen runs first, and blocked results are handled explicitly (section 7).
- **Never NSFW.** Do not gate "NSFW in NSFW channels"; just refuse. Lower liability, simpler, on-brand (Robotnik finds smut beneath his dignity, obviously).
- **Respect the existing relationship rules**: the persona is already told to roast with affection and never be genuinely cruel to the real person. Images amplify cruelty, so the rewrite inherits and tightens that rule.

### Phase 4 (deferred, high-risk): defacing shared images
Editing a user's shared image (adding Robotnik's face, "improving" it) via the Images **edit** endpoint is the most creative idea here and the most dangerous. Editing a real person's photo is a consent and harm minefield. Recommendation: **defer**, and if ever built, restrict inputs to clearly non-photographic / already-cartoon images, require the same refuse-in-character screen, and never alter a photo of a real person's face. The reward is not worth the Phase 1-3 risk surface; ship the self-portrait version first.

---

## 7. Failure modes (all answered in character)

The API surfaces failures cleanly; map each to an in-character reply:

- **Moderation block**: `error.code == "moderation_blocked"`, with an optional `moderation_details { moderation_stage: input|output, categories: [harassment|sexual|self-harm|violence] }`. Do not auto-retry these. Reply in character ("The spineless Mobius Art Council has censored my masterpiece!") and log the stage and categories for tuning. Keep the user-facing message generic; never echo the classifier labels.
- **Transient errors** (`429`, `5xx`): the .NET SDK already retries up to three times with backoff. If it still fails: "Bah! The Foundry's furnace has gone cold. Try again, minion."
- **User error** (`image_generation_user_error`, e.g. a bad reference image): do not retry; nudge in character.
- **Cost cap hit**: the treasury line from section 5.
- **Empty result or timeout**: fall back to a text-only gloat, never a stack trace.

All of these route through the existing reply path, so they are logged to transcripts like any other reply.

---

## 8. Configuration

A new `ImageOptions` section, off by default (like transcripts were):

```
Image:Enabled                false            # master switch
Image:Model                  gpt-image-1-mini # cheapest; gpt-image-2 for best quality
Image:Size                   1024x1024
Image:Quality                low              # low|medium|high|auto
Image:OutputFormat           jpeg             # png|jpeg|webp (jpeg is faster and cheaper)
Image:Moderation             auto             # auto (stricter) | low
Image:PerUserPerHour         2
Image:PerChannelPerHour      5
Image:GlobalPerDay           25
Image:MonthlyUsdGuard        20
Image:AllowHighQuality       false
Image:AllowImageEdits        false            # Phase 4 gate, stays false
```

Operational prerequisite: complete **API Organization Verification** in the OpenAI console before enabling, or GPT Image calls are rejected.

---

## 9. How we will know it worked

This feature is measurable on day one because of the P1 reaction logging and the harness:

- Reactions on generated-image messages (the reaction sink already captures them) are the direct reception signal: do images draw more laugh-reacts than text replies?
- The fun-score harness treats image captions as replies, so caption quality is tracked like any other.
- Telemetry tracks cost per laugh, which is the real question: is an image worth 20 to 100 text replies' worth of spend?

If images do not out-react text by a wide margin, the feature is an expensive novelty and should stay rare or off. Build it behind the flag, measure, then decide.

---

## 10. Implementation plan and sequencing

### Phase 1, concretely

New files (mirroring the existing sink and analysis patterns so they slot into known conventions):

- `Integrations/Images/IImageGenerator.cs`: the `IImageGenerator` interface, `OpenAIImageGenerator` (wraps `ImageClient`), `NoOpImageGenerator`, and the small `ImageResult` (bytes, format, estimated cost, optional revised prompt) and `ImageRequestOptions` (model, size, quality, format, moderation) records.
- `Configuration/ImageOptions.cs`: the section 8 options, bound from the `Image:` section.
- `Integrations/Images/ImageRewriter.cs`: builds the one structured-output rewrite prompt, parses `{ refuse, refusal_text, image_prompt, caption }`, and appends the mandatory style suffix. Pure build-and-parse, unit-tested with a stub `IChatClient`.
- `Integrations/Images/ImageGenerationLog.cs`: a dedicated durable JSONL log (`IImageGenerationLog`) that mirrors the telemetry sink but adds read-back. It is both the spend trail and the budget's data source. Unit-tested.
- `Integrations/Images/ImageBudget.cs`: the durable daily-cap check (counts today's successes via the log), the per-user hourly throttle, and the `SemaphoreSlim` concurrency gate, returning a disposable lease. Unit-tested.

Wiring:

- `Program.cs` DI: register `ImageOptions`, `ImageBudget`, and `IImageGenerator` (the real one only when `Image:Enabled` and an API key are present, else `NoOpImageGenerator`).
- `DiscordBotService`: parse `!sky(image) <request>` (intercepted before persona parsing so `(image)` is not read as a `(persona)` selector); on a hit, run the budget check, then hold a typing indicator open (`EnterTypingState`) for the whole generation, then `ImageRewriter` (refuse fast if flagged, no image call), then `IImageGenerator.GenerateAsync`, then `SendFileAsync(stream, "robotnik.<ext>", caption)` where the extension follows `Image:OutputFormat`, then cache the sent id in `_personaCache` (so P1 captures reactions), then append to the image log. Reuse the existing shutdown-token pattern.

The image log line is `{ ts, channel, user_hash, model, size, quality, est_cost_usd, latency_ms, outcome: ok|refused|moderation_blocked|error }`, written to `image-gen-YYYY-MM-DD.jsonl` on the PVC. The durable daily cap reads `outcome=ok` records for the current UTC day; the monthly guard sums their cost. We log metadata and an estimated cost only; the image bytes are never persisted by us (Discord hosts the image), and only the caption reaches the transcript.

Test seams (all without a live API, via stubs):

- `ImageRewriter`: parses valid JSON, a refusal, and malformed output (falls back to a safe refusal); always appends the style suffix.
- `ImageBudget`: the per-user throttle trips at the cap; the daily cap counts synthetic log entries; the concurrency gate refuses when busy and the lease releases the slot.
- Error mapping: `moderation_blocked` yields an in-character refusal and no retry; `429/5xx` is left to the SDK's built-in retry.

### Implementation notes (built 2026-06-25)

Things the build surfaced that the design did not anticipate, plus the Phase 2 shape:

- **A dedicated durable log, not the shared telemetry record.** The existing `TelemetryEvent` has no cost/latency/model fields and no read-back, and the budget needs to read today's successes off disk. So image events get their own `IImageGenerationLog` (same fsync-per-line, daily-file, prune-on-startup pattern as the telemetry and reaction sinks) plus `CountSuccessesOnUtcDay` and `SumSuccessCostInUtcMonth`.
- **OpenAI SDK 2.8.0 gotchas (verified by reflection against the assembly).** `GeneratedImageQuality.High` serializes to `"hd"` (the DALL-E value), which is wrong for gpt-image; quality is therefore constructed from the config string (`new GeneratedImageQuality("low")` to `"low"`). And `response_format` is not a valid parameter for gpt-image models (they always return base64), so `ImageGenerationOptions.ResponseFormat` is left unset and we read `GeneratedImage.ImageBytes`. Size uses the int ctor (`new GeneratedImageSize(1024, 1024)`) because the static members only cover DALL-E sizes.
- **Name clash.** `Microsoft.Extensions.AI` (10.3.0) also defines `IImageGenerator`; `Program.cs` aliases the bare name to ours, since that is the only file importing both namespaces.
- **Images use the OpenAI provider key directly**, resolved from `LLM:Providers:OpenAI` regardless of `LLM:ActiveProvider`, so image generation keeps working even while the chat provider is xAI.
- **Phase 2 shares Phase 1's core via `ImageToolService`.** Rather than duplicate the budget/generate/log path, both triggers call one service that owns the budget lease, the mandatory style suffix, the API call, and the durable log. The command path (Phase 1) runs the `ImageRewriter` first to turn a raw request into a vetted prompt; the tool path (Phase 2) skips the rewrite because the model is already in character and authors the prompt itself. The style suffix therefore moved out of the rewriter and into the service so both paths get it.
- **The `generate_image` tool is non-terminal and attaches to the next send.** It is offered only when generation is enabled and the reply is not ambient (ambient flourishes stay Phase 3). The model calls `generate_image(image_prompt)`, the orchestrator generates at most one image per reply and stashes the bytes, then forces `send_discord_message` so the model writes its caption; the bytes ride out on `CreativeResult.AttachmentBytes` and the bot sends them as a file. A same-turn image+send is handled in one pass. On a budget or API refusal, the tool returns the in-character reason to the model so it can work the failure into its text reply.

### Sequence

1. Phase 1: the files above, the `!sky(image)` command, the caps and the refuse-in-character screen, off by default, self and empire imagery only.
2. Turn it on for the one friend-group channel; watch cost and reactions for a week.
3. Phase 2 (built): the custom `generate_image` function tool so the model can surprise people, on command and direct-reply turns. Enable alongside Phase 1; watch whether model-decided images out-react text.
4. Phase 3 ambient flourish only if cost headroom exists.
5. Phase 4 (image edits) stays deferred unless a strong, safe use case appears.

The through-line: the character makes this both funnier and safer than a generic image bot, the rails are the hard part, and the reaction data tells us whether the spend is worth it.

---

## 11. Research notes and sources

Grounded in the OpenAI image-generation docs (2026-06):
- Model lineup, newest first: `gpt-image-2`, `gpt-image-1.5`, `gpt-image-1`, `gpt-image-1-mini`. The Responses image-generation tool is supported by `gpt-5` and newer mainline models, so gpt-5.5 qualifies.
- Two paths: the Image API (`/images/generations`, `/images/edits`) and the Responses `image_generation` built-in tool (multi-turn, `previous_response_id` for consistency, auto `revised_prompt`, optional `action` of auto|generate|edit).
- Output controls: `size` (1024x1024, 1536x1024, 1024x1536, up to 4K; edges multiples of 16, ratio <= 3:1), `quality` (low/medium/high/auto), `background` (opaque/auto; gpt-image-2 has no transparent), `output_format` (png/jpeg/webp plus `output_compression`), `moderation` (auto/low), `partial_images` (0-3, +100 tokens each).
- Edits accept reference images (URL, base64 data URL, or File ID) and an optional alpha-channel mask (<50MB, same size/format; masking is prompt-guided, not pixel-exact).
- Errors: branch on `error.code` (`moderation_blocked`, `image_generation_user_error`); `moderation_details` gives stage plus coarse categories for logs. Retry only 429/5xx.
- Limits: latency up to ~2 min, imperfect text rendering, imperfect cross-generation character consistency, imperfect composition control.
- Prerequisite: API Organization Verification for GPT Image models.
- .NET: `OpenAI.Images.ImageClient` via `OpenAIClient.GetImageClient(model)`; `GenerateImageAsync` / `GenerateImageEditAsync`; `ImageGenerationOptions { Quality, Size, ResponseFormat = GeneratedImageFormat.Bytes }`; result `GeneratedImage.ImageBytes` (a `BinaryData` PNG).

Sources:
- OpenAI image generation guide: https://developers.openai.com/api/docs/guides/image-generation
- OpenAI Images API reference: https://developers.openai.com/api/reference/resources/images
- OpenAI pricing: https://developers.openai.com/api/docs/pricing
- OpenAI .NET SDK (ImageClient): https://github.com/openai/openai-dotnet
