"""Workflow that builds prompts and requests completions from OpenAI."""
from __future__ import annotations

import logging
from discord import Message

from ..config import Settings
from ..context import ConversationContextBuilder
from ..services.openai_chat import ChatCompletionResult, OpenAIChatService, OpenAIChatError

logger = logging.getLogger(__name__)


class ChatResponder:
    """Encapsulates the logic for building prompts based on channel history."""

    def __init__(
        self,
        settings: Settings,
        chat_service: OpenAIChatService,
        *,
        context_builder: ConversationContextBuilder | None = None,
    ) -> None:
        self._chat_service = chat_service
        self._context_builder = context_builder or ConversationContextBuilder(settings)

    def set_bot_user(self, user_id: int) -> None:
        logger.debug("ChatResponder bound to bot user id %s", user_id)
        self._context_builder.set_bot_user(user_id)

    async def respond(self, message: Message, middle_section: str) -> str:
        context = await self._context_builder.build(message, middle_section)
        prompt = context.render_prompt()
        try:
            completion: ChatCompletionResult = await self._chat_service.complete(prompt)
        except OpenAIChatError as exc:
            logger.exception("Chat completion failed: %s", exc)
            return f"Sorry, I encountered an error (status code {exc.status}). Please try again later."
        return completion.content
