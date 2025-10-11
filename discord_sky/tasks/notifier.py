"""Background task for dispatching periodic job updates via DM."""
from __future__ import annotations

import logging
from typing import Optional

import discord
from discord.ext import tasks

from ..config import Settings
from ..services.job_scraper import JobScraperService

logger = logging.getLogger(__name__)


class NotifierTask:
    """Encapsulates the periodic DM notifier logic."""

    def __init__(
        self,
        *,
        bot: discord.Client,
        settings: Settings,
        scraper: JobScraperService,
    ) -> None:
        self._bot = bot
        self._settings = settings
        self._scraper = scraper
        self._loop: Optional[tasks.Loop] = None
        if settings.notifier_enabled:
            self._loop = tasks.loop(minutes=settings.minutes_between_messages)(self._run)

    def start(self) -> None:
        if not self._loop:
            logger.info("Notifier task disabled; required settings missing")
            return
        if self._loop.is_running():
            return
        logger.info("Starting notifier task with %s-minute interval", self._settings.minutes_between_messages)
        self._loop.start()

    def stop(self) -> None:
        if self._loop and self._loop.is_running():
            self._loop.stop()

    async def _run(self) -> None:
        try:
            await self._dispatch_notifications()
        except Exception:  # pragma: no cover - defensive guard
            logger.exception("Notifier task encountered an error")

    async def _dispatch_notifications(self) -> None:
        assert self._settings.notifier_enabled  # for type-checkers

        html = await self._scraper.fetch_html(self._settings.url_to_fetch)
        postings = self._scraper.parse_job_section(html)
        general_message = self._scraper.format_postings(postings, "Job Titles & Shift Types:")

        html2 = await self._scraper.fetch_html(self._settings.url_to_fetch2)
        critical_postings = self._scraper.parse_job_section(html2, filter_keyword="icu")
        if not critical_postings:
            critical_postings = self._scraper.parse_job_section(html2, filter_keyword="critical care")
        critical_message = self._scraper.format_postings(critical_postings, "Critical Care/ICU Jobs:")

        user = await self._bot.fetch_user(self._settings.target_user_id)
        if not user:
            logger.warning("Notifier could not resolve user with id %s", self._settings.target_user_id)
            return

        await user.send(general_message)
        await user.send(critical_message)
        logger.info("Notifier dispatched job updates to %s", self._settings.target_user_id)
