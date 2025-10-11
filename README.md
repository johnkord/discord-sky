# Discord Sky Bot

Discord Sky is a mischievous Discord companion inspired by the "Discord Sky – Mischief-Made Muse" vision. The bot keeps creative communities buzzing with playful prompts, collaborative quests, and friendly heckles that spark activity without crossing the spam line.

## Highlights
- **Bit-starter Grenade (`!chaos <topic>`)** – Spins over-the-top lore drops and mini scripts tailored to your crew.
- **Generative Gremlin Studio (`!remix`)** – Turns message attachments into remix ideas for art, memes, or audio hooks.
- **Heckle & Hype Cycle (`!heckle`, `!heckle-done`)** – Sends mischievous reminders and celebratory confetti when tasks ship.
- **Snackable Mischief Quests (`!quest`)** – Deals quick quests that reward goblins with badges, roasts, or lore drops.
- **Guardrails with Glitter** – Configurable chaos level, quiet hours, and ban-word filters keep the fun safe.

## Prerequisites
- [.NET SDK 8.0](https://dotnet.microsoft.com/download) or newer
- A Discord bot token with the **Message Content Intent** enabled

## Getting Started
1. **Install dependencies**
   ```bash
   dotnet restore
   ```
2. **Configure credentials**
   - Copy `src/DiscordSky.Bot/appsettings.json` to `appsettings.Development.json` (ignored by git).
   - Populate the `Bot:Token` with your Discord bot token.
   - (Optional) Fill `Bot:AllowedChannelNames` with the channel names the bot may respond in. Leave empty to allow all.
   - Adjust the `Chaos` section to fit your server's vibe (annoyance level, quiet hours, ban words).
3. **Run the bot**
   ```bash
   dotnet run --project src/DiscordSky.Bot
   ```
4. **Invite to your server** and try the commands in a channel where the bot has permission to read and post messages.

## Testing
Run the smoke tests to validate the creative engines:
```bash
dotnet test
```

## Project Layout
```
src/DiscordSky.Bot/        # Discord bot runtime and creative services
  ├── Bot/                 # Discord client wiring and command handling
  ├── Configuration/       # Options and safeguards (chaos budgets, tokens)
  ├── Models/              # Request/response contracts for creative modules
  └── Services/            # Feature engines for chaos, gremlin remixes, heckles, quests

tests/DiscordSky.Tests/    # xUnit smoke tests for core services
```

## Next Steps
- Persist heckle reminders and quest progress to storage for real scheduling.
- Expand `/remix` support with actual image/audio generation pipelines.
- Add leaderboard tracking and custom role assignment for Mischief Quests.

Happy chaos crafting! ✨
