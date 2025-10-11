"""Plugin that orchestrates the OpenAI image generation workflow."""
from __future__ import annotations

import logging
from typing import Iterable

import discord

from ..agent import AgentPlugin, BotAgent
from ..workflows.image_pipeline import ImagePipeline

logger = logging.getLogger(__name__)


class ImagePlugin(AgentPlugin):
    """Handles messages containing both text and image attachments."""

    name = "image"
    priority = 100

    def __init__(self, pipeline: ImagePipeline) -> None:
        self._pipeline = pipeline
        self._image_extensions = (".png", ".jpg", ".jpeg", ".gif", ".webp")

    async def handle_message(self, agent: BotAgent, message: discord.Message) -> bool:
        if not message.attachments or not message.content:
            return False

        attachment = self._first_image(message.attachments)
        if not attachment:
            return False

        logger.debug("ImagePlugin handling message %s", message.id)
        await self._pipeline.handle_message(message, attachment.url)
        return True

    def _first_image(self, attachments: Iterable[discord.Attachment]) -> discord.Attachment | None:
        for attachment in attachments:
            filename = (attachment.filename or "").lower()
            if filename.endswith(self._image_extensions):
                return attachment
        return None