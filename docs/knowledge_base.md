# Discord Sky – User Story Overview

## Quick summary
Discord Sky is a conversational companion for Discord communities. Once invited to a server and provided with API credentials, it joins specified channels and acts as a smart collaborator: responding to guided prompts, remixing images, and optionally delivering curated job leads directly to members.

## Who benefits
- **Moderators and admins** who want a controllable AI assistant that stays within dedicated channels.
- **Community members** who need quick brainstorming, writing help, or code snippets without leaving Discord.
- **Job seekers** who appreciate timely, private digests of new opportunities.

## Core user flows
### 1. Guided AI chat
1. A member types the configured prefix (default: `!react(...)`) with their request inside the parentheses.
2. Discord Sky stitches together the prefix, the member’s request, and optional suffix guidance defined by admins.
3. The bot replies in-channel with an OpenAI-generated message that respects recent conversation history, keeping the exchange contextual and on-topic.

### 2. Image remixing
1. A member drops an image in a channel and adds a descriptive caption.
2. Discord Sky sends both the visual and textual cues to OpenAI’s image generator.
3. The bot posts a fresh image back to the same channel, effectively turning a rough concept into AI-assisted artwork.

### 3. Scheduled opportunity digests (optional)
1. An admin enables the notifier by supplying the target member ID, fetch frequency, and one or more job source URLs.
2. Discord Sky periodically gathers highlights that match the configured criteria.
3. The bot delivers a direct message summary, so members receive curated leads without monitoring multiple sites.

## Onboarding snapshot
1. **Prepare credentials** – Collect the Discord bot token and OpenAI API keys, then copy `env_template.sh` to `env.sh` and fill in the required values (channels, prefixes, model choices).
2. **Choose a runtime** – Run `./run_sky.sh` on a local machine, launch the provided Docker image, or deploy the supplied Kubernetes manifest.
3. **Invite and configure** – Add the bot to your server, confirm it has access to the intended channels, and verify the command prefix or notifier settings align with your community norms.

## Everyday expectations
- Conversations stay in the channels you list; the bot is quiet elsewhere.
- Responses are shaped by the surrounding chat history as well as your predefined prompt framing.
- Image generations respect the size/model defaults you set but can be updated later without code changes.
- Opt-in job notifications only start once all required notifier settings are present, keeping the feature unobtrusive by default.

Use this guide when introducing Discord Sky to new teammates or documenting what the assistant can do for your community at a glance.