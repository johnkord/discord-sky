# Discord Sky

Discord Sky is a private Discord companion whose default character is Dr. Robotnik from Adventures of Sonic the
Hedgehog. It reads the room, replies or reacts selectively, remembers useful callbacks, generates images, and can
run an unrestricted Discord Steward child for explicitly bound guilds.

The production design separates ordinary conversation from administrative autonomy. Robotnik retains authority,
while host-owned routing and persistent budgets decide when the expensive tool-enabled agent is warranted.

## Current Capabilities

- Direct commands, mentions, and replies with deterministic reply targeting.
- Ambient `Conversation`, `Reaction`, `Silence`, and `FullAutonomy` routes selected by a utility-model judge.
- One-call, no-tools conversation for ordinary Robotnik lines.
- Full Astra/xhigh autonomy with hosted discovery of the complete native Steward catalog for bound guilds.
- Per-user factual, experiential, running-bit, meta, and suppression memories.
- GPT Image 2.5 generation, reference edits, transparent outputs, and progressive previews behind shared budgets.
- Persistent Empire State mood, ranks, and bounded war-room log.
- In-character emoji reactions using server-approved Unicode and custom emotes.
- Scam, AutoMod, new-account, and raid protections.
- Durable telemetry, transcripts, reactions, image records, route budgets, and autonomy journals on a PVC.

## Architecture At A Glance

```text
Discord message
  -> local command/safety ownership
  -> bound autonomy guild?
       direct -> full autonomy -> direct conversation fallback -> no-model decree
       ambient -> coalesced episode -> audience judge
                    -> FullAutonomy | Conversation | Reaction | Silence
  -> ordinary persona pipeline for non-autonomy surfaces
  -> registered Discord delivery + transcript/reception telemetry
```

`Bot:DisabledGuildIds` and `Bot:DisabledGuildNames` are checked before all message behavior. A disabled guild gets
no replies, reactions, images, memory extraction, scam/new-account posts, or member greetings. Native Discord
AutoMod rules may remain as passive safety, but Robotnik block taunts are suppressed.

All active-provider chat calls and OpenAI images share one persistent `LlmProviderGuard`. It enforces quota/auth
circuit behavior, conservative in-flight reservations, and hourly/daily estimated spend ceilings. World-autonomy
route budgets are separate so scarce full-agent attention can degrade gracefully to conversation.

See [docs/runtime_architecture.md](docs/runtime_architecture.md) and
[docs/autonomy_routing_cost_controls_2026-08-03.md](docs/autonomy_routing_cost_controls_2026-08-03.md).
Start with the [maintenance atlas](docs/maintenance/atlas.md) to find the owner, nearest test, and evidence for a behavior. The [decision register](docs/decisions/README.md) records the reasons and stop conditions.

## Requirements

- .NET SDK 10.0 for Sky, its tools and tests, and the pinned Steward child. The optional local SQLite ledger uses the system SQLite library on Linux.
- A Discord bot token with Message Content Intent enabled.
- An API key for the configured `LLM:ActiveProvider`.
- Docker, Azure CLI, and kubectl for the AKS deployment path.

## Local Setup

1. Restore dependencies:

   ```bash
   dotnet restore
   ```

2. Put development credentials in `src/DiscordSky.Bot/appsettings.Development.json`, which is ignored by git.
   Do not modify or commit production secrets.

   ```json
   {
     "Bot": {
       "Token": "..."
     },
     "LLM": {
      "ActiveProvider": "OpenAI",
       "Providers": {
         "OpenAI": {
           "ApiKey": "..."
         }
       }
     }
   }
   ```

3. Run the bot:

   ```bash
   DOTNET_ENVIRONMENT=Development dotnet run --project src/DiscordSky.Bot/DiscordSky.Bot.csproj
   ```

The default command prefix is `!sky`. Locally handled commands include:

- `!sky <topic>`
- `!sky(persona) <topic>`
- `!sky what-do-you-know`
- `!sky forget <topic>`
- `!sky forget-me`
- `!sky(image) <request>`
- owner/moderator Empire and safety commands documented in source and operational runbooks

Image defaults are Flare for new pictures and Sunburst for edits, at medium quality. Reply to a delivered image
with `change the background to green`, or attach reference images to a new request. Explicit controls include
`!sky(image) a mechanical crown sticker --transparent --quality high` and
`!sky(image) change the background --edit --format png`. See
[docs/image_generation_2_5.md](docs/image_generation_2_5.md) for sizes, masks, previews, cost accounting, and limits.

To read a screenshot, attach it to the question or reply directly to its message and mention the bot. No
`!sky(image)` command is needed: in autonomy guilds the chat model receives the image itself. A direct question
such as "what does this say?" can also refer to immediately preceding image-only posts in the same channel.
An upload on the question takes precedence. A `.png` filename and a WebP metadata label do not require re-exporting.

## Model Routing

Model names and reasoning effort are configuration, not hard-coded behavior. Production uses typed workload
profiles for `Main`, `Ambient`, `Utility`, `ColdOpen`, `ColdOpenCritic`, `ImageRewrite`, `MemoryExtraction`, and
`MemoryConsolidation`.

The active provider may be OpenAI or another configured compatible provider. Per-request `ChatOptions.ModelId`
selects the workload model through one shared telemetry and guard boundary.

Production uses OpenAI's `gpt-6.2-astra` through the Responses API for all eight text workloads. Image generation
and editing still use Flare and Sunburst, not Astra. xAI remains an inactive comparison profile, with no automatic
provider fallback. The historical cold-open comparison stays disabled.

| Workload | Callers | Reasoning |
| --- | --- | --- |
| Main | Commands, mentions, direct replies, full world-autonomy agent | xhigh |
| Ambient | Ordinary ambient replies and one-call autonomy conversation | xhigh |
| Utility | Audience/routing and ambient-worth judges, emoji judge, media summaries, Empire updates | low |
| ColdOpen | Proactive cold-open composition | xhigh; cold opens currently disabled |
| ColdOpenCritic | Advisory cold-open audit | xhigh; cold opens currently disabled |
| ImageRewrite | Persona-aware image prompt and caption preparation | xhigh |
| MemoryExtraction | Conversation-window memory extraction | low |
| MemoryConsolidation | Rewriting a user's memory set at the cap | low; consolidation currently disabled |

`ExtraHigh` in configuration serializes as `xhigh`. Effort applies to each model call, not to the total run;
a tool-enabled run can make several xhigh calls. Astra supports low, medium, high, xhigh, and max, but not none.
The current profile layer does not yet expose max; it should be wired and tested before enabling it. Reply
verbosity, reasoning effort, and output-token limits are different controls. The 1,200-token setting is not a
universal cap: explicit workload budgets and reasoning headroom can override it, and full autonomy currently
uses the provider's output limit.

The existing live limits remain $4/hour and $12/day, with the existing route budgets. GPT-6.2 Astra accounting uses
$8/M input, $0.40/M cached input, $10/M 30-minute cache writes, and $40/M output. Above 272,000 input tokens, input/cache
rates double and output rises by 50%. The $0.50 in-flight reservation is an estimate, not a hard per-call cap.
Private per-guild or persona model overrides can supersede defaults; check those when changing providers.

Model capabilities and pricing: [OpenAI GPT-6.2 Astra](https://developers.openai.com/api/docs/models/gpt-6.2-astra).

## Memory

Conversation windows are extracted into typed per-user memories. Suppressed, superseded, meta, and
instruction-shaped entries are filtered before ambient use. The extraction opportunity gate supports `Off`,
`Shadow`, and `Live` modes plus bounded exploration.

Full-autonomy direct petitions can deliberately call `recall_about_user`. The tool is bound to the requester,
loads notes only when selected, and reuses the existing suppression, supersession, ranking, and recall limits.
It does not accept a model-selected user ID. Ambient autonomy and the no-tools conversation route do not receive
this capability. The separate continuity brief remains shadow-only; its top two notes are never injected automatically.

`!sky forget-me` deletes the requester's saved personal notes. Sky confirms only after deletion succeeds. Buffered or in-flight extraction cannot recreate notes from the erased window; conversation after the command can be learned again. This command does not delete Discord messages, transcripts, or operation journals.

## World Autonomy And Steward

World autonomy is dark unless an exact guild binding exists in private runtime configuration. Each enabled guild
gets one isolated Steward child with its own profile and durable paths. All native tools remain callable through
that child. Astra uses hosted tool search and deferred schemas, so the complete catalog remains available without
placing every schema in the first prompt. The inactive xAI profile retains direct exposure because its account
lacks hosted-search alpha access. This setting changes schema presentation, not authority. The xAI-specific
continuation adapter remains available but is not used for OpenAI requests.

Tool-call enforcement, safe reply-envelope recovery, and mixed read/write approval handling are shared across
providers. Switching back to an OpenAI profile does not require undoing these fixes. Approved but uninvoked
writes are closed on agent exit, and missing journal receipts stay unknown without replay or endless recovery.

Hybrid prompt caching is running as a deterministic 50% canary. Both cohorts use the full static persona and
operational instructions before changing run data; Hybrid adds an explicit stable-prefix breakpoint while keeping
implicit caching for the growing tool conversation. The control uses ordinary implicit caching. Neither cohort
changes reasoning effort, authority, or reply style. `world_autonomy_features` identifies the selected cohort and
recall availability for each run. Rollback: `WorldAutonomy:PromptCacheMode=Off` and/or
`WorldAutonomy:MemoryRecallEnabled=false`. See the Kubernetes runbook for comparison queries and promotion gates.

Private profiles and guild IDs never enter public image layers or committed manifests. The hardened deployment
validates exact bindings, probes the bundled Steward executable, and restores prior resources if rollout fails.

See [k8s/discord-sky/README.md](k8s/discord-sky/README.md).

## Observability And Privacy

Durable production evidence lives under the configured PVC paths:

- telemetry: metadata, routing, usage, cost, resources, and selected bounded owner-private context;
- transcripts: full model prompt/reply or deterministic host fallback;
- reactions: add/remove reception events for Sky-authored messages;
- images: model, cost, evidence IDs, digest, and bounded final provider prompt;
- memories, Empire State, provider guard, route budgets, and autonomy journals.

Treat the whole evidence tree as private. Never print or commit tokens. Use the repository skills for read-only
production work:

- `.github/skills/discord-sky-ops/SKILL.md`
- `.github/skills/discord-sky-investigation/SKILL.md`
- `.github/skills/discord-sky-eval/SKILL.md`

## Testing

```bash
dotnet test tests/DiscordSky.Tests/DiscordSky.Tests.csproj
bash scripts/validate-release.sh
```

The second command is the release gate. It requires a sibling checkout at the revision in [scripts/steward-ref](scripts/steward-ref), audits transitive packages, builds the solution in Release, and checks the test results for both real Steward integration cases. No Discord or provider credentials are needed.

## Production Deployment

Production images are identified by the Sky git tree and pinned Steward revision. Use the hardened script or serialized GitHub Actions workflow. Do not
apply the public Kustomize tree or set the image directly as a normal deployment path; doing so can bypass
combined-runtime and private-binding validation.

From a clean committed tree, use values from the private environment inventory:

```bash
bash scripts/deploy.sh \
  --aks-resource-group <AKS_RESOURCE_GROUP> \
  --aks-cluster <AKS_CLUSTER_NAME> \
  --acr-name <ACR_NAME> \
  --image-name discordskybot \
  --image-tag <TREE_AND_STEWARD_TAG> \
  --include-steward \
  --steward-project ../discord-steward/src/DiscordSteward/DiscordSteward.csproj \
  --preserve-steward-profiles
```

The production workflow runs the same gate on PRs and main. Only main can deploy. An already live source tag must match the ready container's registry digest; a matching tag alone does not skip deployment checks. The deploy script validates manifests,
private binding/profile correspondence, the in-image Steward probe, and rollout health before reporting success.

After deployment, verify:

- exact revision and image;
- one Ready pod with zero restarts;
- `/livez` responds; `/healthz` reports Discord Connected and every configured Steward child healthy;
- startup auth/model checks succeeded;
- effective route, budget, and provider-guard configuration;
- no unexpected `llm_call`, `llm_provider_guard`, or runtime-resource failures.

## Repository Layout

```text
src/DiscordSky.Bot/
  Bot/                 Discord gateway ownership and delivery
  Configuration/       Typed options and policy
  Integrations/        Images, links, reactions, members, safety
  Memory/              Stores, filtering, scoring, logging, reception
  Models/              Runtime contracts
  Orchestration/       Creative, autonomy, Empire, impulse, context

tests/DiscordSky.Tests/ Focused and integration-style xUnit coverage
k8s/discord-sky/       Public AKS manifests and deployment documentation
scripts/               Hardened deployment and operational helpers
```
