using System.Collections.Generic;
using DiscordSky.Bot.Integrations.OpenAI;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Tests;

public class OpenAiResponseParserTests
{
    [Fact]
    public void TryParseSendDiscordMessageCall_ReturnsPayload()
    {
        var response = new OpenAiResponse
        {
            Output =
            [
                new OpenAiResponseOutputItem
                {
                    Type = "message",
                    Content = new List<OpenAiResponseOutputContent>
                    {
                        new()
                        {
                            Type = "tool_call",
                            Name = OpenAiTooling.SendDiscordMessageToolName,
                            Arguments = "{\"mode\":\"reply\",\"target_message_id\":\"12345\",\"text\":\"Howdy\"}"
                        }
                    }
                }
            ]
        };

        var parsed = OpenAiResponseParser.TryParseSendDiscordMessageCall(response, out var payload);

        Assert.True(parsed);
        Assert.NotNull(payload);
        Assert.Equal("reply", payload!.Mode);
        Assert.Equal((ulong)12345, payload.TargetMessageId);
        Assert.Equal("Howdy", payload.Text);
    }

    [Fact]
    public void TryParseSendDiscordMessageCall_HandlesFunctionCallOutput()
    {
        var response = new OpenAiResponse
        {
            Output =
            [
                new OpenAiResponseOutputItem
                {
                    Type = "function_call",
                    Name = OpenAiTooling.SendDiscordMessageToolName,
                    Arguments = "{\"mode\":\"broadcast\",\"target_message_id\":null,\"text\":\"Hello world\"}"
                }
            ]
        };

        var parsed = OpenAiResponseParser.TryParseSendDiscordMessageCall(response, out var payload);

        Assert.True(parsed);
        Assert.NotNull(payload);
        Assert.Equal("broadcast", payload!.Mode);
        Assert.Null(payload.TargetMessageId);
        Assert.Equal("Hello world", payload.Text);
    }

    [Fact]
    public void TryParseSendDiscordMessageCall_ReturnsFalseWithoutToolCall()
    {
        var response = new OpenAiResponse
        {
            Output =
            [
                new OpenAiResponseOutputItem
                {
                    Type = "message",
                    Content =
                    [
                        new OpenAiResponseOutputContent
                        {
                            Type = "output_text",
                            Text = "Hello friends!"
                        }
                    ]
                }
            ]
        };

        var parsed = OpenAiResponseParser.TryParseSendDiscordMessageCall(response, out var payload);

        Assert.False(parsed);
        Assert.Null(payload);
    }
}
