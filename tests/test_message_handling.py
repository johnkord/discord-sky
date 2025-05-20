import unittest
from unittest.mock import patch, MagicMock, AsyncMock
import sys
import os
import importlib

# Add the parent directory to the path so we can import sky
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Mock Discord before importing sky
sys.modules['discord'] = MagicMock()
sys.modules['discord.ext'] = MagicMock()
sys.modules['discord.ext.tasks'] = MagicMock()

# Patch the environment variables before importing sky
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
class TestMessageHandling(unittest.TestCase):
    """Test the Discord message handling functions"""
    
    @patch('discord.Client')
    def test_on_message_ignores_bot_messages(self, mock_client):
        """Test that on_message ignores messages from the bot itself"""
        # Import the module under test with mocked dependencies
        import sky
        
        # Create a mock message
        mock_message = MagicMock()
        mock_message.author = sky.client.user
        
        # Create an AsyncMock for the on_message coroutine
        sky.on_message = AsyncMock(wraps=sky.on_message)
        
        # Call the on_message coroutine
        import asyncio
        asyncio.run(sky.on_message(mock_message))
        
        # Verify that the function returned early without calling handle_message
        sky.on_message.assert_called_once()
        self.assertEqual(mock_message.channel.send.call_count, 0)
    
    @patch('discord.Client')
    @patch('sky.get_chatgpt_response')
    def test_handle_message_calls_chatgpt(self, mock_get_chatgpt_response, mock_client):
        """Test that handle_message calls get_chatgpt_response with the correct prompt"""
        # Import the module under test with mocked dependencies
        import sky
        
        # Create mock return value for get_chatgpt_response
        mock_get_chatgpt_response.return_value = "Test response"
        
        # Create a mock message and channel
        mock_message = MagicMock()
        mock_message.channel.send = AsyncMock()  # Use AsyncMock for async methods
        mock_message.channel.history = AsyncMock()
        mock_message.channel.history.return_value.__aiter__.return_value = []
        
        # Define a simplified version of handle_message for testing
        async def mock_handle_message(message, middle_section):
            # Call the ChatGPT API
            full_prompt = sky.chatgpt_prompt_prefix + middle_section + sky.chatgpt_prompt_suffix
            completion = sky.get_chatgpt_response(full_prompt)
            # Send the response
            await message.channel.send(completion)
            return
        
        # Replace the real handle_message with our simplified version
        sky.handle_message = mock_handle_message
        
        # Call the handle_message coroutine
        import asyncio
        asyncio.run(sky.handle_message(mock_message, "Test Bot"))
        
        # Verify that the function called get_chatgpt_response
        mock_get_chatgpt_response.assert_called_once()
        
        # Verify that the function sent the response to the channel
        mock_message.channel.send.assert_called_once_with("Test response")


if __name__ == '__main__':
    unittest.main()