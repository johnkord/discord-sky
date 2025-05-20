import unittest
from unittest.mock import patch, MagicMock
import sys
import os
import json
import importlib

# Add the parent directory to the path so we can import sky
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Mock Discord before importing sky
sys.modules['discord'] = MagicMock()
sys.modules['discord.ext'] = MagicMock()
sys.modules['discord.ext.tasks'] = MagicMock()

# Set required environment variables
@patch.dict(os.environ, {
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
}, clear=False)
class TestSkyEnvironmentVariables(unittest.TestCase):
    """Test the environment variable handling in sky.py"""

    @patch.dict(os.environ, {"BOT_CHANNELS": "test-channel"})
    def test_bot_channels_from_env(self):
        """Test that BOT_CHANNELS is correctly read from environment variables."""
        # We need to import inside the test to ensure patched environment variables are used
        import sky
        # Reload sky module to ensure environment variables are re-read
        importlib.reload(sky)
        
        self.assertEqual(sky.bot_channels, ["test-channel"])
    
    @patch.dict(os.environ, {}, clear=True)
    @patch.dict(os.environ, {
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
    })
    def test_bot_channels_default(self):
        """Test that BOT_CHANNELS has a default value when not in environment."""
        import sky
        importlib.reload(sky)
        
        self.assertEqual(sky.bot_channels, ["bot-test", "chat"])
    
    @patch.dict(os.environ, {"BOT_PREFIX": "!test"})
    def test_bot_prefix_from_env(self):
        """Test that BOT_PREFIX is correctly read from environment variables."""
        import sky
        importlib.reload(sky)
        
        self.assertEqual(sky.bot_prefix, "!test")


class TestChatGPTResponse(unittest.TestCase):
    """Test the ChatGPT response functionality."""

    @patch('requests.post')
    @patch.dict(os.environ, {
        "BOT_CHANNELS": "test-channel",
        "BOT_CONTEXT": "50",
        "BOT_PREFIX": "!test",
        "BOT_TOKEN": "test-token",
        "CHATGPT_API_KEY": "test-key",
        "CHATGPT_MODEL": "test-model",
        "CHATGPT_PROMPT_PREFIX": "You are ",
        "CHATGPT_PROMPT_SUFFIX": ", respond to the following:",
        "CHATGPT_USER_SPECIFIED_MIDDLE_SECTION": "Test Bot",
        "DM_HOUR_TO_NOTIFY": "7",
        "DM_USER_ID": "123456789"
    })
    def test_get_chatgpt_response_success(self, mock_post):
        """Test the get_chatgpt_response function with a successful API response."""
        # Create a mock response for the API call
        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            'choices': [{'message': {'content': 'Test response'}}]
        }
        mock_post.return_value = mock_response
        
        # Import the module and test the function
        import sky
        importlib.reload(sky)
        response = sky.get_chatgpt_response("Test prompt")
        
        # Verify the response
        self.assertEqual(response, 'Test response')
        mock_post.assert_called_once()
    
    @patch('requests.post')
    @patch.dict(os.environ, {
        "BOT_CHANNELS": "test-channel",
        "BOT_CONTEXT": "50",
        "BOT_PREFIX": "!test",
        "BOT_TOKEN": "test-token",
        "CHATGPT_API_KEY": "test-key",
        "CHATGPT_MODEL": "test-model",
        "CHATGPT_PROMPT_PREFIX": "You are ",
        "CHATGPT_PROMPT_SUFFIX": ", respond to the following:",
        "CHATGPT_USER_SPECIFIED_MIDDLE_SECTION": "Test Bot",
        "DM_HOUR_TO_NOTIFY": "7",
        "DM_USER_ID": "123456789"
    })
    def test_get_chatgpt_response_error(self, mock_post):
        """Test the get_chatgpt_response function with an error from the API."""
        # Create a mock response for the API call
        mock_response = MagicMock()
        mock_response.status_code = 400
        mock_response.text = "Error message"
        mock_post.return_value = mock_response
        
        # Import the module and test the function
        import sky
        importlib.reload(sky)
        response = sky.get_chatgpt_response("Test prompt")
        
        # Verify the response is an error message for API errors
        self.assertTrue(response.startswith("Sorry, I encountered an error"))
        self.assertIn("400", response)
        mock_post.assert_called_once()


if __name__ == '__main__':
    unittest.main()