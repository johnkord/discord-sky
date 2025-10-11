"""Workflow handling image generation requests."""
from __future__ import annotations

import logging

import discord

from ..services.openai_images import OpenAIImageError, OpenAIImageService

logger = logging.getLogger(__name__)


class ImagePipeline:
    """Coordinates prompt validation and OpenAI image generation."""

    def __init__(self, image_service: OpenAIImageService) -> None:
        self._image_service = image_service

    async def handle_message(self, message: discord.Message, reference_url: str) -> None:
        prompt = message.content.strip()
        if len(prompt) < 3:
            await message.channel.send(
                "Please provide a more detailed description for the image generation."
            )
            return

        status_message = await message.channel.send(
            "Generating image based on your prompt... This may take a moment."
        )

        try:
            result = await self._image_service.generate(prompt, reference_url)
        except OpenAIImageError as exc:
            logger.exception("Image generation failed: %s", exc)
            await message.channel.send(
                f"Sorry, I encountered an error generating the image (status code {exc.status})."
            )
            await status_message.delete()
            return
        except Exception as exc:  # pragma: no cover - safety net
            logger.exception("Unexpected error during image generation: %s", exc)
            await message.channel.send(
                "Sorry, an unexpected error occurred while generating the image."
            )
            await status_message.delete()
            return

        try:
            discord_file = discord.File(result.bytes_io, filename=result.filename)
            await message.channel.send(
                f'Generated image based on: "{prompt}"', file=discord_file
            )
        finally:
            await status_message.delete()
