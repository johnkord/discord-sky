"""Async OpenAI Chat Completions service."""
from __future__ import annotations

import json
import logging
from dataclasses import dataclass
from typing import Any, Dict

import aiohttp

logger = logging.getLogger(__name__)


class OpenAIChatError(RuntimeError):
    """Raised when the OpenAI chat API responds with an error."""

    def __init__(self, status: int, body: str | Dict[str, Any]):
        self.status = status
        self.body = body
        message = f"OpenAI chat completion failed with status {status}"
        super().__init__(message)


@dataclass
class ChatCompletionResult:
    """Container for chat completion content and raw payload."""

    content: str
    raw: Dict[str, Any]


class OpenAIChatService:
    """Thin async wrapper around the OpenAI chat completions endpoint."""

    _CHAT_URL = "https://api.openai.com/v1/chat/completions"

    def __init__(
        self,
        *,
        api_key: str,
        model: str,
        session: aiohttp.ClientSession,
        max_message_length: int = 2000,
    ) -> None:
        self._api_key = api_key
        self._model = model
        self._session = session
        self._max_message_length = max_message_length

    @property
    def headers(self) -> Dict[str, str]:
        return {
            "Authorization": f"Bearer {self._api_key}",
            "Content-Type": "application/json",
        }

    async def complete(self, prompt: str) -> ChatCompletionResult:
        payload = {
            "model": self._model,
            "messages": [
                {
                    "role": "user",
                    "content": prompt,
                }
            ],
        }

        logger.debug("Submitting chat completion payload: %s", payload)

        async with self._session.post(self._CHAT_URL, headers=self.headers, json=payload) as response:
            status = response.status
            body_text = await response.text()
            if status != 200:
                logger.error("OpenAI chat error (%s): %s", status, body_text)
                raise OpenAIChatError(status, body_text)

            data: Dict[str, Any] = json.loads(body_text)
            logger.debug("Received chat completion payload: %s", data)

        try:
            content = data["choices"][0]["message"]["content"]
        except (KeyError, IndexError) as exc:
            raise OpenAIChatError(status, data) from exc

        if len(content) >= self._max_message_length:
            content = content[: self._max_message_length - 4] + "..."

        content = content.replace("\\n", "\n")
        return ChatCompletionResult(content=content, raw=data)
