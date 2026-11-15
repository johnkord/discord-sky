using System.Text.Json;
using DiscordSky.Bot.Orchestration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.AI;

namespace DiscordSky.Tests;

public class ToolCallParsingTests
{
    [Theory]
    [InlineData("{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\",\"target_message_id\":\"99999\",\"text\":\"My decree stands.\"}}")]
    [InlineData("```json\n{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"broadcast\",\"text\":\"My decree stands.\"}}\n```")]
    [InlineData("```JSON\r\n{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\",\"text\":\"My decree stands.\"}}\r\n```")]
    [InlineData("```\n{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\",\"text\":\"My decree stands.\"}}\n```")]
    [InlineData("{\"function\":{\"name\":\"send_discord_message\",\"arguments\":\"{\\\"mode\\\":\\\"reply\\\",\\\"text\\\":\\\"My decree stands.\\\"}\"}}")]
    [InlineData("[{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\",\"text\":\"My decree stands.\"}}]")]
    [InlineData("{\"tool_calls\":[{\"function\":{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\",\"text\":\"My decree stands.\"}}}]}")]
    public void TextualSendEnvelope_RecoversOnlyReplyText(string response)
    {
        var text = CreativeOrchestrator.NormalizeFallbackReply(response, out var envelope);
        Assert.True(envelope);
        Assert.Equal("My decree stands.", text);
    }

    [Theory]
    [InlineData("{\"name\":\"recall_about_user\",\"arguments\":{\"user_id\":\"123\"}}")]
    [InlineData("{\"name\":\"generate_image\",\"arguments\":{\"image_prompt\":\"a tower\"}}")]
    [InlineData("{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\"}}")]
    [InlineData("{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\",\"text\":{\"name\":\"get_member\"}}}")]
    [InlineData("{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\",\"text\":\"unfinished")]
    [InlineData("{\"function\":{\"name\":\"send_discord_message\",\"arguments\":\"broken\"}}")]
    [InlineData("{\"tool_calls\":[{\"name\":\"send_discord_message\"},{\"name\":\"generate_image\"}]}")]
    [InlineData("{\"type\":\"function_call\",\"name\":\"create_role\",\"arguments\":{}}")]
    public void OtherOrInvalidToolEnvelopes_AreNotPublished(string response)
    {
        Assert.Empty(CreativeOrchestrator.NormalizeFallbackReply(response, out var envelope));
        Assert.True(envelope);
    }

    [Theory]
    [InlineData("A perfectly ordinary proclamation.")]
    [InlineData("{\"name\":\"build\",\"arguments\":[\"release\"]}")]
    [InlineData("```json\n{\"score\":42}\n```")]
    [InlineData("[{\"name\":\"build\",\"arguments\":[\"release\"]}]")]
    [InlineData("[{\"score\":1},{\"score\":2}]")]
    [InlineData("Some prose explaining {\"name\":\"send_discord_message\"} as an example.")]
    public void NormalRepliesAndUserFacingJson_ArePreserved(string response)
    {
        Assert.Equal(response, CreativeOrchestrator.NormalizeFallbackReply(response, out var envelope));
        Assert.False(envelope);
    }

    [Theory]
    [InlineData(CreativeInvocationKind.Mention)]
    [InlineData(CreativeInvocationKind.DirectReply)]
    public void RecoveredEnvelope_CannotRedirectTheReply(CreativeInvocationKind kind)
    {
        var request = new CreativeRequest("Robotnik", "topic", "member", 1, 2, 3, DateTimeOffset.UtcNow,
            InvocationKind: kind, TriggerMessageId: 42);
        var text = CreativeOrchestrator.NormalizeFallbackReply(
            "{\"name\":\"send_discord_message\",\"arguments\":{\"mode\":\"reply\",\"target_message_id\":\"99999\",\"text\":\"My decree stands.\"}}",
            out var envelope);
        var result = CreativeOrchestrator.BuildProviderFallback(request, text);
        Assert.True(envelope);
        Assert.Equal("My decree stands.", result.PrimaryMessage);
        Assert.Equal((ulong)42, result.ReplyToMessageId);
        Assert.Null(result.AttachmentBytes);
    }

    [Fact]
    public void TryParseToolCallArguments_ReplyMode_ExtractsAllFields()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.SendDiscordMessageToolName,
            new Dictionary<string, object?>
            {
                ["mode"] = "reply",
                ["text"] = "Howdy",
                ["target_message_id"] = "12345"
            });

        var result = CreativeOrchestrator.TryParseToolCallArguments(fc, out var mode, out var text, out var targetId);

        Assert.True(result);
        Assert.Equal("reply", mode);
        Assert.Equal("Howdy", text);
        Assert.Equal((ulong)12345, targetId);
    }

    [Fact]
    public void TryParseToolCallArguments_BroadcastMode_NullTarget()
    {
        var fc = new FunctionCallContent("call-2", CreativeOrchestrator.SendDiscordMessageToolName,
            new Dictionary<string, object?>
            {
                ["mode"] = "broadcast",
                ["text"] = "Hello world",
                ["target_message_id"] = null
            });

        var result = CreativeOrchestrator.TryParseToolCallArguments(fc, out var mode, out var text, out var targetId);

        Assert.True(result);
        Assert.Equal("broadcast", mode);
        Assert.Equal("Hello world", text);
        Assert.Null(targetId);
    }

    [Fact]
    public void TryParseToolCallArguments_MissingMode_ReturnsFalse()
    {
        var fc = new FunctionCallContent("call-3", CreativeOrchestrator.SendDiscordMessageToolName,
            new Dictionary<string, object?>
            {
                ["text"] = "Orphaned text"
            });

        var result = CreativeOrchestrator.TryParseToolCallArguments(fc, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseToolCallArguments_MissingText_ReturnsFalse()
    {
        var fc = new FunctionCallContent("call-4", CreativeOrchestrator.SendDiscordMessageToolName,
            new Dictionary<string, object?>
            {
                ["mode"] = "broadcast"
            });

        var result = CreativeOrchestrator.TryParseToolCallArguments(fc, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseToolCallArguments_InvalidMode_ReturnsFalse()
    {
        var fc = new FunctionCallContent("call-5", CreativeOrchestrator.SendDiscordMessageToolName,
            new Dictionary<string, object?>
            {
                ["mode"] = "whisper",
                ["text"] = "Secret message"
            });

        var result = CreativeOrchestrator.TryParseToolCallArguments(fc, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseToolCallArguments_NullArguments_ReturnsFalse()
    {
        var fc = new FunctionCallContent("call-6", CreativeOrchestrator.SendDiscordMessageToolName);

        var result = CreativeOrchestrator.TryParseToolCallArguments(fc, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseToolCallArguments_JsonElementValues_Parses()
    {
        // Simulate arguments that arrive as JsonElement (common from M.E.AI deserialization)
        var json = JsonDocument.Parse("""{"mode":"reply","text":"JSON text","target_message_id":"99999"}""");
        var root = json.RootElement;

        var args = new Dictionary<string, object?>
        {
            ["mode"] = root.GetProperty("mode"),
            ["text"] = root.GetProperty("text"),
            ["target_message_id"] = root.GetProperty("target_message_id")
        };

        var fc = new FunctionCallContent("call-7", CreativeOrchestrator.SendDiscordMessageToolName, args);

        var result = CreativeOrchestrator.TryParseToolCallArguments(fc, out var mode, out var text, out var targetId);

        Assert.True(result);
        Assert.Equal("reply", mode);
        Assert.Equal("JSON text", text);
        Assert.Equal((ulong)99999, targetId);
    }

    [Fact]
    public void TryParseToolCallArguments_NumericTargetId_Parses()
    {
        var fc = new FunctionCallContent("call-8", CreativeOrchestrator.SendDiscordMessageToolName,
            new Dictionary<string, object?>
            {
                ["mode"] = "reply",
                ["text"] = "Numeric target",
                ["target_message_id"] = 67890L
            });

        var result = CreativeOrchestrator.TryParseToolCallArguments(fc, out var mode, out var text, out var targetId);

        Assert.True(result);
        Assert.Equal("reply", mode);
        Assert.Equal("Numeric target", text);
        Assert.Equal((ulong)67890, targetId);
    }
}
