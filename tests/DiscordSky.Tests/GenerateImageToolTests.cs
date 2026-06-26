using System.Text.Json;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;

namespace DiscordSky.Tests;

/// <summary>
/// Argument-parsing tests for the generate_image tool. Mirrors <see cref="RecallToolLoopTests"/>: the full
/// loop needs a SocketCommandContext, so we cover the pure surface (prompt extraction) directly. The
/// generate-then-attach and force-send behavior is exercised by the build and the ImageToolService tests.
/// </summary>
public class GenerateImageToolTests
{
    [Fact]
    public void ParseImagePrompt_ExtractsPrompt()
    {
        var call = MakeCall("generate_image", new { image_prompt = "a giant golden statue of my face" });
        Assert.Equal("a giant golden statue of my face", CreativeOrchestrator.ParseImagePrompt(call));
    }

    [Fact]
    public void ParseImagePrompt_TrimsWhitespace()
    {
        var call = MakeCall("generate_image", new { image_prompt = "  an egg-shaped fortress  " });
        Assert.Equal("an egg-shaped fortress", CreativeOrchestrator.ParseImagePrompt(call));
    }

    [Fact]
    public void ParseImagePrompt_MissingField_ReturnsEmpty()
    {
        var call = MakeCall("generate_image", new { not_the_prompt = "x" });
        Assert.Equal(string.Empty, CreativeOrchestrator.ParseImagePrompt(call));
    }

    [Fact]
    public void ParseImagePrompt_EmptyArgs_ReturnsEmpty()
    {
        var call = new FunctionCallContent("c1", "generate_image", new Dictionary<string, object?>());
        Assert.Equal(string.Empty, CreativeOrchestrator.ParseImagePrompt(call));
    }

    private static FunctionCallContent MakeCall(string name, object args)
    {
        var json = JsonSerializer.Serialize(args);
        var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value;
        }
        return new FunctionCallContent("call-" + Guid.NewGuid().ToString("N")[..8], name, dict);
    }
}
