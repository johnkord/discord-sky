# Discord Sky Bot

Discord Sky is a mischievous Discord companion inspired by the "Discord Sky – Mischief-Made Muse" vision. The bot keeps creative communities buzzing with playful prompts, collaborative quests, and friendly heckles that spark activity without crossing the spam line.

## Highlights
- **Sky Persona Prompt (`!sky(persona) [topic]`)** – Ask the bot to speak in the voice of any character you name. Personas are optional; if you leave them out, the bot falls back to the configured default ("Weird Al" unless you change `Bot:DefaultPersona`). Put the persona inside parentheses so it can include spaces; the optional remainder nudges the topic; otherwise the persona just riffs with the chat.
- **Conversation-Aware Replies** – The bot blends in recent Discord chatter so every response feels grounded in the thread.
- **Guardrails with Glitter** – Configurable chaos level, quiet hours, rate limits, and ban-word filters keep the fun safe.
- **OpenAI-Powered Brain** – A unified orchestrator blends Discord history and OpenAI models to craft every reply.

## Prerequisites
- [.NET SDK 8.0](https://dotnet.microsoft.com/download) or newer
- A Discord bot token with the **Message Content Intent** enabled
- An OpenAI (or Azure OpenAI) API key with access to chat models

## Getting Started
1. **Install dependencies**
   ```bash
   dotnet restore
   ```
2. **Configure credentials**
   - Copy `src/DiscordSky.Bot/appsettings.json` to `appsettings.Development.json` (ignored by git).
   - Populate the `Bot:Token` with your Discord bot token.
   - Fill `OpenAI:ApiKey` (and override `Endpoint`/`ChatModel` if you are using Azure OpenAI).
   - Adjust the `Chaos` section to fit your server's vibe (annoyance level, quiet hours, ban words, prompt budgets).
   - (Optional) Fill `Bot:AllowedChannelNames` with the channel names the bot may respond in. Leave empty to allow all.
  
  
  
3. **Run the bot**
   ```bash
   dotnet run --project src/DiscordSky.Bot
   ```
4. **Invite to your server** and try `!sky(bard) Tell the tavern what you saw in the forest.` in a channel where the bot has permission to read and post messages.

## Usage
```
!sky(persona) [topic]
```

- `persona` *(optional)*: the character or archetype you want the bot to embody (e.g., `bard`, `grizzled captain`, `hyper-AI`). If omitted, the bot uses the configured default persona (`Bot:DefaultPersona` in settings, "Weird Al" out of the box).
- `topic` *(optional)*: what you want the persona to address. Leave it blank to let the persona respond naturally to the recent chat. Attachments are summarized and passed along automatically.

Examples: 
- `!sky(noir detective) Give me a recap of this thread.`
- `!sky(chaotic bard)`
- `!sky Tell us a tour story from the road.`

## Testing
Run the smoke tests to validate configuration helpers and safety rails:
```bash
dotnet test
```

## AKS Deployment
Use the helper script in `scripts/deploy.sh` to build, publish, and roll out a new container to your AKS cluster.

1. Ensure `k8s/discord-sky/secret.yaml` exists locally with real secrets (it stays untracked).
2. Log in with `az login` and make sure you have `az`, `docker`, `kubectl`, and `dotnet` on your PATH.
3. Run the script, substituting values from your private ops note:
   ```bash
   ./scripts/deploy.sh \
     --subscription-id <AZURE_SUBSCRIPTION_ID> \
     --aks-resource-group <AKS_RESOURCE_GROUP> \
     --aks-cluster <AKS_CLUSTER_NAME> \
     --acr-name <ACR_NAME> \
     --image-tag <TAG>
   ```

Flags such as `--acr-resource-group`, `--image-name`, `--project`, and `--skip-build` are available for advanced scenarios. The script rebuilds the bot, pushes an image to ACR, applies the manifests via Kustomize, and waits for the rollout to complete.

## Project Layout
```
src/DiscordSky.Bot/        # Discord bot runtime and creative orchestrator
   ├── Bot/                 # Discord client wiring and command handling
   ├── Configuration/       # Options and safeguards (chaos budgets, tokens, OpenAI)
   ├── Integrations/        # Typed clients for OpenAI
   ├── Models/              # Request/response contracts and orchestrator payloads
   └── Orchestration/       # Context aggregation, safety filtering, prompt repository

tests/DiscordSky.Tests/    # xUnit smoke tests for core services
```

## Next Steps
- Persist heckle reminders and quest progress to durable storage for real scheduling.
- Experiment with lightweight knowledge summaries generated from pinned messages or custom JSON feeds.
- Add leaderboard tracking and custom role assignment for Mischief Quests.

Happy chaos crafting! ✨
