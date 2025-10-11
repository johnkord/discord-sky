from __future__ import annotations

from dataclasses import dataclass
from types import SimpleNamespace
from typing import List, Sequence

import pytest

from discord_sky.config import Settings
from discord_sky.context import (
    ConversationContextBuilder,
    ToolContextProvider,
    ToolResult,
)


@dataclass
class FakeHistoryMessage:
    content: str
    author: SimpleNamespace


class FakeChannel:
    def __init__(self, history_messages: Sequence[FakeHistoryMessage]):
        self._messages = list(history_messages)

    def history(self, limit: int):
        async def iterator():
            for message in reversed(self._messages[:limit]):
                yield message

        return iterator()


class FakeToolProvider(ToolContextProvider):
    def __init__(self, results: Sequence[ToolResult]):
        self._results = list(results)
        self.calls: List[SimpleNamespace] = []

    async def gather(self, message):  # type: ignore[override]
        self.calls.append(message)
        return list(self._results)


@pytest.fixture()
def settings() -> Settings:
    return Settings(
        bot_token="token",
        bot_prefix="!react",
        bot_channels=["bot-test"],
        bot_context=5,
        bot_message_limit=2,
        chatgpt_user_specified_middle_section="default",
        chatgpt_api_key="key",
        chatgpt_model="gpt-test",
        chatgpt_prompt_prefix="prefix",
        chatgpt_prompt_suffix="suffix",
        openai_image_model="image-model",
        openai_image_size="256x256",
    )


@pytest.mark.asyncio()
async def test_builder_collects_history(settings: Settings) -> None:
    human_author = SimpleNamespace(name="person", bot=False, id=1)
    bot_author = SimpleNamespace(name="bot", bot=True, id=2)

    history = [
        FakeHistoryMessage(content="!react(previous)", author=human_author),
        FakeHistoryMessage(content="short", author=human_author),
        FakeHistoryMessage(content="meaningful", author=human_author),
        FakeHistoryMessage(content="human message", author=human_author),
        FakeHistoryMessage(content="human message", author=human_author),
        FakeHistoryMessage(content="bot reply\n", author=bot_author),
    ]
    channel = FakeChannel(history)

    builder = ConversationContextBuilder(settings)
    builder.set_bot_user(bot_author.id)

    message = SimpleNamespace(channel=channel)
    context = await builder.build(message, "topic")

    history_contents = [msg.content for msg in context.history]
    assert history_contents == ["short", "meaningful", "human message", "human message"]
    prompt = context.render_prompt()
    assert "meaningful" in prompt and "bot reply" not in prompt
    assert "!react" not in " ".join(history_contents)


@pytest.mark.asyncio()
async def test_builder_enforces_character_budget(settings: Settings) -> None:
    author = SimpleNamespace(name="person", bot=False, id=1)
    long_content = "x" * 5000

    history = [FakeHistoryMessage(content=long_content, author=author) for _ in range(5)]
    channel = FakeChannel(history)

    builder = ConversationContextBuilder(settings, history_character_limit=5500)
    message = SimpleNamespace(channel=channel)

    context = await builder.build(message, "topic")

    # Only one message should fit in the budget beyond the base prompt
    assert len(context.history) == 1


@pytest.mark.asyncio()
async def test_builder_includes_tool_results(settings: Settings) -> None:
    author = SimpleNamespace(name="person", bot=False, id=1)
    history = [FakeHistoryMessage(content="hello", author=author)]
    channel = FakeChannel(history)
    tool_provider = FakeToolProvider([ToolResult(name="search", content="result text")])

    builder = ConversationContextBuilder(settings)
    builder.add_tool_provider(tool_provider)
    message = SimpleNamespace(channel=channel)

    context = await builder.build(message, "topic")

    assert context.tool_results == (ToolResult(name="search", content="result text"),)
    assert tool_provider.calls == [message]
    assert "[Tool:search]" in context.render_prompt()