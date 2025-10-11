"""Application entrypoint for the Discord Sky bot."""
from __future__ import annotations

import asyncio
import logging

import aiohttp

from .agent import BotAgent
from .config import get_settings
from .logging import configure_logging
from .services.job_scraper import JobScraperService
from .services.openai_chat import OpenAIChatService
from .services.openai_images import OpenAIImageService
from .plugins import ChatPlugin, ImagePlugin, NotifierPlugin
from .workflows.chat_responder import ChatResponder
from .workflows.image_pipeline import ImagePipeline

logger = logging.getLogger(__name__)


async def async_main() -> None:
    configure_logging()
    settings = get_settings()

    timeout = aiohttp.ClientTimeout(total=settings.http_timeout_seconds)
    async with aiohttp.ClientSession(timeout=timeout) as session:
        chat_service = OpenAIChatService(
            api_key=settings.chatgpt_api_key,
            model=settings.chatgpt_model,
            session=session,
        )
        image_service = OpenAIImageService(
            api_key=settings.chatgpt_api_key,
            model=settings.openai_image_model,
            size=settings.openai_image_size,
            session=session,
        )
        scraper_service = JobScraperService(session=session)

        chat_responder = ChatResponder(settings, chat_service)
        image_pipeline = ImagePipeline(image_service)

        agent = BotAgent(settings=settings)
        await agent.add_plugin(ImagePlugin(image_pipeline))
        await agent.add_plugin(ChatPlugin(settings, chat_responder))
        await agent.add_plugin(NotifierPlugin(settings, scraper_service))

        try:
            await agent.start()
        finally:
            await agent.close()


def main() -> None:
    asyncio.run(async_main())


if __name__ == "__main__":  # pragma: no cover
    main()
