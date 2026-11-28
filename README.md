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
- Docker and Azure CLI to build and publish release images, and a Linux host with Docker Compose 2.30 or later to run them.

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

Use Discord's **Reply** and say what you mean. A reply to Robotnik, or a reply to another member that also addresses him, gives him the selected message as context. He answers the new message. No special phrasing is required, and quoting a boast does not establish that a role, nickname, or channel actually changed.

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
Emoji reactions use the current conversation and its media without reading saved personal notes.

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
`WorldAutonomy:MemoryRecallEnabled=false`. See [Cache And Recall Rollout](k8s/discord-sky/README.md#cache-and-recall-rollout) for comparison queries and promotion gates.

Private profiles and guild IDs never enter public image layers or committed files. Production keeps them in the
host's private settings and a read-only profile mount. The release build probes the bundled Steward executable, and a
release is rolled back if the new container does not report every configured guild healthy.

## Observability And Privacy

Durable production evidence lives under the configured data paths (`/app/data/user_memories` in the container, `data/` in the host's deployment directory):

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

For one turn in an existing owner-private local snapshot, use the [turn inspector](scripts/inspect_turn.py). It joins routing, provider calls, reply references and delivery records without printing prompts or replies. See the [snapshot layout and examples](docs/maintenance/atlas.md#inspect-one-turn).

## Testing

```bash
dotnet test tests/DiscordSky.Tests/DiscordSky.Tests.csproj
bash scripts/validate-release.sh
```

The second command is the release gate. It requires a sibling checkout at the revision in [scripts/steward-ref](scripts/steward-ref), runs the offline rollout and turn-inspector checks, audits transitive packages, builds the solution in Release, and requires the real Steward catalog, provider-failure, interrupted-operation and natural-reply tests to pass. No Discord or provider credentials are needed.

## Production Deployment

Sky runs as one container: one Docker Compose service on a Linux host with Docker Compose 2.30 or later
([DS-011](docs/decisions/README.md#ds-011-run-production-as-one-compose-service-on-a-single-host)). Run exactly one
instance per bot token. Discord allows one gateway session per token, and the file journals assume a single writer.
Images are identified by the Sky git tree and pinned Steward revision: `discordskybot:tree-<tree>-s<steward7>`.

### Publish images

The [release workflow](.github/workflows/release.yml) runs the release gate on pull requests and main. On main it
builds the combined image with `scripts/deploy.sh --skip-rollout --include-steward`, runs the image smoke check and
Steward probe, and pushes the image to Azure Container Registry. An already published tree is not rebuilt. CI never
reaches the host. Publishing is skipped until the repository defines:

- variables `ACR_NAME` and `ACR_RESOURCE_GROUP`, plus `IMAGE_NAME` if it is not `discordskybot`;
- secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` for a Microsoft Entra app registration
  that has a federated credential for `repo:<owner>/<repo>:ref:refs/heads/main` and the `AcrPush` and `Reader` roles
  on the registry.

To build and publish an image by hand from a clean committed tree:

```bash
bash scripts/deploy.sh --skip-rollout \
  --acr-name <ACR_NAME> \
  --acr-resource-group <ACR_RESOURCE_GROUP> \
  --image-name discordskybot \
  --image-tag <TREE_AND_STEWARD_TAG> \
  --include-steward \
  --steward-project ../discord-steward/src/DiscordSteward/DiscordSteward.csproj
```

### Run on a host

Give the host a pull-only registry credential (for ACR, a repository-scoped token) and run `docker login` once. Then
create one root-owned directory, for example `/srv/sky`:

| Path | Contents |
| --- | --- |
| `compose.yaml` | [deploy/compose.yaml](deploy/compose.yaml) |
| `.env` | `SKY_IMAGE=<registry>/discordskybot@sha256:<digest>`; optional `SKY_HEALTH_PORT` (default 8080) |
| `public.env` | The released commit's public settings, rendered from `k8s/discord-sky/configmap.yaml` |
| `private.env` | Secrets and exact guild bindings from [deploy/private.env.example](deploy/private.env.example); mode 600 |
| `data/` | Durable state and evidence; owned by uid 1100, mode 700 |
| `profiles/` | Optional private Steward profiles, `<guild-id>.json`; owned by uid 1100, read-only |

Render `public.env` from a checkout, copy it to the host, then start the bot there:

```bash
git show <commit>:k8s/discord-sky/configmap.yaml | python3 deploy/render-public-env.py > public.env

# On the host, in /srv/sky:
sudo docker compose up -d
curl -fsS http://127.0.0.1:8080/healthz
```

The container runs as a non-root user with a read-only root filesystem, no capabilities, memory, CPU and process caps,
and its own network. It needs no inbound port; health is published only on the host's loopback.

### Release a published commit

1. Back up first: `docker compose stop`, copy `data/`, `profiles/` and the three env files, then `docker compose start`.
   The copy holds secrets and private evidence, so encrypt it.
2. Keep the current `.env` and `public.env`. Write the new digest to `.env` and render that commit's `public.env`.
3. Run `docker compose up -d`. Compose stops the old container before it starts the new one.
4. Wait until `/healthz` returns 200 with `"connection":"Connected"` and every configured guild healthy. Otherwise put
   the previous `.env` and `public.env` back and run `docker compose up -d` again.

Public settings change only through a commit to `k8s/discord-sky/configmap.yaml` and a release. Private settings
never come from this repository, and they override the public ones on the host.

After a release, verify:

- `SKY_IMAGE` names the intended digest, and `public.env` matches that commit's rendered settings;
- the container is running with zero unexpected restarts;
- `/livez` responds; `/healthz` reports Discord Connected and every configured Steward child healthy;
- startup auth/model checks succeeded;
- effective route, budget, and provider-guard configuration;
- no unexpected `llm_call`, `llm_provider_guard`, or runtime-resource failures.

The [ops skill](.github/skills/discord-sky-ops/SKILL.md) shows how to read each of these. The manifests in
`k8s/discord-sky/` and the AKS rollout mode of `scripts/deploy.sh` are kept for reference; CI does not exercise them.

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
deploy/                Compose deployment: compose file, public settings renderer, private settings template
k8s/discord-sky/       Public runtime settings (configmap.yaml) and reference AKS manifests
scripts/               Release build/publish, release gate and operational helpers
```
