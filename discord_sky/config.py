"""Application configuration powered by Pydantic settings."""
from __future__ import annotations

import json
from functools import lru_cache
from typing import List, Optional, Sequence

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict
from pydantic_settings.sources import EnvSettingsSource


class LenientEnvSettingsSource(EnvSettingsSource):
    """Environment source that falls back to comma splitting for list fields."""

    def decode_complex_value(self, field_name, field, value):  # type: ignore[override]
        stripped = value.strip() if isinstance(value, str) else value
        try:
            return super().decode_complex_value(field_name, field, stripped)
        except ValueError:
            if isinstance(stripped, str) and "," in stripped:
                return [part.strip() for part in stripped.split(",") if part.strip()]
            return stripped


class Settings(BaseSettings):
    """Strongly-typed environment configuration for the Discord bot."""

    model_config = SettingsConfigDict(
        case_sensitive=False,
        env_parse_delimiter=",",
    )

    bot_token: str = Field(..., json_schema_extra={"env": "BOT_TOKEN"})
    bot_prefix: str = Field(..., json_schema_extra={"env": "BOT_PREFIX"})
    bot_channels: List[str] = Field(
        default_factory=lambda: ["bot-test", "chat"],
        json_schema_extra={"env": "BOT_CHANNELS"},
    )
    bot_context: int = Field(50, json_schema_extra={"env": "BOT_CONTEXT"})
    bot_message_limit: int = Field(2, json_schema_extra={"env": "BOT_MESSAGE_LIMIT"})

    chatgpt_user_specified_middle_section: str = Field(
        ...,
        json_schema_extra={"env": "CHATGPT_USER_SPECIFIED_MIDDLE_SECTION"},
    )
    chatgpt_api_key: str = Field(..., json_schema_extra={"env": "CHATGPT_API_KEY"})
    chatgpt_model: str = Field(..., json_schema_extra={"env": "CHATGPT_MODEL"})
    chatgpt_prompt_prefix: str = Field(..., json_schema_extra={"env": "CHATGPT_PROMPT_PREFIX"})
    chatgpt_prompt_suffix: str = Field(..., json_schema_extra={"env": "CHATGPT_PROMPT_SUFFIX"})

    openai_image_model: str = Field("dall-e-3", json_schema_extra={"env": "OPENAI_IMAGE_MODEL"})
    openai_image_size: str = Field("1024x1024", json_schema_extra={"env": "OPENAI_IMAGE_SIZE"})

    target_user_id: Optional[int] = Field(None, json_schema_extra={"env": "DM_USER_ID"})
    minutes_between_messages: Optional[int] = Field(
        None, json_schema_extra={"env": "MINUTES_BETWEEN_MESSAGES"}
    )
    url_to_fetch: Optional[str] = Field(None, json_schema_extra={"env": "URL_TO_FETCH"})
    url_to_fetch2: Optional[str] = Field(None, json_schema_extra={"env": "URL_TO_FETCH2"})

    http_timeout_seconds: int = Field(45, json_schema_extra={"env": "HTTP_TIMEOUT_SECONDS"})

    @field_validator("bot_channels", mode="before")
    @classmethod
    def _split_channels(cls, value: str | Sequence[str] | None) -> List[str]:
        return cls._parse_channels(value)

    @field_validator(
        "bot_message_limit",
        "bot_context",
        "minutes_between_messages",
        "target_user_id",
        mode="before",
    )
    @classmethod
    def _coerce_ints(cls, value):
        if value is None or value == "":
            return value
        if isinstance(value, int):
            return value
        return int(value)

    @staticmethod
    def _parse_channels(value: str | Sequence[str] | None) -> List[str]:
        if value is None:
            return []
        if isinstance(value, str):
            try:
                loaded = json.loads(value)
                if isinstance(loaded, list):
                    value = loaded
            except json.JSONDecodeError:
                return [channel.strip() for channel in value.split(",") if channel.strip()]

        if isinstance(value, Sequence) and not isinstance(value, str):
            return [str(channel).strip() for channel in value if str(channel).strip()]

        return [str(value).strip()] if str(value).strip() else []

    @property
    def notifier_enabled(self) -> bool:
        """Return True when all fields required for DM notifications are configured."""
        required_values = [
            self.target_user_id,
            self.minutes_between_messages,
            self.url_to_fetch,
            self.url_to_fetch2,
        ]
        return all(value is not None for value in required_values)

    @classmethod
    def settings_customise_sources(
        cls,
        settings_cls,
        init_settings,
        env_settings,
        dotenv_settings,
        file_secret_settings,
    ):
        return (
            init_settings,
            LenientEnvSettingsSource(
                settings_cls,
                case_sensitive=env_settings.case_sensitive,
                env_prefix=env_settings.env_prefix,
                env_nested_delimiter=getattr(env_settings, "env_nested_delimiter", None),
                env_nested_max_split=getattr(env_settings, "env_nested_max_split", None),
                env_ignore_empty=env_settings.env_ignore_empty,
                env_parse_none_str=env_settings.env_parse_none_str,
                env_parse_enums=env_settings.env_parse_enums,
            ),
            dotenv_settings,
            file_secret_settings,
        )


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    """Return a cached instance of Settings."""

    return Settings()
