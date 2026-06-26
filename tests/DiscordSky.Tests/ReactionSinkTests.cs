using System.Text.Json;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

/// <summary>
/// Tests for the reaction sink (fun_assessment_2026-06-25 P1): the durable reception signal.
/// </summary>
public class ReactionSinkTests
{
    private static ReactionEvent Sample(string action = "add", string emote = "joy") =>
        new(DateTimeOffset.UtcNow, action, emote, 111UL, 222UL, 333UL, 444UL, "Robotnik from AOSTH", "Applaud at once!");

    private static IOptions<ReactionOptions> Opt(ReactionOptions o) => Options.Create(o);
    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), "rxtest-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Disabled_WritesNothing()
    {
        var dir = NewTempDir();
        var sink = new FileBackedReactionSink(
            Opt(new ReactionOptions { Enabled = false, BaseDirectory = dir }),
            NullLogger<FileBackedReactionSink>.Instance);

        sink.Record(Sample());

        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
    }

    [Fact]
    public void Enabled_WritesJsonlThatRoundTrips()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var sink = new FileBackedReactionSink(
            Opt(new ReactionOptions { Enabled = true, BaseDirectory = dir }),
            NullLogger<FileBackedReactionSink>.Instance);

        sink.Record(Sample(emote: "thumbsup"));

        var file = Directory.GetFiles(dir, "reactions-*.jsonl").Single();
        var line = File.ReadAllLines(file).Single();
        var parsed = JsonSerializer.Deserialize<ReactionEvent>(line);

        Assert.NotNull(parsed);
        Assert.Equal("thumbsup", parsed!.Emote);
        Assert.Equal(444UL, parsed.MessageId);
        Assert.Equal("Robotnik from AOSTH", parsed.Persona);
        // JSON property names use the snake_case JsonPropertyName attributes (joinable by message_id).
        Assert.Contains("\"emote\":\"thumbsup\"", line);
        Assert.Contains("\"message_id\":444", line);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void NoOpSink_DoesNotThrow()
    {
        new NoOpReactionSink().Record(Sample());
    }
}
