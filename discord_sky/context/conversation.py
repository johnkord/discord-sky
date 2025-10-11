"""Conversation context assembly for OpenAI chat interactions."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import List, Optional, Protocol, Sequence

from discord import Message

from ..config import Settings


BLOCK_SEPARATOR = " \n "


@dataclass(slots=True)
class ContextMessage:
    """Structured representation of a message included in the OpenAI context."""

    author: str
    content: str


@dataclass(slots=True)
class ToolResult:
    """Represents a tool result bundled into the OpenAI request."""

    name: str
    content: str


class ToolContextProvider(Protocol):
    """Callable responsible for providing tool results relevant to a Discord message."""

    async def gather(self, message: Message) -> Sequence[ToolResult]:
        ...


@dataclass(slots=True)
class ConversationContext:
    """Container describing the full payload sent to OpenAI."""

    prompt_prefix: str
    middle_section: str
    prompt_suffix: str
    history: Sequence[ContextMessage] = field(default_factory=tuple)
    tool_results: Sequence[ToolResult] = field(default_factory=tuple)

    def render_prompt(self) -> str:
        """Compose the string prompt for traditional completion endpoints."""

        base_prompt = f"{self.prompt_prefix}{self.middle_section}{self.prompt_suffix}"
        segments: List[str] = []

        if self.history:
            history_text = BLOCK_SEPARATOR.join(f"{entry.author}: {entry.content}" for entry in self.history)
            segments.append(history_text)

        if self.tool_results:
            tool_text = BLOCK_SEPARATOR.join(
                f"[Tool:{result.name}] {result.content}" for result in self.tool_results
            )
            segments.append(tool_text)

        if not segments:
            return base_prompt

        return f"{base_prompt}{BLOCK_SEPARATOR}{BLOCK_SEPARATOR.join(segments)}"


class ConversationContextBuilder:
    """Orchestrates selection of channel history and tool data for OpenAI calls."""

    def __init__(
        self,
        settings: Settings,
        *,
        history_character_limit: int = 10_000,
        tool_providers: Optional[Sequence[ToolContextProvider]] = None,
    ) -> None:
        self._settings = settings
        self._history_character_limit = history_character_limit
        self._tool_providers = list(tool_providers or [])
        self._bot_user_id: Optional[int] = None

    def set_bot_user(self, user_id: int) -> None:
        self._bot_user_id = user_id

    def add_tool_provider(self, provider: ToolContextProvider) -> None:
        """Register an additional tool context provider at runtime."""

        self._tool_providers.append(provider)

    async def build(self, message: Message, middle_section: str) -> ConversationContext:
        history = await self._collect_history(message, middle_section)
        tools = await self._gather_tool_results(message)
        return ConversationContext(
            prompt_prefix=self._settings.chatgpt_prompt_prefix,
            middle_section=middle_section,
            prompt_suffix=self._settings.chatgpt_prompt_suffix,
            history=history,
            tool_results=tools,
        )

    async def _collect_history(self, message: Message, middle_section: str) -> Sequence[ContextMessage]:
        prompt_prefix = self._settings.chatgpt_prompt_prefix
        prompt_suffix = self._settings.chatgpt_prompt_suffix
        base_prompt = f"{prompt_prefix}{middle_section}{prompt_suffix}"

        history_messages: List[Message] = []
        bot_message_counts: dict[str, int] = {}
        bot_messages_content: List[str] = []

        async for past_message in message.channel.history(limit=self._settings.bot_context):
            content = past_message.content or ""
            if self._is_from_bot(past_message):
                bot_messages_content.append(content)
                continue
            if content.startswith(self._settings.bot_prefix):
                continue
            history_messages.append(past_message)
            bot_message_counts.setdefault(content, 0)

        for human_message in history_messages:
            content_lower = (human_message.content or "").lower()
            if len(content_lower) < 5:
                continue
            for bot_message in bot_messages_content:
                if f"{content_lower}\n" in bot_message.lower():
                    bot_message_counts[human_message.content or ""] += 1

        ignored_contents = {
            content_text
            for content_text, count in bot_message_counts.items()
            if count >= self._settings.bot_message_limit
        }

        final_history: List[ContextMessage] = []
        character_budget = len(base_prompt) + 1
        for past_message in history_messages:
            content = past_message.content or ""
            if content in ignored_contents:
                continue
            entry = ContextMessage(author=past_message.author.name, content=content)
            character_budget += len(entry.author) + len(entry.content) + 2
            if character_budget > self._history_character_limit:
                break
            final_history.append(entry)

        final_history.reverse()
        return tuple(final_history)

    async def _gather_tool_results(self, message: Message) -> Sequence[ToolResult]:
        if not self._tool_providers:
            return ()
        gathered: List[ToolResult] = []
        for provider in self._tool_providers:
            results = await provider.gather(message)
            gathered.extend(results)
        return tuple(gathered)

    def _is_from_bot(self, message: Message) -> bool:
        if self._bot_user_id is None:
            return bool(getattr(message.author, "bot", False))
        return message.author.id == self._bot_user_id