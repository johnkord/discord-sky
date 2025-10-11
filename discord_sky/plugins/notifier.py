"""Plugin that wires the background notifier task into the agent lifecycle."""
from __future__ import annotations

import logging
from typing import Optional

from ..agent import AgentPlugin, BotAgent
from ..config import Settings
from ..services.job_scraper import JobScraperService
from ..tasks.notifier import NotifierTask

logger = logging.getLogger(__name__)


class NotifierPlugin(AgentPlugin):
    """Starts and stops the notifier background task depending on configuration."""

    name = "notifier"
    priority = 400

    def __init__(self, settings: Settings, scraper: JobScraperService) -> None:
        self._settings = settings
        self._scraper = scraper
        self._task: Optional[NotifierTask] = None

    async def on_loaded(self, agent: BotAgent) -> None:
        if not self._settings.notifier_enabled:
            logger.info("NotifierPlugin disabled; required settings missing")
            return
        self._task = NotifierTask(bot=agent.client, settings=self._settings, scraper=self._scraper)
        logger.debug("NotifierPlugin initialised background task")

    async def on_ready(self, agent: BotAgent) -> None:
        if self._task:
            logger.info("Starting notifier background loop")
            self._task.start()

    async def on_shutdown(self, agent: BotAgent) -> None:
        if self._task:
            logger.info("Stopping notifier background loop")
            self._task.stop()