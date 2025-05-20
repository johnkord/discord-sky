"""
Pytest configuration file.
"""
import pytest
import os
import sys

# Add the parent directory to the path so we can import sky
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

@pytest.fixture
def mock_env_variables():
    """Fixture to mock common environment variables used in tests."""
    env_vars = {
        "BOT_CHANNELS": "test-channel",
        "BOT_CONTEXT": "50",
        "BOT_PREFIX": "!test",
        "BOT_TOKEN": "test-token",
        "CHATGPT_API_KEY": "test-api-key",
        "CHATGPT_MODEL": "test-model",
        "CHATGPT_PROMPT_PREFIX": "You are ",
        "CHATGPT_PROMPT_SUFFIX": ", respond to the following:",
        "CHATGPT_USER_SPECIFIED_MIDDLE_SECTION": "Test Bot",
        "DM_HOUR_TO_NOTIFY": "7",
        "DM_USER_ID": "123456789"
    }
    
    return env_vars