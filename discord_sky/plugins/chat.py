"""Plugin that handles conversational text commands via the chat responder."""
from __future__ import annotations

import logging
from typing import Iterable, Set

import discord

from ..agent import AgentPlugin, BotAgent
from ..config import Settings
from ..workflows.chat_responder import ChatResponder

logger = logging.getLogger(__name__)


class ChatPlugin(AgentPlugin):
    """Routes prefixed messages to the ``ChatResponder`` workflow."""

    name = "chat"
    priority = 200

    def __init__(self, settings: Settings, responder: ChatResponder) -> None:
        self._settings = settings
        self._responder = responder
        self._allowed_channels: Set[str] = {channel.lower() for channel in settings.bot_channels}
        self._image_extensions = (".png", ".jpg", ".jpeg", ".gif", ".webp")

    async def on_ready(self, agent: BotAgent) -> None:
        bot_user = agent.client.user
        if bot_user:
            logger.debug("Binding ChatResponder to bot user id %s", bot_user.id)
            self._responder.set_bot_user(bot_user.id)

    async def handle_message(self, agent: BotAgent, message: discord.Message) -> bool:
        if self._should_ignore_channel(message):
            return False
        if message.attachments and self._contains_image(message.attachments):
            # Let the image plugin handle attachment scenarios that include images.
            return False

        content = message.content or ""
        prefix = self._settings.bot_prefix
        if not content.startswith(prefix):
            return False

        if content.startswith(f"{prefix}("):
            middle_section = content.split("(", 1)[1].split(")", 1)[0]
        else:
            middle_section = self._settings.chatgpt_user_specified_middle_section

        logger.debug("ChatPlugin handling message from %s", message.author)
        response = await self._responder.respond(message, middle_section)
        await message.channel.send(response)
        return True

    def _should_ignore_channel(self, message: discord.Message) -> bool:
        channel_name = getattr(message.channel, "name", None)
        if channel_name is None or not self._allowed_channels:
            return False
        return channel_name.lower() not in self._allowed_channels

    def _contains_image(self, attachments: Iterable[discord.Attachment]) -> bool:
        for attachment in attachments:
            filename = (attachment.filename or "").lower()
            if filename.endswith(self._image_extensions):
                return True
        return False