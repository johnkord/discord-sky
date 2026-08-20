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
- Full Sol/xhigh autonomy with deferred native Steward tools for bound guilds.
- Per-user factual, experiential, running-bit, meta, and suppression memories.
- GPT Image generation with one shared budget and provider guard.
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

All active-provider chat calls and OpenAI images share one persistent `LlmProviderGuard`. It enforces quota/auth
circuit behavior, conservative in-flight reservations, and hourly/daily estimated spend ceilings. World-autonomy
route budgets are separate so scarce full-agent attention can degrade gracefully to conversation.

See [docs/runtime_architecture.md](docs/runtime_architecture.md) and
[docs/autonomy_routing_cost_controls_2026-08-03.md](docs/autonomy_routing_cost_controls_2026-08-03.md).

## Requirements

- .NET SDK 8.0 for Discord Sky.
- .NET SDK 10.0 when building the pinned Discord Steward child locally.
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

## Model Routing

Model names and reasoning effort are configuration, not hard-coded behavior. Production uses typed workload
profiles for `Main`, `Ambient`, `Utility`, `ColdOpen`, `ColdOpenCritic`, `ImageRewrite`, `MemoryExtraction`, and
`MemoryConsolidation`.

The active provider may be OpenAI or another configured compatible provider. Per-request `ChatOptions.ModelId`
selects the workload model through one shared telemetry and guard boundary.

## Memory

Conversation windows are extracted into typed per-user memories. Suppressed, superseded, meta, and
instruction-shaped entries are filtered before ambient use. The extraction opportunity gate supports `Off`,
`Shadow`, and `Live` modes plus bounded exploration.

The dominant world-autonomy routes do not yet receive memory text. Production computes a shadow-only, bounded
continuity candidate and records selected memory IDs and a digest for relevance review. It does not change
Robotnik's prompt.

## World Autonomy And Steward

World autonomy is dark unless an exact guild binding exists in private runtime configuration. Each enabled guild
gets one isolated Steward child with its own profile and durable paths. All native tools remain callable through
that child, while the model initially receives deferred hosted tool-search schemas rather than every schema at once.

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
dotnet build src/DiscordSky.Bot/DiscordSky.Bot.csproj -c Release
```

The existing AngleSharp `NU1902` advisory is a known repository warning and should not be confused with a change
introduced by unrelated work.

## Production Deployment

Production deploys are commit-derived and use the hardened script or serialized GitHub Actions workflow. Do not
apply the public Kustomize tree or set the image directly as a normal deployment path; doing so can bypass
combined-runtime and private-binding validation.

From a clean committed tree, use values from the private environment inventory:

```bash
bash scripts/deploy.sh \
  --aks-resource-group <AKS_RESOURCE_GROUP> \
  --aks-cluster <AKS_CLUSTER_NAME> \
  --acr-name <ACR_NAME> \
  --image-name discordskybot \
  --image-tag <COMMIT_DERIVED_TAG> \
  --include-steward \
  --steward-project ../discord-steward/src/DiscordSteward/DiscordSteward.csproj \
  --preserve-steward-profiles
```

The production workflow pins the Steward revision and generates a combined image tag. It validates manifests,
private binding/profile correspondence, the in-image Steward probe, and rollout health before reporting success.

After deployment, verify:

- exact revision and image;
- one Ready pod with zero restarts;
- `/healthz` reports Discord Connected and every configured Steward child healthy;
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
