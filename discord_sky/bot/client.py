"""Factory for configuring the Discord bot instance."""
from __future__ import annotations

import logging
from typing import Iterable

import discord
from discord.ext import commands

from ..config import Settings
from ..services.job_scraper import JobScraperService
from ..tasks.notifier import NotifierTask
from ..workflows.chat_responder import ChatResponder
from ..workflows.image_pipeline import ImagePipeline

logger = logging.getLogger(__name__)


def create_bot(
    *,
    settings: Settings,
    chat_responder: ChatResponder,
    image_pipeline: ImagePipeline,
    scraper: JobScraperService,
) -> tuple[commands.Bot, NotifierTask]:
    intents = discord.Intents.default()
    intents.message_content = True

    bot = commands.Bot(command_prefix=settings.bot_prefix, intents=intents)
    notifier = NotifierTask(bot=bot, settings=settings, scraper=scraper)

    allowed_channels = set(channel.lower() for channel in settings.bot_channels)

    @bot.event
    async def on_ready() -> None:  # type: ignore[override]
        assert bot.user is not None
        chat_responder.set_bot_user(bot.user.id)
        notifier.start()
        logger.info("Logged in as %s", bot.user)

    @bot.event
    async def on_message(message: discord.Message) -> None:  # type: ignore[override]
        if message.author == bot.user:
            return

        if _should_ignore_channel(message, allowed_channels):
            await bot.process_commands(message)
            return

        if message.attachments and message.content:
            image_attachments = _iter_image_attachments(message.attachments)
            first_image = next(image_attachments, None)
            if first_image:
                await image_pipeline.handle_message(message, first_image.url)
                await bot.process_commands(message)
                return

        content = message.content or ""
        prefix = settings.bot_prefix
        if content.startswith(f"{prefix}("):
            middle_section = content.split("(", 1)[1].split(")", 1)[0]
            await message.channel.send(await chat_responder.respond(message, middle_section))
        elif content.startswith(prefix):
            await message.channel.send(
                await chat_responder.respond(message, settings.chatgpt_user_specified_middle_section)
            )

        await bot.process_commands(message)

    return bot, notifier


def _should_ignore_channel(message: discord.Message, allowed_channels: Iterable[str]) -> bool:
    channel_name = getattr(message.channel, "name", None)
    if channel_name is None:
        return False
    return bool(allowed_channels) and channel_name.lower() not in allowed_channels


def _iter_image_attachments(attachments: Iterable[discord.Attachment]):
    image_extensions = (".png", ".jpg", ".jpeg", ".gif", ".webp")
    for attachment in attachments:
        filename = (attachment.filename or "").lower()
        if filename.endswith(image_extensions):
            yield attachment
