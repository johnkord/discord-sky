"""Async OpenAI image generation service."""
from __future__ import annotations

import io
import json
import logging
from dataclasses import dataclass
from typing import Any, Dict, Optional

import aiohttp

logger = logging.getLogger(__name__)


class OpenAIImageError(RuntimeError):
    """Raised when the OpenAI image API responds with an error."""

    def __init__(self, status: int, body: str | Dict[str, Any]):
        self.status = status
        self.body = body
        super().__init__(f"OpenAI image generation failed with status {status}")


@dataclass
class ImageGenerationResult:
    """Container for an image generation response."""

    filename: str
    prompt: str
    bytes_io: io.BytesIO


class OpenAIImageService:
    """Async helper for OpenAI image generation endpoints."""

    _IMAGE_URL = "https://api.openai.com/v1/images/generations"

    def __init__(
        self,
        *,
        api_key: str,
        model: str,
        size: str,
        session: aiohttp.ClientSession,
    ) -> None:
        self._api_key = api_key
        self._model = model
        self._size = size
        self._session = session

    @property
    def headers(self) -> Dict[str, str]:
        return {
            "Authorization": f"Bearer {self._api_key}",
            "Content-Type": "application/json",
        }

    async def generate(self, prompt: str, reference_image_url: Optional[str] = None) -> ImageGenerationResult:
        payload: Dict[str, Any] = {
            "model": self._model,
            "prompt": prompt,
            "n": 1,
            "size": self._size,
            "response_format": "url",
        }
        if reference_image_url:
            payload["reference_image"] = reference_image_url

        logger.debug("Submitting image generation payload: %s", payload)

        async with self._session.post(self._IMAGE_URL, headers=self.headers, json=payload) as response:
            status = response.status
            body_text = await response.text()
            if status != 200:
                logger.error("OpenAI image error (%s): %s", status, body_text)
                raise OpenAIImageError(status, body_text)

            data: Dict[str, Any] = json.loads(body_text)
            logger.debug("Received image generation payload: %s", data)

        try:
            image_url = data["data"][0]["url"]
        except (KeyError, IndexError) as exc:
            raise OpenAIImageError(status, data) from exc

        async with self._session.get(image_url) as image_response:
            image_status = image_response.status
            if image_status != 200:
                body = await image_response.text()
                logger.error("Failed to download generated image (%s): %s", image_status, body)
                raise OpenAIImageError(image_status, body)
            content = await image_response.read()

        return ImageGenerationResult(
            filename="generated_image.png",
            prompt=prompt,
            bytes_io=io.BytesIO(content),
        )
