using System.Text.Json;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;

namespace DiscordSky.Tests;

public class ToolCallParsingTests
{
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
