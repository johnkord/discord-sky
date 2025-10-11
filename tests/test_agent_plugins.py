from __future__ import annotations

from types import MethodType, SimpleNamespace
from typing import List

import pytest

from discord_sky.agent import AgentPlugin, BotAgent
from discord_sky.config import Settings
from discord_sky.plugins import ChatPlugin, ImagePlugin, NotifierPlugin


class DummyChannel:
    def __init__(self, name: str = "bot-test") -> None:
        self.name = name
        self.sent: List[str] = []

    async def send(self, content: str, **kwargs) -> None:  # pragma: no cover - exercised in tests
        self.sent.append(content)


class DummyAttachment:
    def __init__(self, filename: str, url: str = "https://example.com/image.png") -> None:
        self.filename = filename
        self.url = url


class DummyMessage:
    def __init__(self, *, content: str, channel: DummyChannel, attachments=None, author=None, message_id: int = 1):
        self.content = content
        self.channel = channel
        self.attachments = attachments or []
        self.author = author or SimpleNamespace(id=123, bot=False, name="tester")
        self.id = message_id


class FakeResponder:
    def __init__(self) -> None:
        self.bound_user = None
        self.calls: List[str] = []

    def set_bot_user(self, user_id: int) -> None:
        self.bound_user = user_id

    async def respond(self, message: DummyMessage, middle_section: str) -> str:  # pragma: no cover - exercised in tests
        self.calls.append(middle_section)
        return f"response:{middle_section}"


class FakeImagePipeline:
    def __init__(self) -> None:
        self.calls: List[str] = []

    async def handle_message(self, message: DummyMessage, reference_url: str) -> None:  # pragma: no cover - exercised
        self.calls.append(reference_url)


class DummyAgent:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings
        self.client = SimpleNamespace(user=SimpleNamespace(id=999))


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


@pytest.mark.asyncio
async def test_chat_plugin_handles_prefixed_message(settings: Settings) -> None:
    responder = FakeResponder()
    plugin = ChatPlugin(settings, responder)
    agent = DummyAgent(settings)

    await plugin.on_ready(agent)

    channel = DummyChannel()
    message = DummyMessage(content="!react(pizza)", channel=channel)

    handled = await plugin.handle_message(agent, message)

    assert handled is True
    assert channel.sent == ["response:pizza"]
    assert responder.bound_user == 999
    assert responder.calls == ["pizza"]


@pytest.mark.asyncio
async def test_chat_plugin_ignores_unlisted_channel(settings: Settings) -> None:
    responder = FakeResponder()
    plugin = ChatPlugin(settings, responder)
    agent = DummyAgent(settings)

    await plugin.on_ready(agent)

    channel = DummyChannel(name="general")
    message = DummyMessage(content="!react(hi)", channel=channel)

    handled = await plugin.handle_message(agent, message)

    assert handled is False
    assert channel.sent == []
    assert responder.calls == []


@pytest.mark.asyncio
async def test_image_plugin_handles_first_image(settings: Settings) -> None:
    pipeline = FakeImagePipeline()
    plugin = ImagePlugin(pipeline)

    channel = DummyChannel()
    attachments = [DummyAttachment("photo.png"), DummyAttachment("other.txt")]
    message = DummyMessage(content="draw this", channel=channel, attachments=attachments)

    handled = await plugin.handle_message(DummyAgent(settings), message)

    assert handled is True
    assert pipeline.calls == ["https://example.com/image.png"]


@pytest.mark.asyncio
async def test_agent_respects_plugin_priority(settings: Settings) -> None:
    results: List[str] = []

    class FirstPlugin(AgentPlugin):
        name = "first"
        priority = 10

        async def handle_message(self, agent: BotAgent, message) -> bool:
            results.append("first")
            return True

    class SecondPlugin(AgentPlugin):
        name = "second"
        priority = 20

        async def handle_message(self, agent: BotAgent, message) -> bool:
            results.append("second")
            return False

    agent = BotAgent(settings=settings)
    await agent.add_plugin(SecondPlugin())
    await agent.add_plugin(FirstPlugin())

    async def fake_process_commands(self, message):  # type: ignore[override]
        return None

    agent.client.process_commands = MethodType(fake_process_commands, agent.client)

    message = DummyMessage(content="hi", channel=DummyChannel())
    await agent._on_message(message)  # type: ignore[arg-type]

    assert results == ["first"]

    await agent.close()


@pytest.mark.asyncio
async def test_notifier_plugin_ignored_when_disabled(settings: Settings) -> None:
    plugin = NotifierPlugin(settings, scraper=SimpleNamespace())
    agent = DummyAgent(settings)

    await plugin.on_loaded(agent)
    assert plugin._task is None

    await plugin.on_ready(agent)
    await plugin.on_shutdown(agent)