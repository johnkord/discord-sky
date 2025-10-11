"""Core abstractions for composing the Discord bot as a modular agent."""
from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass, field
from typing import Iterable, List, Optional

import discord
from discord.ext import commands

from ..config import Settings

logger = logging.getLogger(__name__)


class AgentPlugin:
    """Base class for modular bot behaviour.

    Subclasses can override lifecycle hooks to participate in the bot run loop.
    Hooks are awaited sequentially based on plugin priority ordering.
    """

    name: str = "plugin"
    priority: int = 100

    async def on_loaded(self, agent: BotAgent) -> None:  # pragma: no cover - default noop
        """Called after the plugin is registered but before the bot starts."""

    async def on_ready(self, agent: BotAgent) -> None:  # pragma: no cover - default noop
        """Called once Discord reports that the bot connection is ready."""

    async def handle_message(self, agent: BotAgent, message: discord.Message) -> bool:
        """Handle an incoming message.

        Return ``True`` when the message has been fully handled and should not be passed
        to subsequent plugins.
        """

        return False

    async def on_shutdown(self, agent: BotAgent) -> None:  # pragma: no cover - default noop
        """Called during graceful shutdown"""


@dataclass(order=True)
class PluginHandle:
    """Internal wrapper used to keep track of plugin ordering and metadata."""

    priority: int
    plugin: AgentPlugin = field(compare=False)

    @property
    def name(self) -> str:
        return getattr(self.plugin, "name", self.plugin.__class__.__name__)


class BotAgent:
    """High-level orchestrator that wires Discord events to registered plugins."""

    def __init__(
        self,
        *,
        settings: Settings,
        intents: Optional[discord.Intents] = None,
    ) -> None:
        self._settings = settings
        resolved_intents = intents or discord.Intents.default()
        if not resolved_intents.message_content:
            resolved_intents.message_content = True
        self._bot = commands.Bot(command_prefix=settings.bot_prefix, intents=resolved_intents)
        self._plugins: List[PluginHandle] = []
        self._ready_event = asyncio.Event()
        self._closed = False

        self._bot.add_listener(self._on_ready, name="on_ready")
        self._bot.add_listener(self._on_message, name="on_message")

    @property
    def settings(self) -> Settings:
        return self._settings

    @property
    def client(self) -> commands.Bot:
        return self._bot

    @property
    def plugins(self) -> Iterable[AgentPlugin]:
        return (handle.plugin for handle in self._plugins)

    async def add_plugin(self, plugin: AgentPlugin) -> None:
        """Register a plugin with the agent and invoke its ``on_loaded`` hook."""

        handle = PluginHandle(priority=getattr(plugin, "priority", 100), plugin=plugin)
        self._plugins.append(handle)
        self._plugins.sort()
        logger.debug("Registered plugin %s (priority %s)", handle.name, handle.priority)
        await plugin.on_loaded(self)

    async def start(self) -> None:
        """Start the underlying Discord bot using the configured token."""

        logger.info("Starting bot agent with %s plugins", len(self._plugins))
        await self._bot.start(self._settings.bot_token)

    async def close(self) -> None:
        """Stop the Discord bot and dispatch plugin shutdown hooks."""

        if self._closed:
            return
        self._closed = True
        logger.info("Shutting down bot agent")
        for handle in reversed(self._plugins):
            try:
                await handle.plugin.on_shutdown(self)
            except Exception:  # pragma: no cover - defensive
                logger.exception("Plugin %s failed during shutdown", handle.name)
        await self._bot.close()

    async def wait_until_ready(self) -> None:
        await self._ready_event.wait()

    async def _on_ready(self) -> None:
        logger.info("Discord connection ready; initialising plugins")
        for handle in self._plugins:
            try:
                await handle.plugin.on_ready(self)
            except Exception:  # pragma: no cover - defensive
                logger.exception("Plugin %s failed during on_ready hook", handle.name)
        self._ready_event.set()

    async def _on_message(self, message: discord.Message) -> None:
        if message.author == self._bot.user:
            return

        for handle in self._plugins:
            try:
                handled = await handle.plugin.handle_message(self, message)
            except Exception:  # pragma: no cover - defensive
                logger.exception("Plugin %s failed while handling message", handle.name)
                handled = True
            if handled:
                break

        await self._bot.process_commands(message)